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
#include "esp_mac.h"
#include "esp_rom_md5.h"
#include "esp_attr.h"
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
/* One JPEG frame: 768x768 q85 is 30-60 KB; a busy 1920x1080 frame can pass 512 KB, so 1080p gets 1 MB (INFO
 * reports it as max_frame, and the host sends nothing larger). A header that announces more is drained, not
 * treated as a broken stream (#16). */
static uint32_t s_rx_size = 512 * 1024;
#define RX_BUF_SIZE s_rx_size
#define XD_MAX_DRAIN (16u * 1024 * 1024)   /* larger than this is not a message but garbage: resync */

static char s_serial[33];
static char s_label[32];    /* human-readable unit name sent by the host with SHOW_IDENT */
static volatile uint32_t s_ident_gen;   /* bumped by every SHOW_IDENT, so a new label shows while ident is on */
static volatile int s_screen_state = -1;   /* the screen task's state: 0 picture, 1 ident, 2 no USB, 3 not assigned */
static char s_display_error[48];    /* why the display could not start (INFO); empty when it runs */
static uint8_t *s_rx;               /* frame receive buffer (PSRAM) */
static uint8_t *s_last_jpeg;        /* copy of the last frame shown, redrawn after IDENT / idle screens (PSRAM) */
static uint32_t s_last_jpeg_len;

/* A layout: the screen split into tiles, one display each (an HDMI screen behind a panel with several cutouts).
   Set by SET_LAYOUT, fed by TILE; a tile costs what a frame costs on a single-display DU, whatever the screen size. */
#define MAX_TILES 6
typedef struct { uint16_t x, y, w, h; bool on; } tile_t;   /* on = false: the host's rectangle did not fit (#19) */
static tile_t s_tiles[MAX_TILES];
static int s_tile_n;                    /* > 0: tile mode; tile i is the host's tile i (rejected ones keep their slot) */
static bool s_tile_cards;               /* show the tiles as test cards (for lining them up with the cutouts) */
static uint8_t *s_tile_jpeg[MAX_TILES]; /* the last JPEG of each tile, redrawn after IDENT and brightness changes (PSRAM) */
static uint32_t s_tile_jpeg_len[MAX_TILES], s_tile_jpeg_cap[MAX_TILES];
static bool has_last(void);
static void redraw_last(void);
static uint32_t s_last_seq;
static uint32_t s_frames, s_dropped, s_decode_ms_acc, s_decode_n, s_draw_us_acc, s_rx_us_acc;
static int64_t s_last_frame_us, s_ident_until_us, s_last_stats_us;
static bool s_assigned;
static struct { esp_ota_handle_t h; const esp_partition_t *part; uint32_t expected; uint32_t got; uint32_t crc;
                int64_t last_us; bool active; } s_ota;
static bool s_app_confirmed;        /* this image has shown it works; until then a crash rolls back to the old one */

/* Consecutive boots that did not get as far as a stable unit; kept across software resets, not across power loss.
 * Three in a row start in the safe 768x768 mode (#21). */
#define BOOT_MAGIC 0x474c4254u
static RTC_NOINIT_ATTR uint32_t s_boot_magic, s_boot_count;

/* ---- serial GUID (NVS) --------------------------------------------------------------------- */
static void load_or_create_serial(void)
{
    nvs_handle_t nvs;
    ESP_ERROR_CHECK(nvs_open("module", NVS_READWRITE, &nvs));
    size_t len = sizeof(s_serial);
    if (nvs_get_str(nvs, "serial", s_serial, &len) != ESP_OK || strlen(s_serial) != 24) {
        /* Derived from the chip's factory MAC, so an NVS erase gives the same serial back (#24). Units that already
         * have a stored serial keep it. */
        uint8_t mac[6] = {0}, digest[16];
        if (esp_read_mac(mac, ESP_MAC_EFUSE_FACTORY) == ESP_OK) {
            md5_context_t ctx;
            esp_rom_md5_init(&ctx);
            esp_rom_md5_update(&ctx, "glasslink-du", 12);
            esp_rom_md5_update(&ctx, mac, sizeof(mac));
            esp_rom_md5_final(digest, &ctx);
            for (int i = 0; i < 12; i++) snprintf(s_serial + i * 2, 3, "%02x", digest[i]);
        } else {
            for (int i = 0; i < 24; i += 8) snprintf(s_serial + i, 9, "%08lx", (unsigned long)esp_random());
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
static SemaphoreHandle_t s_frame_mutex;

/* A black screen with two lines of text. Only the band with the text is allocated (a full 1080p picture is 6 MB,
 * which is not always free) and everything is drawn under the frame lock, like every other draw (#18, #22).
 * Returns false when nothing could be drawn, so the caller tries again. */
static bool show_idle_screen(const char *line1, const char *line2, uint32_t colour)
{
    display_info_t di = display_get_info();
    if (!display_ready()) return true;
    int scale = di.width / 120, small = scale / 2 > 0 ? scale / 2 : 1;  /* ~6 px per glyph column at 768 wide */
    int y0 = di.height / 2 - 60, bh = 80 + 7 * small + 8;
    if (y0 < 0) y0 = 0;
    if (y0 + bh > di.height) bh = di.height - y0;
    uint8_t *buf = heap_caps_calloc(1, (size_t)di.width * bh * 3, MALLOC_CAP_SPIRAM);
    if (!buf) return false;
    ident_draw_text(buf, di.width, bh, 20, 0, scale, colour, line1);
    ident_draw_text(buf, di.width, bh, 20, 80, small, 0x808080, line2);
    bool ok = false;
    if (xSemaphoreTake(s_frame_mutex, pdMS_TO_TICKS(500)) == pdTRUE) {
        display_fill(0x000000);
        ok = display_show_rgb_at(buf, di.width, bh, 0, y0) == ESP_OK;
        xSemaphoreGive(s_frame_mutex);
    }
    free(buf);
    return ok;
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
    if (n >= (int)sizeof(buf)) n = sizeof(buf) - 1;     /* the would-be length of a cut message: never past the buffer */
    if (n > 0) send_msg(XD_T_LOG, buf, (uint32_t)n, 0, level);
}

static void send_info(void)
{
    display_info_t di = display_get_info();
    char buf[400];
    int n = snprintf(buf, sizeof(buf),
                     "{\"fw\":\"%s\",\"build\":\"%s\",\"hw\":\"%s\",\"panel\":[%d,%d],\"decoder\":\"hw\",\"uptime_s\":%lld,\"serial\":\"%s\",\"mode\":%d,\"ident\":%d,\"caps\":[\"mode\",\"tiles\"],\"tiles\":%d,\"max_frame\":%lu,\"max_tiles\":%d,\"slot\":\"%s\",\"confirmed\":%d,\"display_error\":\"%s\"}",
                     FW_VERSION, FW_BUILD, HW_NAME, di.width, di.height, (long long)(esp_timer_get_time() / 1000000), s_serial, di.mode,
                     esp_timer_get_time() < s_ident_until_us ? 1 : 0, s_tile_n, (unsigned long)RX_BUF_SIZE, MAX_TILES,
                     esp_ota_get_running_partition() ? esp_ota_get_running_partition()->label : "?", s_app_confirmed ? 1 : 0, s_display_error);
    if (n >= (int)sizeof(buf)) n = sizeof(buf) - 1;
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
    if (n >= (int)sizeof(buf)) n = sizeof(buf) - 1;
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
    if (has_last() && xSemaphoreTake(s_frame_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
        redraw_last();
        xSemaphoreGive(s_frame_mutex);
    }
    send_msg(XD_T_OTA_PROGRESS, NULL, 0, 0, 0);
}

/* This image works with the host: cancel the rollback. Called after the first picture shown, or after a minute up. */
static void confirm_app(const char *why)
{
    if (s_app_confirmed) return;
    s_app_confirmed = true;
    s_boot_count = 0;
    esp_err_t err = esp_ota_mark_app_valid_cancel_rollback();
    ESP_LOGI(TAG, "image confirmed (%s): %s", why, esp_err_to_name(err));
}

static void ota_data(const uint8_t *data, uint32_t len, uint32_t offset)
{
    if (!s_ota.active) { send_msg(XD_T_OTA_RESULT, NULL, 0, 0, 7); return; }   /* no update running: out of order */
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
    if (!s_ota.active) { send_msg(XD_T_OTA_RESULT, NULL, 0, 0, 7); return; }
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
    s_boot_count = 0;                   /* a planned restart, not a crash */
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

/* One tile as a test card: a bright border, its number and its size, so it can be lined up with a panel cutout. */
static void draw_tile_card(int i)
{
    const tile_t *t = &s_tiles[i];
    if (!t->on) return;
    static const uint32_t colours[MAX_TILES] = {0x00C0FF, 0xFFC000, 0x40FF40, 0xFF60FF, 0x40FFFF, 0xFF8040};
    uint8_t *buf = heap_caps_malloc((size_t)t->w * t->h * 3, MALLOC_CAP_SPIRAM);
    if (!buf) return;
    uint32_t c = colours[i], bg = 0x202020;
    for (int y = 0; y < t->h; y++) {
        for (int x = 0; x < t->w; x++) {
            bool edge = x < 6 || y < 6 || x >= t->w - 6 || y >= t->h - 6;
            bool cross = (x == t->w / 2 || y == t->h / 2) && (x > t->w / 2 - 40 && x < t->w / 2 + 40) && (y > t->h / 2 - 40 && y < t->h / 2 + 40);
            uint32_t v = edge || cross ? c : bg;
            uint8_t *p = buf + ((size_t)y * t->w + x) * 3;
            p[0] = v & 0xFF; p[1] = (v >> 8) & 0xFF; p[2] = (v >> 16) & 0xFF;      /* B, G, R */
        }
    }
    char l1[24], l2[48];
    snprintf(l1, sizeof(l1), "TILE %d", i + 1);
    snprintf(l2, sizeof(l2), "%uX%u AT %u,%u", t->w, t->h, t->x, t->y);
    int big = t->w / 120 > 0 ? t->w / 120 : 1;
    ident_draw_text(buf, t->w, t->h, 20, t->h / 2 - 60 * big / 4, big, c, l1);
    ident_draw_text(buf, t->w, t->h, 20, t->h / 2 + 20, big / 2 > 0 ? big / 2 : 1, 0xC0C0C0, l2);
    display_stamp_overlay(buf, t->w, t->h);
    display_show_rgb_at(buf, t->w, t->h, t->x, t->y);
    free(buf);
}

/* True when there is a picture to show again: test cards, a tile picture, or the last frame. */
static bool has_last(void)
{
    if (s_tile_n) {
        if (s_tile_cards) return true;
        for (int i = 0; i < s_tile_n; i++) if (s_tiles[i].on && s_tile_jpeg_len[i]) return true;
        return false;
    }
    return s_last_jpeg_len != 0;
}

/* The screen task shows a picture (or the ident banner on it): only then is a redraw wanted. A brightness change or
 * a new layout while NOT ASSIGNED / NO USB is up must not paint an old cockpit picture over it (#17). */
static bool picture_on_screen(void)
{
    return s_screen_state == 0 || s_screen_state == 1;
}

/* Redraws what the screen last showed: the tiles (or their test cards) in tile mode, the last frame otherwise.
   Called with s_frame_mutex held. */
static void redraw_last(void)
{
    if (s_tile_n) {
        for (int i = 0; i < s_tile_n; i++) {
            if (!s_tiles[i].on) continue;
            if (s_tile_cards) draw_tile_card(i);
            else if (s_tile_jpeg_len[i]) display_show_jpeg_at(s_tile_jpeg[i], s_tile_jpeg_len[i], s_tiles[i].x, s_tiles[i].y, s_tiles[i].w, s_tiles[i].h, NULL);
        }
    } else if (s_last_jpeg_len) {
        display_show_jpeg(s_last_jpeg, s_last_jpeg_len, NULL);
    }
}

/* Leaves tile mode: a single-display host (a FRAME, a new USB session) owns the whole screen again. */
static void end_tile_mode(void)
{
    if (!s_tile_n) return;
    s_tile_n = 0;
    s_tile_cards = false;
    for (int i = 0; i < MAX_TILES; i++) s_tile_jpeg_len[i] = 0;
    display_fill(0x000000);
}

static int64_t s_hdr_us;   /* when the last header arrived (for rx time accounting) */

static void handle_message(const xd_header_t *h, const uint8_t *payload)
{
    switch (h->type) {
    case XD_T_FRAME: {
        if (s_ota.active) { send_ready(); break; }        /* no frames while updating, but the host must not stall */
        uint32_t ms = 0;
        int64_t rx_done = esp_timer_get_time();
        xSemaphoreTake(s_frame_mutex, portMAX_DELAY);     /* the screen task may be redrawing the last frame */
        end_tile_mode();                                   /* a whole-screen picture ends a layout (#17) */
        esp_err_t err = display_show_jpeg(payload, h->length, &ms);
        if (err == ESP_OK) {
            /* keep this frame for redraws by swapping buffers, not copying: the payload is s_rx, both are
             * RX_BUF_SIZE, and the next frame is read into the old one */
            uint8_t *old = s_last_jpeg;
            s_last_jpeg = s_rx;
            s_rx = old;
            s_last_jpeg_len = h->length;
        }
        xSemaphoreGive(s_frame_mutex);
        if (err == ESP_OK) {
            s_frames++; s_decode_ms_acc += ms; s_decode_n++;
            s_draw_us_acc += display_last_draw_us();
            s_rx_us_acc += (uint32_t)(rx_done - s_hdr_us);
            s_last_seq = h->seq; s_last_frame_us = esp_timer_get_time(); s_assigned = true;
            confirm_app("a picture was shown");
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
        if (picture_on_screen() && has_last() && esp_timer_get_time() - s_last_frame_us > 40000) {
            /* no frame is arriving right now: redraw the last one so the change shows at once */
            xSemaphoreTake(s_frame_mutex, portMAX_DELAY);
            redraw_last();
            xSemaphoreGive(s_frame_mutex);
        }
        break;
    case XD_T_SET_ROTATION: display_set_rotation((int)h->arg); nvs_set_int("rotation", (int)h->arg); break;
    case XD_T_SHOW_IDENT:
        snprintf(s_label, sizeof(s_label), "%.*s", (int)(h->length < sizeof(s_label) - 1 ? h->length : sizeof(s_label) - 1), (const char *)payload);
        s_ident_until_us = h->arg ? esp_timer_get_time() + (int64_t)h->arg * 1000000 : 0;
        s_ident_gen++;
        send_stats();                       /* so the host sees the new ident state immediately */
        break;
    case XD_T_PING: send_msg(XD_T_PONG, NULL, 0, 0, h->arg); break;
    case XD_T_SET_ASSIGNED:
        s_assigned = h->arg != 0;           /* the screen task switches between picture and NOT ASSIGNED */
        break;
    case XD_T_SET_LAYOUT: {
        if (s_ota.active) { break; }
        display_info_t di = display_get_info();
        int n = (int)(h->length / 8);
        if (n > MAX_TILES) {
            send_log(2, "layout of %d tiles: only the first %d are kept", n, MAX_TILES);
            n = MAX_TILES;
        }
        tile_t next[MAX_TILES] = {0};
        int off = 0;
        for (int i = 0; i < n; i++) {
            const uint8_t *p = payload + i * 8;
            tile_t t = {(uint16_t)(p[0] | p[1] << 8), (uint16_t)(p[2] | p[3] << 8), (uint16_t)(p[4] | p[5] << 8), (uint16_t)(p[6] | p[7] << 8), true};
            if (t.w == 0 || t.h == 0 || t.x + t.w > di.width || t.y + t.h > di.height) {
                send_log(2, "tile %d: %ux%u at %u,%u is outside the %dx%d screen, left empty", i + 1, t.w, t.h, t.x, t.y, di.width, di.height);
                t.on = false;                  /* the slot stays: tile i+1 is still the host's tile i+1 (#19) */
                off++;
            }
            next[i] = t;
        }
        bool cards = (h->arg & 1) != 0;
        xSemaphoreTake(s_frame_mutex, portMAX_DELAY);
        bool same = n == s_tile_n && cards == s_tile_cards && memcmp(next, s_tiles, sizeof(tile_t) * n) == 0;
        if (!same) {                               /* the host resends the layout after INFO: unchanged = nothing to do */
            for (int i = 0; i < MAX_TILES; i++) {
                if (i >= n || memcmp(&next[i], &s_tiles[i], sizeof(tile_t)) != 0) s_tile_jpeg_len[i] = 0;   /* its old picture belongs elsewhere */
            }
            memcpy(s_tiles, next, sizeof(next));
            s_tile_n = n;
            s_tile_cards = cards;
            if (picture_on_screen()) {
                display_fill(0x000000);
                redraw_last();
            }
        }
        xSemaphoreGive(s_frame_mutex);
        if (!same) send_log(1, "layout: %d tile(s)%s%s", n - off, off ? " (some left empty)" : "", cards ? ", test cards" : "");
        break;
    }
    case XD_T_TILE: {
        if (s_ota.active) { send_ready(); break; }
        int i = (int)h->arg;
        if (i < 0 || i >= s_tile_n || !s_tiles[i].on) {
            s_dropped++;
            static int64_t last_log;                    /* at most one line a second: the host keeps sending */
            if (esp_timer_get_time() - last_log > 1000000) {
                last_log = esp_timer_get_time();
                send_log(2, "tile %d: not in the layout (%d tiles)", i + 1, s_tile_n);
            }
            send_ready();
            break;
        }
        uint32_t ms = 0;
        int64_t rx_done = esp_timer_get_time();
        xSemaphoreTake(s_frame_mutex, portMAX_DELAY);
        esp_err_t err = s_tile_cards ? ESP_OK      /* while the cards are up the picture is kept, not drawn */
                        : display_show_jpeg_at(payload, h->length, s_tiles[i].x, s_tiles[i].y, s_tiles[i].w, s_tiles[i].h, &ms);
        if (err == ESP_OK) {
            if (h->length > s_tile_jpeg_cap[i]) {       /* sized to the pictures of this tile, not to the largest possible (#22) */
                uint32_t cap = h->length + h->length / 4;
                uint8_t *b = heap_caps_realloc(s_tile_jpeg[i], cap, MALLOC_CAP_SPIRAM);
                if (b) { s_tile_jpeg[i] = b; s_tile_jpeg_cap[i] = cap; }
            }
            if (s_tile_jpeg[i] && h->length <= s_tile_jpeg_cap[i]) { memcpy(s_tile_jpeg[i], payload, h->length); s_tile_jpeg_len[i] = h->length; }
            else s_tile_jpeg_len[i] = 0;
        }
        xSemaphoreGive(s_frame_mutex);
        if (err == ESP_OK) {
            s_frames++; s_decode_ms_acc += ms; s_decode_n++;
            s_draw_us_acc += s_tile_cards ? 0 : display_last_draw_us();
            s_rx_us_acc += (uint32_t)(rx_done - s_hdr_us);
            s_last_seq = h->seq; s_last_frame_us = esp_timer_get_time(); s_assigned = true;
            confirm_app("a tile was shown");
        } else {
            s_dropped++;
            send_log(2, "tile %d decode failed seq %lu: %s", i + 1, (unsigned long)h->seq, esp_err_to_name(err));
        }
        send_ready();
        break;
    }
    case XD_T_SET_MODE:
        if (h->arg <= 4) {                  /* the HDMI DU on another screen: takes effect after the restart */
            nvs_set_int("mode", (int)h->arg);
            send_log(1, "HDMI mode %u stored, restarting", (unsigned)h->arg);
            vTaskDelay(pdMS_TO_TICKS(300));
            s_boot_count = 0;               /* a planned restart */
            confirm_app("the host asked for a restart");   /* it talks to the host: not a reason to roll back */
            esp_restart();
        }
        send_log(1, "HDMI mode %u unknown", (unsigned)h->arg);
        break;
    case XD_T_OTA_BEGIN: ota_begin(h->arg); break;
    case XD_T_OTA_DATA: ota_data(payload, h->length, h->arg); break;
    case XD_T_OTA_END: ota_end(h->arg); break;
    case XD_T_REBOOT: s_boot_count = 0; confirm_app("the host asked for a restart"); esp_restart(); break;
    default: send_log(1, "unknown message type 0x%02x", h->type); break;
    }
}

static bool type_known(uint8_t type)
{
    switch (type) {
    case XD_T_FRAME: case XD_T_GET_INFO: case XD_T_SET_BRIGHTNESS: case XD_T_SET_ROTATION: case XD_T_SHOW_IDENT:
    case XD_T_PING: case XD_T_SET_ASSIGNED: case XD_T_SET_MODE: case XD_T_SET_LAYOUT: case XD_T_TILE: case XD_T_OTA_BEGIN: case XD_T_OTA_DATA: case XD_T_OTA_END: case XD_T_REBOOT:
        return true;
    default:
        return false;
    }
}

/* A header we can act on or at least skip: our magic and version and a length that could be a message. */
static bool header_sane(const xd_header_t *h)
{
    return h->magic[0] == XD_MAGIC0 && h->magic[1] == XD_MAGIC1 && h->version == XD_PROTO_VERSION && h->length <= XD_MAX_DRAIN;
}

/* While resyncing only known host->module types count, so JPEG data cannot fake a header. */
static bool header_valid(const xd_header_t *h)
{
    return header_sane(h) && type_known(h->type);
}

/* Reads and throws away a payload we cannot use (too large for our buffer, or a type from a newer host). */
static bool drain(uint32_t len)
{
    uint32_t left = len;
    while (left) {
        size_t chunk = left < RX_BUF_SIZE ? left : RX_BUF_SIZE;
        if (read_exact(s_rx, chunk, 3000) != chunk) return false;
        left -= chunk;
    }
    return true;
}

static void protocol_task(void *arg)
{
    bool was_connected = false;
    for (;;) {
        bool connected = usb_link_connected();
        if (connected && !was_connected) {
            ESP_LOGI(TAG, "usb configured");
            s_last_stats_us = esp_timer_get_time();
            xSemaphoreTake(s_frame_mutex, portMAX_DELAY);
            end_tile_mode();                /* a new host session starts without the old one's layout (#17) */
            xSemaphoreGive(s_frame_mutex);
            vTaskDelay(pdMS_TO_TICKS(100));
            send_ready();
        }
        was_connected = connected;
        if (s_ota.active && esp_timer_get_time() - s_ota.last_us > 15000000) {
            ota_fail(6);                     /* the host went away mid-update (also unplugged): give the flash slot back */
        }
        if (!s_app_confirmed && esp_timer_get_time() > 60000000) {
            confirm_app("a minute without a crash");
        }
        if (!connected) {
            vTaskDelay(pdMS_TO_TICKS(50));
            continue;
        }
        xd_header_t h;
        if (read_exact((uint8_t *)&h, sizeof(h), 250) != sizeof(h)) {
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
        if (h.length > RX_BUF_SIZE || !type_known(h.type)) {
            /* a frame too large for this unit, or a message a newer host sends: skip it by its length, say so, and
             * keep the flow going (it used to cost a 3 s resync per frame) (#16) */
            bool ok = drain(h.length);
            if (h.type == XD_T_FRAME || h.type == XD_T_TILE) {
                s_dropped++;
                send_log(2, "frame of %lu bytes is larger than this unit's %lu: skipped", (unsigned long)h.length, (unsigned long)RX_BUF_SIZE);
                send_ready();
            } else {
                send_log(1, "message type 0x%02x (%lu bytes) not known here: skipped%s", h.type, (unsigned long)h.length, ok ? "" : " (short)");
            }
            continue;
        }
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
    uint32_t last_gen = 0;
    bool idle_up = false;                  /* an idle screen covers the whole panel: clear it before a picture */
    for (;;) {
        int64_t now = esp_timer_get_time();
        int state;
        if (now < s_ident_until_us) state = 1;                       /* identify */
        else if (!usb_link_connected()) state = 2;                    /* no usb */
        else if (!s_assigned) state = 3;                              /* waiting for assignment */
        else state = 0;                                               /* streaming */
        s_screen_state = state;
        if (state == 1 && s_ident_gen != last_gen) last_state = -1;   /* a new label while ident is on */
        last_gen = s_ident_gen;
        if (state != last_state) {
            bool drawn = true;
            /* IDENT is a banner stamped on every frame (live or the kept last one), not a separate screen. */
            if (state == 1) {
                snprintf(l1, sizeof(l1), "IDENT %s", s_label);
                snprintf(l2, sizeof(l2), "%.8s-%.8s", s_serial, s_serial + 8);
                display_set_overlay(l1, l2);
            } else {
                display_set_overlay(NULL, NULL);
            }
            if ((state == 0 && last_state != 0) || (state == 1 && has_last())) {
                /* redraw the last received frame: without the banner (back to normal) or with it (ident); after an
                 * idle screen clear first, or its text stays around a smaller picture or between tiles (#17) */
                if (xSemaphoreTake(s_frame_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
                    if (idle_up) display_fill(0x000000);
                    redraw_last();
                    xSemaphoreGive(s_frame_mutex);
                    idle_up = false;
                } else {
                    drawn = false;
                }
            } else if (state == 1) {
                drawn = show_idle_screen(l1, l2, 0xFF8000);     /* no frame received yet: plain ident screen */
                idle_up = true;
            } else if (state == 2) {
                snprintf(l1, sizeof(l1), "NO USB");
                snprintf(l2, sizeof(l2), "%.8s FW %s", s_serial, FW_VERSION);
                drawn = show_idle_screen(l1, l2, 0x404040);
                idle_up = true;
            } else if (state == 3) {
                snprintf(l1, sizeof(l1), "NOT ASSIGNED");
                snprintf(l2, sizeof(l2), "%.8s-%.8s", s_serial, s_serial + 8);
                drawn = show_idle_screen(l1, l2, 0x00A0FF);
                idle_up = true;
            }
            if (drawn) last_state = state;                            /* not drawn: try again next round */
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

    /* Boots that crash before the unit is stable are counted; three in a row fall back to the safe mode (#21). */
    if (s_boot_magic != BOOT_MAGIC || esp_reset_reason() == ESP_RST_POWERON) {
        s_boot_magic = BOOT_MAGIC;
        s_boot_count = 0;
    }
    s_boot_count++;
    int mode = nvs_get_int("mode", 0);
    if (s_boot_count >= 3 && mode != 0) {
        ESP_LOGW(TAG, "%lu unstable boots in a row: starting in the 768x768 mode", (unsigned long)s_boot_count);
        mode = 0;
        nvs_set_int("mode", 0);
    }

    s_frame_mutex = xSemaphoreCreateMutex();
    if (!nvs_get_int("diag", 0)) {
        nvs_set_int("dsivar", 0);      /* a diagnostics run leaves this behind; "mode" is the user's HDMI mode (SET_MODE) and stays */
    }
    /* USB first: whatever happens to the display, the host can still reach the unit (LOG, SET_MODE, OTA) (#21). */
    if (mode == 4) s_rx_size = 1024 * 1024;
    s_rx = heap_caps_malloc(RX_BUF_SIZE, MALLOC_CAP_SPIRAM);
    s_last_jpeg = heap_caps_malloc(RX_BUF_SIZE, MALLOC_CAP_SPIRAM);
    ESP_ERROR_CHECK(s_rx && s_last_jpeg ? ESP_OK : ESP_ERR_NO_MEM);
    ESP_ERROR_CHECK(usb_link_start(s_serial));

    esp_err_t derr = display_init(mode, nvs_get_int("dsivar", 0));
    if (derr != ESP_OK) {
        snprintf(s_display_error, sizeof(s_display_error), "display: %s", esp_err_to_name(derr));
        ESP_LOGE(TAG, "%s: running without a display (USB, LOG and firmware updates still work)", s_display_error);
    }
    display_set_brightness(nvs_get_int("brightness", 100));
    display_set_rotation(nvs_get_int("rotation", 0));
    /* The image is confirmed (rollback cancelled) only once it has shown a picture, or after a minute without a
     * crash: a new image that fails before that is rolled back by the bootloader (#20). One that was confirmed on
     * an earlier boot stays confirmed. */
    esp_ota_img_states_t ota_state;
    if (esp_ota_get_state_partition(esp_ota_get_running_partition(), &ota_state) == ESP_OK && ota_state != ESP_OTA_IMG_PENDING_VERIFY) {
        s_app_confirmed = true;
    }

    xTaskCreatePinnedToCore(protocol_task, "proto", 8192, NULL, 5, NULL, 0);   /* core 0: TinyUSB owns core 1 */
    if (!display_ready()) return;           /* headless: no screen task */
    if (nvs_get_int("diag", 0)) {
        xTaskCreatePinnedToCore(diag_task, "diag", 8192, NULL, 3, NULL, 0);
    } else {
        xTaskCreatePinnedToCore(screen_task, "screen", 8192, NULL, 3, NULL, 0);
    }
}
