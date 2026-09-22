/* GlassLink module firmware - main: serial GUID, display bring-up, USB link, protocol loop, OTA.
 *
 * Protocol: docs/usb-protocol.md. The module sends READY, the host answers with the newest JPEG FRAME,
 * the module decodes it with the hardware JPEG engine, shows it, and sends READY again.
 */
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_system.h"
#include "esp_ota_ops.h"
#include "esp_rom_crc.h"
#include "esp_app_desc.h"
#include "esp_heap_caps.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "proto.h"
#include "usb_link.h"
#include "display.h"
#include "ident.h"

static const char *TAG = "main";

#ifndef FW_VERSION              /* both come from CMake: ../VERSION and git describe */
#define FW_VERSION "0.0.0"
#endif
#ifndef FW_BUILD
#define FW_BUILD "nogit"
#endif
#define HW_NAME "p4-nano+lt8912b"
#define RX_BUF_SIZE (512 * 1024)     /* one JPEG frame (768x768 q85 is 30-60 KB; allow headroom) */

static char s_serial[33];
static char s_label[32];    /* human-readable unit name sent by the host with SHOW_IDENT */   /* 24 hex chars: TinyUSB/Windows show at most 31 chars of a string descriptor */
static uint8_t *s_rx;               /* frame receive buffer (PSRAM) */
static uint8_t *s_last_jpeg;        /* copy of the last frame shown, redrawn after IDENT / idle screens (PSRAM) */
static uint32_t s_last_jpeg_len;
static SemaphoreHandle_t s_frame_mutex;
static uint32_t s_last_seq;
static uint32_t s_frames, s_dropped, s_decode_ms_acc, s_decode_n, s_draw_us_acc, s_rx_us_acc;
static int64_t s_last_frame_us, s_ident_until_us, s_last_stats_us;
static bool s_assigned;
static struct { esp_ota_handle_t h; const esp_partition_t *part; uint32_t expected; uint32_t got; uint32_t crc;
                int64_t last_us; bool active; } s_ota;

/* ---- serial GUID (NVS) --------------------------------------------------------------------- */
static void load_or_create_serial(void)
{
    nvs_handle_t nvs;
    ESP_ERROR_CHECK(nvs_open("module", NVS_READWRITE, &nvs));
    size_t len = sizeof(s_serial);
    if (nvs_get_str(nvs, "serial", s_serial, &len) != ESP_OK || strlen(s_serial) != 24) {
        for (int i = 0; i < 24; i += 8) {
            snprintf(s_serial + i, 9, "%08lx", (unsigned long)esp_random());
        }
        ESP_ERROR_CHECK(nvs_set_str(nvs, "serial", s_serial));
        ESP_ERROR_CHECK(nvs_commit(nvs));
        ESP_LOGI(TAG, "generated new serial %s", s_serial);
    }
    nvs_close(nvs);
}

static int nvs_get_int(const char *key, int def)
{
    nvs_handle_t nvs;
    int32_t v = def;
    if (nvs_open("module", NVS_READONLY, &nvs) == ESP_OK) {
        nvs_get_i32(nvs, key, &v);
        nvs_close(nvs);
    }
    return v;
}

static void nvs_set_int(const char *key, int v)
{
    nvs_handle_t nvs;
    if (nvs_open("module", NVS_READWRITE, &nvs) == ESP_OK) {
        nvs_set_i32(nvs, key, v);
        nvs_commit(nvs);
        nvs_close(nvs);
    }
}

/* ---- screens --------------------------------------------------------------------------------- */
static void show_idle_screen(const char *line1, const char *line2, uint32_t colour)
{
    display_info_t di = display_get_info();
    size_t n = (size_t)di.width * di.height * 3;
    uint8_t *buf = heap_caps_calloc(1, n, MALLOC_CAP_SPIRAM);
    if (!buf) return;
    int scale = di.width / 120;  /* ~6 px per glyph column at 768 wide */
    ident_draw_text(buf, di.width, di.height, 20, di.height / 2 - 60, scale, colour, line1);
    ident_draw_text(buf, di.width, di.height, 20, di.height / 2 + 20, scale / 2 > 0 ? scale / 2 : 1, 0x808080, line2);
    display_show_rgb(buf, di.width, di.height);
    free(buf);
}

/* ---- messaging ------------------------------------------------------------------------------- */
static bool send_msg(uint8_t type, const void *payload, uint32_t len, uint32_t seq, uint32_t arg)
{
    xd_header_t h = {{XD_MAGIC0, XD_MAGIC1}, XD_PROTO_VERSION, type, len, seq, arg};
    if (!usb_link_write((const uint8_t *)&h, sizeof(h), 500)) return false;
    if (len && !usb_link_write(payload, len, 2000)) return false;
    return true;
}

static void send_log(int level, const char *fmt, ...)
{
    char buf[200];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n > 0) send_msg(XD_T_LOG, buf, (uint32_t)n, 0, level);
}

static void send_info(void)
{
    display_info_t di = display_get_info();
    char buf[256];
    int n = snprintf(buf, sizeof(buf),
                     "{\"fw\":\"%s\",\"build\":\"%s\",\"hw\":\"%s\",\"panel\":[%d,%d],\"decoder\":\"hw\",\"uptime_s\":%lld,\"serial\":\"%s\",\"mode\":%d,\"ident\":%d,\"slot\":\"%s\"}",
                     FW_VERSION, FW_BUILD, HW_NAME, di.width, di.height, (long long)(esp_timer_get_time() / 1000000), s_serial, di.mode,
                     esp_timer_get_time() < s_ident_until_us ? 1 : 0,
                     esp_ota_get_running_partition() ? esp_ota_get_running_partition()->label : "?");
    send_msg(XD_T_INFO, buf, (uint32_t)n, 0, 0);
}

static void send_stats(void)
{
    int64_t now = esp_timer_get_time();
    double secs = (now - s_last_stats_us) / 1e6;
    char buf[160];
    int n = snprintf(buf, sizeof(buf), "{\"fps\":%.1f,\"decode_ms\":%.1f,\"draw_ms\":%.1f,\"rx_ms\":%.1f,\"dropped\":%lu,\"free_psram\":%u,\"ident\":%d}",
                     secs > 0 ? s_frames / secs : 0.0,
                     s_decode_n ? (double)s_decode_ms_acc / s_decode_n : 0.0,
                     s_decode_n ? (double)s_draw_us_acc / s_decode_n / 1000.0 : 0.0,
                     s_decode_n ? (double)s_rx_us_acc / s_decode_n / 1000.0 : 0.0,
                     (unsigned long)s_dropped, (unsigned)heap_caps_get_free_size(MALLOC_CAP_SPIRAM),
                     now < s_ident_until_us ? 1 : 0);
    send_msg(XD_T_STATS, buf, (uint32_t)n, 0, 0);
    s_frames = 0; s_decode_ms_acc = 0; s_decode_n = 0; s_draw_us_acc = 0; s_rx_us_acc = 0;
    s_last_stats_us = now;
}

static void send_ready(void)
{
    send_msg(XD_T_READY, NULL, 0, s_last_seq, 0);
}

/* ---- OTA ------------------------------------------------------------------------------------- */
/* Stop-and-wait transfer: every step is answered (OTA_PROGRESS with the byte count, or OTA_RESULT with an error),
 * because erasing and writing flash blocks this task and the host must not run ahead.
 * OTA_RESULT codes: 0 ok, 1 begin failed, 2 write failed, 3 image rejected, 4 CRC mismatch, 5 size mismatch,
 * 6 timed out, 7 data out of order. */
static void ota_fail(uint32_t code)
{
    if (s_ota.active) esp_ota_abort(s_ota.h);
    s_ota.active = false;
    display_set_overlay(NULL, NULL);
    ESP_LOGW(TAG, "OTA failed, code %lu at %lu/%lu bytes", (unsigned long)code, (unsigned long)s_ota.got, (unsigned long)s_ota.expected);
    send_msg(XD_T_OTA_RESULT, NULL, 0, 0, code);
}

static void ota_begin(uint32_t size)
{
    if (s_ota.active) esp_ota_abort(s_ota.h);
    s_ota.active = false;
    s_ota.part = esp_ota_get_next_update_partition(NULL);
    s_ota.expected = size; s_ota.got = 0; s_ota.crc = 0;
    ESP_LOGI(TAG, "OTA begin: %lu bytes into %s", (unsigned long)size, s_ota.part ? s_ota.part->label : "?");
    esp_err_t err = (s_ota.part && size && size <= s_ota.part->size) ? esp_ota_begin(s_ota.part, size, &s_ota.h) : ESP_FAIL;
    if (err != ESP_OK) {
        ota_fail(1);
        return;
    }
    s_ota.active = true;
    s_ota.last_us = esp_timer_get_time();
    display_set_overlay("UPDATING FIRMWARE", "DO NOT UNPLUG");
    if (s_last_jpeg_len && xSemaphoreTake(s_frame_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
        display_show_jpeg(s_last_jpeg, s_last_jpeg_len, NULL);
        xSemaphoreGive(s_frame_mutex);
    }
    send_msg(XD_T_OTA_PROGRESS, NULL, 0, 0, 0);
}

static void ota_data(const uint8_t *data, uint32_t len, uint32_t offset)
{
    if (!s_ota.active) return;
    if (offset != s_ota.got || s_ota.got + len > s_ota.expected) {
        ota_fail(7);
        return;
    }
    if (esp_ota_write(s_ota.h, data, len) != ESP_OK) {
        ota_fail(2);
        return;
    }
    s_ota.crc = esp_rom_crc32_le(s_ota.crc, data, len);
    s_ota.got += len;
    s_ota.last_us = esp_timer_get_time();
    send_msg(XD_T_OTA_PROGRESS, NULL, 0, 0, s_ota.got);
}

static void ota_end(uint32_t crc)
{
    if (!s_ota.active) return;
    if (s_ota.got != s_ota.expected) { ota_fail(5); return; }
    if (crc != s_ota.crc) { ota_fail(4); return; }
    esp_err_t err = esp_ota_end(s_ota.h);          /* validates the image */
    s_ota.active = false;
    if (err == ESP_OK) err = esp_ota_set_boot_partition(s_ota.part);
    if (err != ESP_OK) {
        display_set_overlay(NULL, NULL);
        ESP_LOGW(TAG, "OTA image rejected: %s", esp_err_to_name(err));
        send_msg(XD_T_OTA_RESULT, NULL, 0, 0, 3);
        return;
    }
    ESP_LOGI(TAG, "OTA complete (%lu bytes, crc %08lx): rebooting into %s", (unsigned long)s_ota.got, (unsigned long)crc, s_ota.part->label);
    send_msg(XD_T_OTA_RESULT, NULL, 0, 0, 0);
    vTaskDelay(pdMS_TO_TICKS(300));
    esp_restart();
}

/* ---- protocol loop --------------------------------------------------------------------------- */
static size_t read_exact(uint8_t *dst, size_t len, uint32_t timeout_ms)
{
    size_t got = 0;
    int64_t deadline = esp_timer_get_time() + (int64_t)timeout_ms * 1000;
    while (got < len) {
        size_t n = usb_link_read(dst + got, len - got, 50);
        got += n;
        if (!n && esp_timer_get_time() > deadline) break;
        if (!n) vTaskDelay(1);              /* never spin: a read that returns nothing must not starve the idle task */
    }
    return got;
}

static int64_t s_hdr_us;   /* when the last header arrived (for rx time accounting) */

static void handle_message(const xd_header_t *h, const uint8_t *payload)
{
    switch (h->type) {
    case XD_T_FRAME: {
        if (s_ota.active) { break; }        /* no frames while updating */
        uint32_t ms = 0;
        int64_t rx_done = esp_timer_get_time();
        xSemaphoreTake(s_frame_mutex, portMAX_DELAY);     /* the screen task may be redrawing the last frame */
        esp_err_t err = display_show_jpeg(payload, h->length, &ms);
        if (err == ESP_OK) {
            memcpy(s_last_jpeg, payload, h->length);
            s_last_jpeg_len = h->length;
        }
        xSemaphoreGive(s_frame_mutex);
        if (err == ESP_OK) {
            s_frames++; s_decode_ms_acc += ms; s_decode_n++;
            s_draw_us_acc += display_last_draw_us();
            s_rx_us_acc += (uint32_t)(rx_done - s_hdr_us);
            s_last_seq = h->seq; s_last_frame_us = esp_timer_get_time(); s_assigned = true;
        } else {
            s_dropped++;
            send_log(2, "decode failed seq %lu: %s", (unsigned long)h->seq, esp_err_to_name(err));
        }
        send_ready();
        break;
    }
    case XD_T_GET_INFO: send_info(); send_ready(); break;   /* a (re)connecting host learns we can take a frame */
    case XD_T_SET_BRIGHTNESS:
        display_set_brightness((int)h->arg);        /* follows the cockpit knob: not persisted */
        if (s_last_jpeg_len && esp_timer_get_time() - s_last_frame_us > 40000) {
            /* no frame is arriving right now: redraw the last one so the change shows at once */
            xSemaphoreTake(s_frame_mutex, portMAX_DELAY);
            display_show_jpeg(s_last_jpeg, s_last_jpeg_len, NULL);
            xSemaphoreGive(s_frame_mutex);
        }
        break;
    case XD_T_SET_ROTATION: display_set_rotation((int)h->arg); nvs_set_int("rotation", (int)h->arg); break;
    case XD_T_SHOW_IDENT:
        snprintf(s_label, sizeof(s_label), "%.*s", (int)(h->length < sizeof(s_label) - 1 ? h->length : sizeof(s_label) - 1), (const char *)payload);
        s_ident_until_us = h->arg ? esp_timer_get_time() + (int64_t)h->arg * 1000000 : 0;
        send_stats();                       /* so the host sees the new ident state immediately */
        break;
    case XD_T_PING: send_msg(XD_T_PONG, NULL, 0, 0, h->arg); break;
    case XD_T_SET_ASSIGNED:
        s_assigned = h->arg != 0;           /* the screen task switches between picture and NOT ASSIGNED */
        break;
    case XD_T_SET_MODE:
        if (h->arg <= 3) {                  /* the HDMI DU on another screen: takes effect after the restart */
            nvs_set_int("mode", (int)h->arg);
            send_log(1, "HDMI mode %u stored, restarting", (unsigned)h->arg);
            vTaskDelay(pdMS_TO_TICKS(300));
            esp_restart();
        }
        send_log(1, "HDMI mode %u unknown", (unsigned)h->arg);
        break;
    case XD_T_OTA_BEGIN: ota_begin(h->arg); break;
    case XD_T_OTA_DATA: ota_data(payload, h->length, h->arg); break;
    case XD_T_OTA_END: ota_end(h->arg); break;
    case XD_T_REBOOT: esp_restart(); break;
    default: send_log(1, "unknown message type 0x%02x", h->type); break;
    }
}

static bool header_valid(const xd_header_t *h)
{
    if (h->magic[0] != XD_MAGIC0 || h->magic[1] != XD_MAGIC1 || h->version != XD_PROTO_VERSION || h->length > RX_BUF_SIZE) {
        return false;
    }
    switch (h->type) {   /* known host->module types only, so JPEG data cannot fake a header */
    case XD_T_FRAME: case XD_T_GET_INFO: case XD_T_SET_BRIGHTNESS: case XD_T_SET_ROTATION: case XD_T_SHOW_IDENT:
    case XD_T_PING: case XD_T_SET_ASSIGNED: case XD_T_SET_MODE: case XD_T_OTA_BEGIN: case XD_T_OTA_DATA: case XD_T_OTA_END: case XD_T_REBOOT:
        return true;
    default:
        return false;
    }
}

static void protocol_task(void *arg)
{
    bool was_connected = false;
    for (;;) {
        bool connected = usb_link_connected();
        if (connected && !was_connected) {
            ESP_LOGI(TAG, "usb configured");
            s_last_stats_us = esp_timer_get_time();
            vTaskDelay(pdMS_TO_TICKS(100));
            send_ready();
        }
        was_connected = connected;
        if (!connected) {
            vTaskDelay(pdMS_TO_TICKS(50));
            continue;
        }
        xd_header_t h;
        if (read_exact((uint8_t *)&h, sizeof(h), 250) != sizeof(h)) {
            if (s_ota.active && esp_timer_get_time() - s_ota.last_us > 15000000) {
                ota_fail(6);                 /* the host went away mid-update: give the flash slot back */
            }
            if (esp_timer_get_time() - s_last_stats_us > 2000000) {
                send_stats();
                /* Idle for 2 s: repeat READY so a host that missed it (or connected later) starts sending. */
                send_ready();
            }
            continue;
        }
        if (!header_valid(&h)) {
            /* Out of step with the host (it was restarted mid-message, so the tail of an old frame is still
             * coming). Slide the 16-byte window one byte at a time until a valid header lines up; draining
             * blindly does not work because the host keeps sending while we drain. */
            uint8_t *w = (uint8_t *)&h;
            size_t skipped = 0;
            int64_t deadline = esp_timer_get_time() + 3000000;
            bool ok = false;
            while (esp_timer_get_time() < deadline) {
                memmove(w, w + 1, sizeof(h) - 1);
                if (read_exact(w + sizeof(h) - 1, 1, 250) != 1) {
                    continue;
                }
                skipped++;
                if (header_valid(&h)) {
                    ok = true;
                    break;
                }
            }
            ESP_LOGW(TAG, "stream resync %s after %u bytes", ok ? "ok" : "failed", (unsigned)skipped);
            if (!ok) {
                send_ready();
                continue;
            }
        }
        s_hdr_us = esp_timer_get_time();
        if (h.length && read_exact(s_rx, h.length, 3000) != h.length) {
            ESP_LOGW(TAG, "short payload for type 0x%02x", h.type);
            send_ready();
            continue;
        }
        handle_message(&h, s_rx);
        if (esp_timer_get_time() - s_last_stats_us > 2000000) send_stats();
    }
}

static void screen_task(void *arg)
{
    /* Idle / ident / no-signal presentation, independent of the protocol loop. */
    char l1[40], l2[40];
    int64_t last_state = -1;
    for (;;) {
        int64_t now = esp_timer_get_time();
        int state;
        if (now < s_ident_until_us) state = 1;                       /* identify */
        else if (!usb_link_connected()) state = 2;                    /* no usb */
        else if (!s_assigned) state = 3;                              /* waiting for assignment */
        else state = 0;                                               /* streaming */
        if (state != last_state) {
            /* IDENT is a banner stamped on every frame (live or the kept last one), not a separate screen. */
            if (state == 1) {
                snprintf(l1, sizeof(l1), "IDENT %s", s_label);
                snprintf(l2, sizeof(l2), "%.8s-%.8s", s_serial, s_serial + 8);
                display_set_overlay(l1, l2);
            } else {
                display_set_overlay(NULL, NULL);
            }
            if ((state == 0 && last_state > 0) || (state == 1 && s_last_jpeg_len)) {
                /* redraw the last received frame: without the banner (back to normal) or with it (ident) */
                if (xSemaphoreTake(s_frame_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
                    if (s_last_jpeg_len) display_show_jpeg(s_last_jpeg, s_last_jpeg_len, NULL);
                    xSemaphoreGive(s_frame_mutex);
                }
            } else if (state == 1) {
                show_idle_screen(l1, l2, 0xFF8000);     /* no frame received yet: plain ident screen */
            } else if (state == 2) {
                snprintf(l1, sizeof(l1), "NO USB");
                snprintf(l2, sizeof(l2), "%.8s FW %s", s_serial, FW_VERSION);
                show_idle_screen(l1, l2, 0x404040);
            } else if (state == 3) {
                snprintf(l1, sizeof(l1), "NOT ASSIGNED");
                snprintf(l2, sizeof(l2), "%.8s-%.8s", s_serial, s_serial + 8);
                show_idle_screen(l1, l2, 0x00A0FF);
            }
            last_state = state;
        }
        static int tick;
        if (++tick % 25 == 0) {          /* every 5 s */
            display_log_bridge_status();
        }
        vTaskDelay(pdMS_TO_TICKS(200));
    }
}

#define DIAG_MODES 4      /* HDMI modes 0..3, see display.h */
#define DIAG_STAGE_S 20

/* Big stage number, the resolution as text, a white frame and corner blocks so cropping / stretching is visible. */
static void show_diag_screen(int mode, char sub)
{
    display_info_t di = display_get_info();
    int w = di.width, h = di.height;
    size_t n = (size_t)w * h * 3;
    uint8_t *buf = heap_caps_calloc(1, n, MALLOC_CAP_SPIRAM);
    if (!buf) return;
    /* frame: 8 px white border, 40 px corner blocks */
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            bool border = x < 8 || y < 8 || x >= w - 8 || y >= h - 8;
            bool corner = (x < 40 || x >= w - 40) && (y < 40 || y >= h - 40);
            if (border || corner) {
                uint8_t *p = buf + ((size_t)y * w + x) * 3;
                p[0] = p[1] = p[2] = 0xFF;
            }
        }
    }
    char num[16], res[32];
    snprintf(num, sizeof(num), "%d%c", mode + 1, sub);
    snprintf(res, sizeof(res), "%dX%d", w, h);
    int big = h / 3 / 7;                              /* glyph height = a third of the picture */
    ident_draw_text(buf, w, h, (w - 11 * big) / 2, h / 2 - 7 * big / 2 - big, big, 0xFFFFFF, num);
    int small = w / 100;
    int tw = (int)strlen(res) * 6 * small;
    ident_draw_text(buf, w, h, (w - tw) / 2, h / 2 + 7 * big / 2, small, 0x00C0FF, res);
    esp_err_t err = display_show_rgb(buf, w, h);
    ESP_LOGI(TAG, "diag screen %dx%d drawn: %s", w, h, esp_err_to_name(err));
    free(buf);
}

/* Bench diagnostics (NVS "diag" != 0): show the stage screen for this boot's HDMI mode, then reboot into the next
 * mode. The panel then shows 1 (768x768), 2 (1024x768), 3 (800x600), 4 (1280x720) in turn, 20 s each. */
static void diag_task(void *arg)
{
    display_info_t di = display_get_info();
    int var = nvs_get_int("dsivar", 0);
    vTaskDelay(pdMS_TO_TICKS(1000));
    show_diag_screen(di.mode, 'A' + var);
    ESP_LOGW(TAG, "DIAG stage %d%c: HDMI %dx%d, DSI variant %d, 20 s", di.mode + 1, 'A' + var, di.width, di.height, var);
    for (int t = 0; t < 4; t++) { vTaskDelay(pdMS_TO_TICKS(5000)); display_log_bridge_status(); }
    nvs_set_int("dsivar", (var + 1) % 4);
    nvs_set_int("mode", 0);            /* stay on the 768x768 target mode while testing DSI variants */
    esp_restart();
}

void app_main(void)
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ESP_ERROR_CHECK(nvs_flash_init());
    }
    load_or_create_serial();
    const esp_app_desc_t *app = esp_app_get_description();
    ESP_LOGI(TAG, "GlassLink DU fw %s (%s, app %s) serial %s", FW_VERSION, FW_BUILD, app->version, s_serial);

    s_rx = heap_caps_malloc(RX_BUF_SIZE, MALLOC_CAP_SPIRAM);
    s_last_jpeg = heap_caps_malloc(RX_BUF_SIZE, MALLOC_CAP_SPIRAM);
    ESP_ERROR_CHECK(s_rx && s_last_jpeg ? ESP_OK : ESP_ERR_NO_MEM);
    s_frame_mutex = xSemaphoreCreateMutex();

    if (!nvs_get_int("diag", 0)) {
        nvs_set_int("dsivar", 0);      /* a diagnostics run leaves this behind; "mode" is the user's HDMI mode (SET_MODE) and stays */
    }
    ESP_ERROR_CHECK(display_init(nvs_get_int("mode", 0), nvs_get_int("dsivar", 0)));
    display_set_brightness(nvs_get_int("brightness", 100));
    display_set_rotation(nvs_get_int("rotation", 0));

    ESP_ERROR_CHECK(usb_link_start(s_serial));

    /* A previous OTA image that boots this far is good: cancel rollback. */
    esp_ota_mark_app_valid_cancel_rollback();

    xTaskCreatePinnedToCore(protocol_task, "proto", 8192, NULL, 5, NULL, 0);   /* core 0: TinyUSB owns core 1 */
    if (nvs_get_int("diag", 0)) {
        xTaskCreatePinnedToCore(diag_task, "diag", 8192, NULL, 3, NULL, 0);
    } else {
        xTaskCreatePinnedToCore(screen_task, "screen", 8192, NULL, 3, NULL, 0);
    }
}
