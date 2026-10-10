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
#include "esp_chip_info.h"
#include "esp_rom_crc.h"
#include "esp_app_desc.h"
#include "esp_heap_caps.h"
#include "esp_mac.h"
#include "esp_rom_md5.h"
#include "esp_attr.h"
#include "esp_task_wdt.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "freertos/queue.h"
#include "proto.h"
#include "usb_link.h"
#include "display.h"
#include "ident.h"
#include "screens.h"

static const char *TAG = "main";

#ifndef FW_VERSION              /* both come from CMake: ../VERSION and git describe */
#define FW_VERSION "0.0.0"
#endif
#ifndef FW_BUILD
#define FW_BUILD "nogit"
#endif
#define HW_NAME "p4-nano+lt8912b"
/* A released build (sdkconfig.release) takes updates over USB only when they are signed with the release key; INFO
 * says so ("signed"), so the DMC can tell before it sends an image that would be refused. */
#ifdef CONFIG_SECURE_SIGNED_ON_UPDATE_NO_SECURE_BOOT
#define SIGNED_UPDATES 1
#else
#define SIGNED_UPDATES 0
#endif
/* One JPEG frame: 768x768 q85 is 30-60 KB; a busy 1920x1080 frame can pass 512 KB, so 1080p gets 1 MB (INFO
 * reports it as max_frame, and the host sends nothing larger). A header that announces more is drained, not
 * treated as a broken stream (#16). */
static uint32_t s_rx_size = 512 * 1024;
#define RX_BUF_SIZE s_rx_size
#define XD_MAX_DRAIN XD_MAX_PAYLOAD        /* larger than this is not a message but garbage: resync (protocol: 4 MiB) */

static char s_serial[33];
static char s_label[32];    /* the DU's name on the status page (SHOW_IDENT, SET_ASSIGNED), kept in NVS for the screens */
static volatile uint32_t s_ident_gen;   /* bumped by every SHOW_IDENT, so a new label shows while ident is on */
/* the screen task's state: 0 picture, 1 picture with the Identify banner, 2 no USB, 3 not assigned, 4 no DMC,
 * 5 waiting for the sim, 6 updating, 7 Identify without a picture (#81) */
static volatile int s_screen_state = -1;
static char s_display_error[48];    /* why the display could not start (INFO); empty when it runs */
static uint8_t *s_rx;               /* frame receive buffer (PSRAM) */
static uint8_t *s_last_jpeg;        /* copy of the last frame shown, redrawn after IDENT / idle screens (PSRAM) */
static uint32_t s_last_jpeg_len;

/* A layout: the screen split into tiles, one display each (an HDMI screen behind a panel with several cutouts).
   Set by SET_LAYOUT, fed by TILE; a tile costs what a frame costs on a single-display DU, whatever the screen size. */
#define MAX_TILES 6
/* on = 0: the host's rectangle did not fit (#19). No padding bytes: layouts are compared with memcmp, and a padding
   byte of stack garbage made an unchanged layout look new (a black flash at every INFO, review F13). */
typedef struct { uint16_t x, y, w, h, on; } tile_t;
_Static_assert(sizeof(tile_t) == 10, "tile_t must have no padding: it is compared with memcmp");
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
static volatile int s_assign;           /* SET_ASSIGNED: 0 nothing assigned, 1 pictures come, 2 waiting for the sim (#81) */
static char s_display_name[64];         /* the assigned display's name, from SET_ASSIGNED */
static volatile int64_t s_host_us;      /* when the last message from a DMC came; 0: none in this session */
static volatile uint32_t s_screen_gen;  /* bumped when what a screen of the DU's own shows changes: draw it again */
#define HOST_TIMEOUT_US 6000000         /* a DMC repeats SET_ASSIGNED every 2 s */
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
    /* Without NVS (a flash fault) the serial is still made from the MAC, only not stored: an abort here would restart
     * the DU for ever with nothing on screen and no USB to update it through (review F14). */
    nvs_handle_t nvs;
    bool have = nvs_open("module", NVS_READWRITE, &nvs) == ESP_OK;
    size_t len = sizeof(s_serial);
    if (!have || nvs_get_str(nvs, "serial", s_serial, &len) != ESP_OK || strlen(s_serial) != 24) {
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
        if (have && (nvs_set_str(nvs, "serial", s_serial) != ESP_OK || nvs_commit(nvs) != ESP_OK)) {
            ESP_LOGE(TAG, "the serial could not be stored in NVS");
        }
        ESP_LOGI(TAG, "generated new serial %s", s_serial);
    }
    if (have) nvs_close(nvs);
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

static void nvs_load_label(void)
{
    nvs_handle_t nvs;
    if (nvs_open("module", NVS_READONLY, &nvs) == ESP_OK) {
        size_t len = sizeof(s_label);
        if (nvs_get_str(nvs, "label", s_label, &len) != ESP_OK) s_label[0] = 0;
        nvs_close(nvs);
    }
}

static void nvs_save_label(void)
{
    nvs_handle_t nvs;
    if (nvs_open("module", NVS_READWRITE, &nvs) == ESP_OK) {
        nvs_set_str(nvs, "label", s_label);
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
static SemaphoreHandle_t s_tx_mutex;   /* header and payload of one message go out together: two tasks send (#61) */

static bool send_msg(uint8_t type, const void *payload, uint32_t len, uint32_t seq, uint32_t arg)
{
    /* One message, one USB transfer (all of the DU's messages are small): it goes out whole or not at all. As two, a
     * header could be queued while no host reads and its payload time out, and the next host session read that header
     * with the next message's bytes as its payload, losing the INFO it waits for (review F6). */
    static uint8_t buf[sizeof(xd_header_t) + 512];    /* under s_tx_mutex */
    xd_header_t h = {{XD_MAGIC0, XD_MAGIC1}, XD_PROTO_VERSION, type, len, seq, arg};
    xSemaphoreTake(s_tx_mutex, portMAX_DELAY);
    bool ok;
    if (len <= sizeof(buf) - sizeof(h)) {
        memcpy(buf, &h, sizeof(h));
        if (len) memcpy(buf + sizeof(h), payload, len);
        ok = usb_link_write(buf, sizeof(h) + len, 500);
    } else {
        ok = usb_link_write((const uint8_t *)&h, sizeof(h), 500) && usb_link_write(payload, len, 2000);
    }
    xSemaphoreGive(s_tx_mutex);
    return ok;
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
    esp_chip_info_t ci;
    esp_chip_info(&ci);                     /* revision as major * 100 + minor: which image fits this DU (#82) */
    char buf[512];
    int n = snprintf(buf, sizeof(buf),
                     "{\"fw\":\"%s\",\"build\":\"%s\",\"hw\":\"%s\",\"panel\":[%d,%d],\"decoder\":\"hw\",\"uptime_s\":%lld,\"serial\":\"%s\",\"mode\":%d,\"ident\":%d,\"caps\":[\"mode\",\"tiles\",\"band\"],\"tiles\":%d,\"max_frame\":%lu,\"max_tiles\":%d,\"slot\":\"%s\",\"confirmed\":%d,\"signed\":%d,\"chip_rev\":%d,\"display_error\":\"%s\"}",
                     FW_VERSION, FW_BUILD, HW_NAME, di.width, di.height, (long long)(esp_timer_get_time() / 1000000), s_serial, di.mode,
                     esp_timer_get_time() < s_ident_until_us ? 1 : 0, s_tile_n, (unsigned long)RX_BUF_SIZE, MAX_TILES,
                     esp_ota_get_running_partition() ? esp_ota_get_running_partition()->label : "?", s_app_confirmed ? 1 : 0, SIGNED_UPDATES, (int)ci.revision, s_display_error);
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

/* Why the DU started, if it was not a normal start: said once to the first host that asks for INFO, so a restart by
 * the watchdog shows in the DMC's log without a serial cable (#80). */
static esp_reset_reason_t s_reset_reason;
static bool s_reset_reported;

static void report_reset_reason(void)
{
    if (s_reset_reported) return;
    s_reset_reported = true;
    const char *why = s_reset_reason == ESP_RST_TASK_WDT ? "the task watchdog: a task hung (the serial console shows where)"
                    : s_reset_reason == ESP_RST_PANIC ? "a crash (the serial console shows where)"
                    : s_reset_reason == ESP_RST_INT_WDT ? "the interrupt watchdog"
                    : s_reset_reason == ESP_RST_WDT ? "a watchdog"
                    : s_reset_reason == ESP_RST_BROWNOUT ? "a brownout (the supply voltage dipped)"
                    : NULL;
    if (why) send_log(2, "restarted after %s", why);
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

static void confirm_app(const char *why);

static void ota_begin(uint32_t size)
{
    /* the host talks to this image, which is enough to keep it: an unconfirmed image cannot start another update
     * (ESP_ERR_OTA_ROLLBACK_INVALID_STATE), and a second update in its first minute would fail */
    confirm_app("the host started an update");
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
    s_screen_gen++;                          /* the screen task shows the update with its progress (#81) */
    send_msg(XD_T_OTA_PROGRESS, NULL, 0, 0, 0);
}

/* This image works with the host: cancel the rollback. Called after the first picture shown, at the start of an update,
 * or after a minute up once a host has talked to it. */
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

/* A new DMC session, a DMC that quit, or a display whose sim went away must not bring back an old picture: yesterday's
 * PFD while the sim is not running (#81). The layout stays unless asked. Called with no picture being drawn. */
static void forget_pictures(bool end_layout)
{
    xSemaphoreTake(s_frame_mutex, portMAX_DELAY);
    if (end_layout) end_tile_mode();
    for (int i = 0; i < MAX_TILES; i++) s_tile_jpeg_len[i] = 0;
    s_last_jpeg_len = 0;
    xSemaphoreGive(s_frame_mutex);
}

static int64_t s_hdr_us;   /* when the last header arrived (for rx time accounting) */

/* ---- pictures (#61) ----------------------------------------------------------------------------
 * FRAME and TILE are drawn by the picture task while this task already receives the next one: READY goes out as
 * soon as a picture is handed over, so the transfer overlaps the decode (a 1080p band: 9 ms + 25 ms became the
 * longer of the two). Three receive buffers go round: the one being received into (s_rx), the one being drawn or
 * waiting, and the last whole-screen frame kept for redraws (s_last_jpeg). At most one picture waits; every other
 * message is handled only once the pictures before it are on screen, so the order stays what the host sent. */
typedef struct {
    xd_header_t h;
    uint8_t *buf;
    int64_t hdr_us, rx_done_us;
} picture_job_t;

static QueueHandle_t s_jobs;            /* pictures for the picture task (one waits at most) */
static QueueHandle_t s_free;            /* receive buffers the picture task is done with */
static TaskHandle_t s_proto_task;
static int s_pictures;                  /* handed over and not yet drawn (atomic) */

static void queue_picture(const xd_header_t *h)
{
    picture_job_t job = {*h, s_rx, s_hdr_us, esp_timer_get_time()};
    __atomic_add_fetch(&s_pictures, 1, __ATOMIC_SEQ_CST);
    xQueueSend(s_jobs, &job, portMAX_DELAY);
    xQueueReceive(s_free, &s_rx, portMAX_DELAY);   /* waits while one picture is drawn and another one waits */
    s_last_seq = h->seq;
    send_ready();
}

/* Before any other message: the pictures that came before it are drawn first. */
static void wait_pictures(void)
{
    while (__atomic_load_n(&s_pictures, __ATOMIC_SEQ_CST) > 0) {
        ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(100));
    }
}

/* Draws one picture; returns the buffer that is free afterwards (the old last frame when this one replaced it). */
static uint8_t *show_picture(const picture_job_t *job)
{
    const xd_header_t *h = &job->h;
    uint8_t *payload = job->buf;
    uint8_t *spare = payload;
    uint32_t ms = 0;
    if (h->type == XD_T_FRAME) {
        xSemaphoreTake(s_frame_mutex, portMAX_DELAY);     /* the screen task may be redrawing the last frame */
        end_tile_mode();                                   /* a whole-screen picture ends a layout (#17) */
        esp_err_t err = display_show_jpeg(payload, h->length, &ms);
        if (err == ESP_OK) {
            spare = s_last_jpeg;                           /* kept for redraws by swapping buffers, not copying */
            s_last_jpeg = payload;
            s_last_jpeg_len = h->length;
        } else if (err != ESP_ERR_INVALID_SIZE && s_last_jpeg_len) {
            /* a decode that failed half-way may have written into the screen: the last good picture again (F8) */
            display_show_jpeg(s_last_jpeg, s_last_jpeg_len, NULL);
        }
        xSemaphoreGive(s_frame_mutex);
        if (err == ESP_OK) {
            s_frames++; s_decode_ms_acc += ms; s_decode_n++;
            s_draw_us_acc += display_last_draw_us();
            s_rx_us_acc += (uint32_t)(job->rx_done_us - job->hdr_us);
            s_last_frame_us = esp_timer_get_time(); s_assign = 1;
            confirm_app("a picture was shown");
        } else {
            s_dropped++;
            static int64_t last_log_us;            /* a picture that never fits fails at the frame rate: one line a second */
            if (esp_timer_get_time() - last_log_us > 1000000) {
                last_log_us = esp_timer_get_time();
                send_log(2, "decode failed seq %lu: %s", (unsigned long)h->seq, esp_err_to_name(err));
            }
        }
        return spare;
    }

    int i = (int)h->arg;
    xSemaphoreTake(s_frame_mutex, portMAX_DELAY);
    if (i < 0 || i >= s_tile_n || !s_tiles[i].on) {
        int n = s_tile_n;
        xSemaphoreGive(s_frame_mutex);
        s_dropped++;
        static int64_t last_log;                    /* at most one line a second: the host keeps sending */
        if (esp_timer_get_time() - last_log > 1000000) {
            last_log = esp_timer_get_time();
            send_log(2, "tile %d: not in the layout (%d tiles)", i + 1, n);
        }
        return spare;
    }
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
    } else if (err != ESP_ERR_INVALID_SIZE && !s_tile_cards && s_tile_jpeg_len[i]) {
        /* a band is decoded straight into the screen: one that failed half-way left a torn picture until the display
           changes again, which a still display may not do for a long time (review F8) */
        display_show_jpeg_at(s_tile_jpeg[i], s_tile_jpeg_len[i], s_tiles[i].x, s_tiles[i].y, s_tiles[i].w, s_tiles[i].h, NULL);
    }
    bool cards = s_tile_cards;
    xSemaphoreGive(s_frame_mutex);
    if (err == ESP_OK) {
        s_frames++; s_decode_ms_acc += ms; s_decode_n++;
        s_draw_us_acc += cards ? 0 : display_last_draw_us();
        s_rx_us_acc += (uint32_t)(job->rx_done_us - job->hdr_us);
        s_last_frame_us = esp_timer_get_time(); s_assign = 1;
        confirm_app("a tile was shown");
    } else {
        s_dropped++;
        send_log(2, "tile %d decode failed seq %lu: %s", i + 1, (unsigned long)h->seq, esp_err_to_name(err));
    }
    return spare;
}

static void picture_task(void *arg)
{
    picture_job_t job;
    esp_task_wdt_add(NULL);                 /* a picture that never finishes restarts the DU (#80) */
    for (;;) {
        esp_task_wdt_reset();
        if (xQueueReceive(s_jobs, &job, pdMS_TO_TICKS(1000)) != pdTRUE) {
            continue;                       /* idle: waiting for a picture is not hanging */
        }
        uint8_t *spare = show_picture(&job);
        xQueueSend(s_free, &spare, portMAX_DELAY);
        if (__atomic_sub_fetch(&s_pictures, 1, __ATOMIC_SEQ_CST) == 0 && s_proto_task) {
            xTaskNotifyGive(s_proto_task);
        }
    }
}

static bool s_host_seen;               /* a host has sent a valid message to this image: its USB works */

static void handle_message(const xd_header_t *h, const uint8_t *payload)
{
    s_host_seen = true;
    switch (h->type) {
    case XD_T_FRAME:
    case XD_T_TILE:
        if (s_ota.active) { send_ready(); break; }        /* no pictures while updating, but the host must not stall */
        queue_picture(h);
        break;
    case XD_T_GET_INFO: send_info(); report_reset_reason(); send_ready(); break;   /* a (re)connecting host learns we can take a frame */
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
    case XD_T_SET_ASSIGNED: {
        /* the screen task switches between the picture and the DU's own screens (#81); a DMC repeats it every 2 s */
        int a = h->arg <= 2 ? (int)h->arg : 1;
        if (h->length) {                    /* "label\ndisplay name" */
            char text[128];
            size_t n = h->length < sizeof(text) - 1 ? h->length : sizeof(text) - 1;
            memcpy(text, payload, n);
            text[n] = 0;
            char *nl = strchr(text, '\n');
            const char *name = "";
            if (nl) { *nl = 0; name = nl + 1; }
            if (strlen(text) >= sizeof(s_label)) text[sizeof(s_label) - 1] = 0;          /* compared as stored: a long
                                                                                 label must not be written every 2 s */
            if (strlen(name) >= sizeof(s_display_name)) ((char *)name)[sizeof(s_display_name) - 1] = 0;
            if (strcmp(name, s_display_name) != 0) {
                snprintf(s_display_name, sizeof(s_display_name), "%.63s", name);
                s_screen_gen++;
            }
            if (text[0] && strcmp(text, s_label) != 0) {
                snprintf(s_label, sizeof(s_label), "%.31s", text);
                nvs_save_label();           /* only when it changes: the screens show it without a DMC too */
                s_screen_gen++;
            }
        }
        if (a == 2 && s_assign != 2) forget_pictures(false);   /* its sim went away: no stale picture when it is back */
        s_assign = a;
        break;
    }
    case XD_T_BYE:                          /* the DMC quits: "waiting for the DMC", and nothing of its session stays */
        s_host_us = 0;
        s_assign = 0;
        s_display_name[0] = 0;
        forget_pictures(true);
        s_screen_gen++;
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
    case XD_T_PING: case XD_T_SET_ASSIGNED: case XD_T_SET_MODE: case XD_T_SET_LAYOUT: case XD_T_TILE: case XD_T_BYE: case XD_T_OTA_BEGIN: case XD_T_OTA_DATA: case XD_T_OTA_END: case XD_T_REBOOT:
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
        esp_task_wdt_reset();
        if (read_exact(s_rx, chunk, 3000) != chunk) return false;
        left -= chunk;
    }
    return true;
}

static void protocol_task(void *arg)
{
    s_proto_task = xTaskGetCurrentTaskHandle();
    esp_task_wdt_add(NULL);                 /* every wait on this path is shorter than the watchdog's 30 s (#80) */
    bool was_connected = false;
    uint32_t last_session = usb_link_session();
    for (;;) {
        esp_task_wdt_reset();
        bool connected = usb_link_connected();
        uint32_t session = usb_link_session();
        /* a new session also when the host reset and configured the DU again between two looks (#80) */
        if (connected && (!was_connected || session != last_session)) {
            ESP_LOGI(TAG, "usb configured");
            s_last_stats_us = esp_timer_get_time();
            if (s_ota.active) {
                ota_fail(6);                /* an update from the old session cannot continue: give the slot back now, not
                                               after 15 s in which the new host's layout would be dropped (review 9 Oct 2026) */
            }
            s_last_seq = 0;                 /* a new host counts its pictures from the start */
            wait_pictures();                /* the old session's last pictures first */
            forget_pictures(true);          /* a new host session starts without the old one's layout (#17) or pictures (#81) */
            s_assign = 0;
            s_host_us = 0;
            s_display_name[0] = 0;
            s_screen_gen++;
            vTaskDelay(pdMS_TO_TICKS(100));
            send_ready();
        }
        was_connected = connected;
        last_session = session;
        if (s_ota.active && esp_timer_get_time() - s_ota.last_us > 15000000) {
            ota_fail(6);                     /* the host went away mid-update (also unplugged): give the flash slot back */
        }
        /* A minute without a crash confirms a new image only once a host has talked to it: an image whose USB does not
         * work (a host cannot bind it) must still roll back at the next restart, not need a serial cable (review 9 Oct
         * 2026). The crash counter starts again after a stable minute, also on an image confirmed long ago. */
        if (esp_timer_get_time() > 60000000) {
            if (!s_app_confirmed && s_host_seen) {
                confirm_app("a minute without a crash, with a host");
            }
            s_boot_count = 0;
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
        s_host_us = s_hdr_us;                   /* a DMC is there (#81) */
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
        size_t got = 0;
        if (h.length && (got = read_exact(s_rx, h.length, 3000)) != h.length) {
            ESP_LOGW(TAG, "short payload for type 0x%02x: %u of %lu bytes", h.type, (unsigned)got, (unsigned long)h.length);
            send_ready();
            continue;
        }
        bool heartbeat = h.type == XD_T_SET_ASSIGNED && (int)h.arg == s_assign;   /* every 2 s: no wait while streaming */
        if (h.type != XD_T_FRAME && h.type != XD_T_TILE && h.type != XD_T_PING && h.type != XD_T_SET_BRIGHTNESS && !heartbeat) {
            wait_pictures();                    /* layout, ident, mode, OTA ...: after the pictures sent before them */
        }
        handle_message(&h, s_rx);
        if (esp_timer_get_time() - s_last_stats_us > 2000000) send_stats();
    }
}

/* The Identify banner over live pictures, in the DU's look (display.c asks for it per picture width). */
static const uint8_t *banner_now(int w, int *h)
{
    return screens_banner(w, s_label, s_display_name, s_serial, display_get_brightness(), h);
}

static bool s_screens_ok;               /* the fonts loaded: the DU's own screens; else the old 5x7 text */

/* One of the DU's own screens, under the frame lock like every other draw (#18, #81). */
static bool show_screen(screen_kind_t kind, int progress)
{
    if (!display_ready()) return true;
    if (!s_screens_ok) {
        static const char *const words[] = {"NO USB", "NO DMC", "NOT ASSIGNED", "WAITING FOR SIM", "IDENT", "UPDATING"};
        char l2[40];
        snprintf(l2, sizeof(l2), "%.8s FW %s", s_serial, FW_VERSION);
        return show_idle_screen(words[kind], l2, 0x00A0FF);
    }
    screen_t sc = {kind, s_label, s_display_name, s_serial, FW_VERSION, progress, display_get_brightness()};
    bool ok = false;
    if (xSemaphoreTake(s_frame_mutex, pdMS_TO_TICKS(500)) == pdTRUE) {
        ok = screens_show(&sc) == ESP_OK;
        xSemaphoreGive(s_frame_mutex);
    }
    return ok;
}

static void screen_task(void *arg)
{
    /* What the panel shows when it is not a picture, independent of the protocol loop (#81):
     *   no USB -> no DMC talking (none for 6 s, or it said BYE) -> nothing assigned -> assigned but no picture yet
     *   (the sim is not showing it) -> the picture. Identify and a firmware update take precedence. */
    char l1[40], l2[40];
    int last_state = -1, last_bright = -1, last_pct = -1;
    uint32_t last_gen = 0, last_ident = 0;
    int64_t last_pct_us = 0;
    bool idle_up = false;                  /* one of our screens covers the whole panel: clear it before a picture */
    for (;;) {
        int64_t now = esp_timer_get_time();
        bool usb = usb_link_host_present();       /* a pulled cable shows only as a suspended bus */
        bool host = usb && s_host_us && now - s_host_us < HOST_TIMEOUT_US;
        bool picture = host && s_assign == 1 && has_last();
        int state;
        if (s_ota.active) state = 6;
        else if (now < s_ident_until_us) state = picture ? 1 : 7;
        else if (!usb) state = 2;
        else if (!host) state = 4;
        else if (s_assign == 0) state = 3;
        else if (!picture) state = 5;
        else state = 0;
        s_screen_state = state;
        int bright = display_get_brightness();
        int pct = s_ota.expected ? (int)((uint64_t)s_ota.got * 100 / s_ota.expected) : 0;
        bool own = state >= 2;
        bool redraw = state != last_state || s_screen_gen != last_gen
                      || ((state == 1 || state == 7) && s_ident_gen != last_ident)
                      || (own && bright != last_bright)
                      || (state == 6 && pct != last_pct && now - last_pct_us > 400000);
        if (redraw) {
            bool drawn = true;
            /* IDENT on a picture is a banner stamped on every frame (live or the kept last one) */
            if (state == 1) {
                snprintf(l1, sizeof(l1), "IDENT %s", s_label);
                snprintf(l2, sizeof(l2), "%.8s-%.8s", s_serial, s_serial + 8);
                display_set_overlay(l1, l2);
            } else {
                display_set_overlay(NULL, NULL);
            }
            switch (state) {
            case 0:
            case 1:
                /* the last frame again: without the banner (back to normal) or with it (ident); after one of our screens
                 * clear first, or it stays around a smaller picture or between tiles (#17) */
                if (xSemaphoreTake(s_frame_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
                    if (idle_up) display_fill(0x000000);
                    redraw_last();
                    xSemaphoreGive(s_frame_mutex);
                    idle_up = false;
                } else {
                    drawn = false;
                }
                break;
            case 2: drawn = show_screen(SCREEN_NO_USB, 0); break;
            case 3: drawn = show_screen(SCREEN_UNASSIGNED, 0); break;
            case 4: drawn = show_screen(SCREEN_NO_HOST, 0); break;
            case 5: drawn = show_screen(SCREEN_WAITING, 0); break;
            case 6:
                drawn = show_screen(SCREEN_UPDATING, pct);
                last_pct = pct;
                last_pct_us = now;
                break;
            default: drawn = show_screen(SCREEN_IDENTIFY, 0); break;
            }
            if (own) idle_up = true;
            if (drawn) {                   /* not drawn: try again next round */
                last_state = state;
                last_gen = s_screen_gen;
                last_ident = s_ident_gen;
                last_bright = bright;
            }
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
        err = nvs_flash_erase();
        if (err == ESP_OK) err = nvs_flash_init();
    }
    if (err != ESP_OK) {
        /* not erased: units from before #24 keep a stored serial there. The DU runs on its defaults instead (F14). */
        ESP_LOGE(TAG, "NVS not available (%s): settings are not kept", esp_err_to_name(err));
    }
    load_or_create_serial();
    nvs_load_label();
    s_reset_reason = esp_reset_reason();
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
    uint8_t *third = heap_caps_malloc(RX_BUF_SIZE, MALLOC_CAP_SPIRAM);   /* received into while a picture is drawn (#61) */
    s_tx_mutex = xSemaphoreCreateMutex();
    s_jobs = xQueueCreate(1, sizeof(picture_job_t));
    s_free = xQueueCreate(2, sizeof(uint8_t *));
    ESP_ERROR_CHECK(s_rx && s_last_jpeg && third && s_tx_mutex && s_jobs && s_free ? ESP_OK : ESP_ERR_NO_MEM);
    xQueueSend(s_free, &third, 0);
    ESP_ERROR_CHECK(usb_link_start(s_serial));

    esp_err_t derr = display_init(mode, nvs_get_int("dsivar", 0));
    if (derr != ESP_OK) {
        snprintf(s_display_error, sizeof(s_display_error), "display: %s", esp_err_to_name(derr));
        ESP_LOGE(TAG, "%s: running without a display (USB, LOG and firmware updates still work)", s_display_error);
    }
    if (display_ready()) {
        s_screens_ok = screens_init() == ESP_OK;
        if (s_screens_ok) display_set_banner_fn(banner_now);
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

    /* The task watchdog restarts a DU whose protocol or picture task hangs, instead of leaving it deaf until someone
     * power-cycles it; the panic output on the serial console shows where it hung (#80). 30 s is longer than any wait
     * on those paths (the flash erase at the start of an update included). */
    esp_task_wdt_config_t wdt = {
        .timeout_ms = 30000,
        .idle_core_mask = (1 << portNUM_PROCESSORS) - 1,
        .trigger_panic = true,
    };
    ESP_ERROR_CHECK(esp_task_wdt_reconfigure(&wdt));
    xTaskCreatePinnedToCore(picture_task, "picture", 8192, NULL, 5, NULL, 0);
    xTaskCreatePinnedToCore(protocol_task, "proto", 8192, NULL, 5, NULL, 0);   /* core 0: TinyUSB owns core 1 */
    if (!display_ready()) return;           /* headless: no screen task */
    if (nvs_get_int("diag", 0)) {
        xTaskCreatePinnedToCore(diag_task, "diag", 8192, NULL, 3, NULL, 0);
    } else {
        xTaskCreatePinnedToCore(screen_task, "screen", 8192, NULL, 3, NULL, 0);
    }
}
