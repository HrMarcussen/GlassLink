/* The DU's own screens (#81), laid out like the mockup on a 768 x 768 grid and scaled to the panel: the GlassLink mark
 * and name at the top, the state in the middle (a coloured dot and a word, a title, a line of help), the DU's label,
 * serial and firmware at the bottom. Text is Inter, rasterised with stb_truetype from fonts built into the image
 * (firmware/tools/make_fonts.py). Frame buffer byte order is B,G,R. */
#include <math.h>
#include <stdbool.h>
#include <stdio.h>
#include <string.h>
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "display.h"
#include "screens.h"

#define STBTT_malloc(x, u) ((void)(u), heap_caps_malloc((x), MALLOC_CAP_SPIRAM))
#define STBTT_free(x, u) ((void)(u), heap_caps_free(x))
#define STB_TRUETYPE_IMPLEMENTATION
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wunused-function"
#pragma GCC diagnostic ignored "-Wsign-compare"
#pragma GCC diagnostic ignored "-Wunused-but-set-variable"
#pragma GCC diagnostic ignored "-Wmisleading-indentation"
#include "third_party/stb_truetype.h"
#pragma GCC diagnostic pop

static const char *TAG = "screens";

/* The status page's dark theme. */
#define C_BG    0x0f1115
#define C_FG    0xeceef2
#define C_DIM   0xa3acbd
#define C_ACC   0x6cb2ff
#define C_WARN  0xf5c04a
#define C_GREY  0x6b7486
#define C_TRACK 0x262b36

extern const uint8_t inter_regular_start[] asm("_binary_inter_regular_ttf_start");
extern const uint8_t inter_semibold_start[] asm("_binary_inter_semibold_ttf_start");
extern const uint8_t inter_bold_start[] asm("_binary_inter_bold_ttf_start");

typedef struct {
    stbtt_fontinfo info;
    int asc, desc;          /* font units, desc negative */
    bool ok;
} font_t;

enum { F_REGULAR, F_SEMIBOLD, F_BOLD, F_COUNT };
static font_t s_fonts[F_COUNT];
static bool s_ready;

/* A horizontal strip of the screen: rows y0 .. y0 + h - 1. Everything is drawn in screen coordinates and clipped to
 * the strip, so a screen is drawn strip by strip without a buffer for the whole picture (6 MB in 1080p). */
typedef struct {
    uint8_t *p;
    int w, h, y0;
} canvas_t;

static uint8_t *s_glyph;            /* scratch for one glyph's coverage */
static size_t s_glyph_cap;

esp_err_t screens_init(void)
{
    const uint8_t *data[F_COUNT] = {inter_regular_start, inter_semibold_start, inter_bold_start};
    for (int i = 0; i < F_COUNT; i++) {
        font_t *f = &s_fonts[i];
        f->ok = stbtt_InitFont(&f->info, data[i], stbtt_GetFontOffsetForIndex(data[i], 0)) != 0;
        if (!f->ok) {
            ESP_LOGE(TAG, "font %d unreadable", i);
            return ESP_FAIL;
        }
        int gap;
        stbtt_GetFontVMetrics(&f->info, &f->asc, &f->desc, &gap);
    }
    s_ready = true;
    return ESP_OK;
}

/* ---- drawing primitives -------------------------------------------------------------------------- */

static inline void blend(canvas_t *c, int x, int y, uint32_t rgb, int a)
{
    y -= c->y0;
    if (a <= 0 || x < 0 || y < 0 || x >= c->w || y >= c->h) return;
    uint8_t *p = c->p + ((size_t)y * c->w + x) * 3;
    int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
    if (a >= 255) {
        p[0] = b; p[1] = g; p[2] = r;
        return;
    }
    p[0] = (uint8_t)(p[0] + ((b - p[0]) * a) / 255);
    p[1] = (uint8_t)(p[1] + ((g - p[1]) * a) / 255);
    p[2] = (uint8_t)(p[2] + ((r - p[2]) * a) / 255);
}

static void fill(canvas_t *c, int x, int y, int w, int h, uint32_t rgb)
{
    y -= c->y0;
    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > c->w) w = c->w - x;
    if (y + h > c->h) h = c->h - y;
    if (w <= 0 || h <= 0) return;
    uint8_t px[3] = {rgb & 0xFF, (rgb >> 8) & 0xFF, (rgb >> 16) & 0xFF};
    uint8_t *row0 = c->p + ((size_t)y * c->w + x) * 3;
    for (int i = 0; i < w; i++) memcpy(row0 + i * 3, px, 3);
    for (int j = 1; j < h; j++) memcpy(row0 + (size_t)j * c->w * 3, row0, (size_t)w * 3);
}

/* A rounded rectangle with smooth edges (signed distance per pixel). A circle is one with r = half its size. */
static void round_rect(canvas_t *c, float x, float y, float w, float h, float r, uint32_t rgb)
{
    float cx = x + w / 2, cy = y + h / 2, hx = w / 2 - r, hy = h / 2 - r;
    int ya = (int)floorf(y), yb = (int)ceilf(y + h);
    if (ya < c->y0) ya = c->y0;
    if (yb > c->y0 + c->h) yb = c->y0 + c->h;
    for (int py = ya; py < yb; py++) {
        for (int px = (int)floorf(x); px < (int)ceilf(x + w); px++) {
            float qx = fabsf(px + 0.5f - cx) - hx, qy = fabsf(py + 0.5f - cy) - hy;
            float ox = qx > 0 ? qx : 0, oy = qy > 0 ? qy : 0;
            float d = sqrtf(ox * ox + oy * oy) + fminf(fmaxf(qx, qy), 0) - r;
            float a = 0.5f - d;
            if (a > 0) blend(c, px, py, rgb, a >= 1 ? 255 : (int)(a * 255));
        }
    }
}

static float seg_dist(float px, float py, float ax, float bx, float y)
{
    float cx = px < ax ? ax : (px > bx ? bx : px);
    return sqrtf((px - cx) * (px - cx) + (py - y) * (py - y));
}

/* The GlassLink mark (the icon): an attitude indicator, sky over earth, white horizon, yellow wings, in a rounded
 * square; drawn on a 64-unit grid, 4 x 4 samples per pixel. */
static void logo(canvas_t *c, int x0, int y0, int size)
{
    float u = 64.0f / size;
    for (int py = 0; py < size; py++) {
        if (y0 + py < c->y0 || y0 + py >= c->y0 + c->h) continue;
        for (int px = 0; px < size; px++) {
            int n = 0, sr = 0, sg = 0, sb = 0;
            for (int j = 0; j < 4; j++) {
                for (int i = 0; i < 4; i++) {
                    float lx = (px + (i + 0.5f) / 4) * u, ly = (py + (j + 0.5f) / 4) * u;
                    float qx = fabsf(lx - 32) - 16, qy = fabsf(ly - 32) - 16;
                    float ox = qx > 0 ? qx : 0, oy = qy > 0 ? qy : 0;
                    if (sqrtf(ox * ox + oy * oy) + fminf(fmaxf(qx, qy), 0) > 14) continue;   /* outside the square */
                    uint32_t col = ly < 32 ? 0x2f8fe0 : 0x8a5a2b;
                    if (fabsf(ly - 32) <= 1.5f) col = 0xffffff;
                    if (seg_dist(lx, ly, 12, 24, 32) <= 2.5f || seg_dist(lx, ly, 40, 52, 32) <= 2.5f) col = 0xffd030;
                    if ((lx - 32) * (lx - 32) + (ly - 32) * (ly - 32) <= 9) col = 0xffffff;
                    n++; sr += (col >> 16) & 0xFF; sg += (col >> 8) & 0xFF; sb += col & 0xFF;
                }
            }
            if (n) blend(c, x0 + px, y0 + py, ((uint32_t)(sr / n) << 16) | ((uint32_t)(sg / n) << 8) | (uint32_t)(sb / n), n * 255 / 16);
        }
    }
}

/* ---- text ---------------------------------------------------------------------------------------- */

static int utf8_next(const char **s)
{
    const unsigned char *p = (const unsigned char *)*s;
    int cp;
    if (p[0] < 0x80) { cp = p[0]; *s += 1; }
    else if ((p[0] & 0xE0) == 0xC0 && p[1]) { cp = ((p[0] & 0x1F) << 6) | (p[1] & 0x3F); *s += 2; }
    else if ((p[0] & 0xF0) == 0xE0 && p[1] && p[2]) { cp = ((p[0] & 0x0F) << 12) | ((p[1] & 0x3F) << 6) | (p[2] & 0x3F); *s += 3; }
    else { cp = '?'; *s += 1; }
    return cp;
}

/* Width of a run of text in pixels; `track` is extra space after every character (CSS letter-spacing). */
static float text_width_n(int f, float px, const char *s, size_t n, float track)
{
    const stbtt_fontinfo *fi = &s_fonts[f].info;
    float scale = stbtt_ScaleForMappingEmToPixels(fi, px), x = 0;
    const char *end = s + n;
    while (s < end && *s) {
        int cp = utf8_next(&s), adv, lsb;
        stbtt_GetCodepointHMetrics(fi, cp, &adv, &lsb);
        x += adv * scale + track;
        if (s < end && *s) {
            const char *peek = s;
            x += scale * stbtt_GetCodepointKernAdvance(fi, cp, utf8_next(&peek));
        }
    }
    return x;
}

static float text_width(int f, float px, const char *s, float track)
{
    return text_width_n(f, px, s, strlen(s), track);
}

/* The baseline of a line of text whose line box (font size px, line height lh times px) starts at top. */
static float baseline(int f, float px, float lh, float top)
{
    const font_t *ft = &s_fonts[f];
    float scale = stbtt_ScaleForMappingEmToPixels(&ft->info, px);
    float content = (ft->asc - ft->desc) * scale;
    float box = lh > 0 ? lh * px : content;
    return top + (box - content) / 2 + ft->asc * scale;
}

static float line_height(int f, float px, float lh)
{
    const font_t *ft = &s_fonts[f];
    return lh > 0 ? lh * px : (ft->asc - ft->desc) * stbtt_ScaleForMappingEmToPixels(&ft->info, px);
}

static void draw_text_n(canvas_t *c, int f, float px, float x, float base, uint32_t rgb, const char *s, size_t n, float track)
{
    const stbtt_fontinfo *fi = &s_fonts[f].info;
    float scale = stbtt_ScaleForMappingEmToPixels(fi, px);
    int by = (int)lroundf(base);
    const char *end = s + n;
    while (s < end && *s) {
        int cp = utf8_next(&s), adv, lsb, x0, y0, x1, y1;
        int ix = (int)floorf(x);
        float shift = x - ix;
        stbtt_GetCodepointHMetrics(fi, cp, &adv, &lsb);
        stbtt_GetCodepointBitmapBoxSubpixel(fi, cp, scale, scale, shift, 0, &x0, &y0, &x1, &y1);
        int gw = x1 - x0, gh = y1 - y0;
        if (gw > 0 && gh > 0 && by + y1 > c->y0 && by + y0 < c->y0 + c->h) {    /* only glyphs in this strip */
            size_t need = (size_t)gw * gh;
            if (need > s_glyph_cap) {
                heap_caps_free(s_glyph);
                s_glyph = heap_caps_malloc(need, MALLOC_CAP_SPIRAM);
                s_glyph_cap = s_glyph ? need : 0;
            }
            if (s_glyph) {
                stbtt_MakeCodepointBitmapSubpixel(fi, s_glyph, gw, gh, gw, scale, scale, shift, 0, cp);
                for (int j = 0; j < gh; j++) {
                    for (int i = 0; i < gw; i++) {
                        blend(c, ix + x0 + i, by + y0 + j, rgb, s_glyph[j * gw + i]);
                    }
                }
            }
        }
        x += adv * scale + track;
        if (s < end && *s) {
            const char *peek = s;
            x += scale * stbtt_GetCodepointKernAdvance(fi, cp, utf8_next(&peek));
        }
    }
}

static void draw_text(canvas_t *c, int f, float px, float x, float base, uint32_t rgb, const char *s, float track)
{
    draw_text_n(c, f, px, x, base, rgb, s, strlen(s), track);
}

/* Breaks text into lines no wider than max_w, at spaces; returns the number of lines (at most max_lines) and where
 * each starts and how long it is. */
static int wrap(int f, float px, const char *s, float max_w, const char **starts, size_t *lens, int max_lines)
{
    int n = 0;
    while (*s && n < max_lines) {
        while (*s == ' ') s++;
        const char *line = s, *last_break = NULL;
        const char *p = s;
        while (*p) {
            const char *word_end = p;
            while (*word_end && *word_end != ' ') word_end++;
            if (text_width_n(f, px, line, (size_t)(word_end - line), 0) > max_w && last_break) break;
            last_break = word_end;
            p = word_end;
            while (*p == ' ') p++;
        }
        if (!last_break) last_break = p;
        starts[n] = line;
        lens[n] = (size_t)(last_break - line);
        n++;
        s = last_break;
    }
    return n;
}

/* The largest size up to px at which the text fits in max_w (not below min_px). */
static float fit(int f, float px, float min_px, const char *s, float track_em, float max_w)
{
    while (px > min_px && text_width(f, px, s, track_em * px) > max_w) px -= 1;
    return px;
}

/* ---- screens ------------------------------------------------------------------------------------- */

static uint8_t *s_buf;
static size_t s_buf_size;

static void dim_all(canvas_t *c, int brightness)
{
    if (brightness >= 100) return;
    if (brightness < 0) brightness = 0;
    uint8_t lut[256];
    for (int i = 0; i < 256; i++) lut[i] = (uint8_t)((i * brightness + 50) / 100);
    size_t n = (size_t)c->w * c->h * 3;
    for (size_t i = 0; i < n; i++) c->p[i] = lut[c->p[i]];
}

static void short_serial(const char *serial, char *out, size_t size)
{
    snprintf(out, size, "%.8s", serial ? serial : "");
    for (char *p = out; *p; p++) if (*p >= 'a' && *p <= 'f') *p -= 32;
}

/* Draws the part of screen s that lies in strip c (the whole layout is worked out again for every strip: cheap). */
static void draw_scene(canvas_t *c, const screen_t *s, int W, int H)
{
    display_info_t di = {.width = W, .height = H};
    float k = (di.width < di.height ? di.width : di.height) / 768.0f;
    float pad = 56 * k, content_w = di.width - 2 * pad;
    const char *label = s->label && s->label[0] ? s->label : "";
    const char *display = s->display && s->display[0] ? s->display : "";
    fill(c, 0, 0, W, H, C_BG);

    /* header: the mark and the name */
    float mark = 44 * k;
    logo(c, (int)lroundf(pad), (int)lroundf(pad), (int)lroundf(mark));
    draw_text(c, F_SEMIBOLD, 30 * k, pad + mark + 16 * k, baseline(F_SEMIBOLD, 30 * k, 0, pad + (mark - line_height(F_SEMIBOLD, 30 * k, 0)) / 2),
              C_FG, "GlassLink", 0.3f * k);

    /* footer: label on the left, serial and firmware on the right (Identify: the whole serial) */
    char serial8[12], right[64];
    short_serial(s->serial, serial8, sizeof(serial8));
    float fpx = 20 * k, foot_h = line_height(F_REGULAR, fpx, 0), foot_base = baseline(F_REGULAR, fpx, 0, H - pad - foot_h);
    if (s->kind == SCREEN_IDENTIFY) {
        char full[40];
        snprintf(full, sizeof(full), "%.8s-%.8s", s->serial ? s->serial : "", s->serial && strlen(s->serial) > 8 ? s->serial + 8 : "");
        for (char *p = full; *p; p++) if (*p >= 'a' && *p <= 'f') *p -= 32;
        draw_text(c, F_REGULAR, fpx, pad, foot_base, C_DIM, full, 0);
        snprintf(right, sizeof(right), "FW %s", s->fw ? s->fw : "");
    } else {
        if (label[0]) draw_text(c, F_SEMIBOLD, fpx, pad, foot_base, C_FG, label, 0);
        snprintf(right, sizeof(right), "%s \xC2\xB7 FW %s", serial8, s->fw ? s->fw : "");
    }
    draw_text(c, F_REGULAR, fpx, W - pad - text_width(F_REGULAR, fpx, right, 0), foot_base, C_DIM, right, 0);

    /* the middle, centred between header and footer */
    float top_free = pad + mark, bottom_free = H - pad - foot_h;
    if (s->kind == SCREEN_IDENTIFY) {
        int border = (int)lroundf(12 * k);
        fill(c, 0, 0, W, border, C_ACC);
        fill(c, 0, H - border, W, border, C_ACC);
        fill(c, 0, 0, border, H, C_ACC);
        fill(c, W - border, 0, border, H, C_ACC);
        const char *name = label[0] ? label : serial8;
        float spx = 20 * k, lpx = fit(F_BOLD, 200 * k, 60 * k, name, -0.02f, content_w), dpx = 26 * k;
        float h1 = line_height(F_SEMIBOLD, spx, 0), h2 = line_height(F_BOLD, lpx, 1.0f), h3 = display[0] ? line_height(F_REGULAR, dpx, 0) : 0;
        float total = h1 + 8 * k + h2 + (display[0] ? 8 * k + h3 : 0), y = top_free + (bottom_free - top_free - total) / 2;
        float w1 = text_width(F_SEMIBOLD, spx, "IDENTIFY", 2.4f * k);
        draw_text(c, F_SEMIBOLD, spx, (W - w1) / 2, baseline(F_SEMIBOLD, spx, 0, y), C_ACC, "IDENTIFY", 2.4f * k);
        y += h1 + 8 * k;
        float w2 = text_width(F_BOLD, lpx, name, -0.02f * lpx);
        draw_text(c, F_BOLD, lpx, (W - w2) / 2, baseline(F_BOLD, lpx, 1.0f, y), C_FG, name, -0.02f * lpx);
        y += h2 + 8 * k;
        if (display[0]) {
            draw_text(c, F_REGULAR, dpx, (W - text_width(F_REGULAR, dpx, display, 0)) / 2, baseline(F_REGULAR, dpx, 0, y), C_DIM, display, 0);
        }
        return;
    }

    const char *pill, *title, *body;
    uint32_t dot;
    char body_buf[120];
    int title_font = F_SEMIBOLD;
    float title_px = 60 * k, title_lh = 1.1f;
    switch (s->kind) {
    case SCREEN_NO_USB:
        pill = "NO USB"; dot = C_GREY; title = "Waiting for the PC"; body = "Connect this DU to the sim PC with USB.";
        break;
    case SCREEN_NO_HOST:
        pill = "USB CONNECTED"; dot = C_WARN; title = "Waiting for the DMC"; body = "Start the sim, or start the GlassLink DMC on the PC.";
        break;
    case SCREEN_WAITING:
        pill = "WAITING FOR THE SIM"; dot = C_WARN; title = display[0] ? display : "Waiting for the sim";
        body = "The picture appears as soon as the sim shows this display.";
        title_font = F_BOLD; title_lh = 1.05f;
        title_px = fit(F_BOLD, 72 * k, 36 * k, title, 0, content_w);
        break;
    case SCREEN_UPDATING:
        pill = "UPDATING"; dot = C_ACC; title = "Updating firmware"; body = "Do not unplug the DU.";
        break;
    case SCREEN_UNASSIGNED:
    default:
        pill = "CONNECTED"; dot = C_ACC; title = "Not assigned";
        snprintf(body_buf, sizeof(body_buf), "Choose a display for %s on the GlassLink status page.", label[0] ? label : "this DU");
        body = body_buf;
        break;
    }

    float ppx = 20 * k, bpx = 26 * k, gap = 20 * k, bar = 14 * k;
    const char *lines[4];
    size_t lens[4];
    int nlines = wrap(F_REGULAR, bpx, body, 600 * k < content_w ? 600 * k : content_w, lines, lens, 4);
    float pill_h = line_height(F_SEMIBOLD, ppx, 0), title_h = line_height(title_font, title_px, title_lh);
    float body_line = line_height(F_REGULAR, bpx, 1.4f), prog_h = s->kind == SCREEN_UPDATING ? line_height(F_SEMIBOLD, bpx, 0) : 0;
    float total = pill_h + gap + title_h + gap + (prog_h > 0 ? prog_h + gap : 0) + nlines * body_line;
    float y = top_free + (bottom_free - top_free - total) / 2;

    round_rect(c, pad, y + (pill_h - bar) / 2, bar, bar, bar / 2, dot);
    draw_text(c, F_SEMIBOLD, ppx, pad + bar + 12 * k, baseline(F_SEMIBOLD, ppx, 0, y), C_DIM, pill, 2.4f * k);
    y += pill_h + gap;
    draw_text(c, title_font, title_px, pad, baseline(title_font, title_px, title_lh, y), C_FG, title, 0);
    y += title_h + gap;
    if (prog_h > 0) {
        int pct = s->progress < 0 ? 0 : (s->progress > 100 ? 100 : s->progress);
        char pct_text[8];
        snprintf(pct_text, sizeof(pct_text), "%d %%", pct);
        float tw = text_width(F_SEMIBOLD, bpx, "100 %", 0), track_w = content_w - tw - 20 * k;
        round_rect(c, pad, y + (prog_h - bar) / 2, track_w, bar, bar / 2, C_TRACK);
        if (pct > 0) round_rect(c, pad, y + (prog_h - bar) / 2, fmaxf(bar, track_w * pct / 100), bar, bar / 2, C_ACC);
        draw_text(c, F_SEMIBOLD, bpx, W - pad - text_width(F_SEMIBOLD, bpx, pct_text, 0), baseline(F_SEMIBOLD, bpx, 0, y), C_FG, pct_text, 0);
        y += prog_h + gap;
    }
    for (int i = 0; i < nlines; i++) {
        draw_text_n(c, F_REGULAR, bpx, pad, baseline(F_REGULAR, bpx, 1.4f, y), C_DIM, lines[i], lens[i], 0);
        y += body_line;
    }
}

esp_err_t screens_show(const screen_t *s)
{
    if (!s_ready) return ESP_ERR_INVALID_STATE;
    display_info_t di = display_get_info();
    int strip = di.width > 1024 ? 96 : 128;
    size_t size = (size_t)di.width * strip * 3;
    if (size > s_buf_size) {
        heap_caps_free(s_buf);
        s_buf = heap_caps_malloc(size, MALLOC_CAP_SPIRAM);
        s_buf_size = s_buf ? size : 0;
        if (!s_buf) return ESP_ERR_NO_MEM;
    }
    for (int y0 = 0; y0 < di.height; y0 += strip) {
        int h = di.height - y0 < strip ? di.height - y0 : strip;
        canvas_t c = {s_buf, di.width, h, y0};
        draw_scene(&c, s, di.width, di.height);
        dim_all(&c, s->brightness);
        esp_err_t err = display_show_rgb_at(c.p, c.w, c.h, 0, y0);
        if (err != ESP_OK) return err;
    }
    return ESP_OK;
}

/* ---- the Identify banner over live pictures ------------------------------------------------------- */

static uint8_t *s_banner;
static int s_banner_w, s_banner_h, s_banner_bright;
static char s_banner_key[160];

const uint8_t *screens_banner(int w, const char *label, const char *display, const char *serial, int brightness, int *h)
{
    if (!s_ready || w <= 0) return NULL;
    char key[160];
    snprintf(key, sizeof(key), "%s|%s|%s", label ? label : "", display ? display : "", serial ? serial : "");
    if (s_banner && w == s_banner_w && brightness == s_banner_bright && strcmp(key, s_banner_key) == 0) {
        *h = s_banner_h;
        return s_banner;
    }
    float k = w / 768.0f;
    if (k < 0.4f) k = 0.4f;
    int bh = (int)lroundf(112 * k);
    heap_caps_free(s_banner);
    s_banner = heap_caps_malloc((size_t)w * bh * 3, MALLOC_CAP_SPIRAM);
    if (!s_banner) return NULL;
    canvas_t cv = {s_banner, w, bh, 0}, *c = &cv;
    fill(c, 0, 0, w, bh, C_BG);
    fill(c, 0, bh - (int)lroundf(4 * k), w, (int)lroundf(4 * k), C_ACC);
    char serial8[12];
    short_serial(serial, serial8, sizeof(serial8));
    const char *name = label && label[0] ? label : serial8;
    float x = 32 * k, lpx = 60 * k;
    draw_text(c, F_BOLD, lpx, x, baseline(F_BOLD, lpx, 0, (bh - 4 * k - line_height(F_BOLD, lpx, 0)) / 2), C_FG, name, 0);
    x += text_width(F_BOLD, lpx, name, 0) + 28 * k;
    float spx = 18 * k, dpx = 24 * k, h1 = line_height(F_SEMIBOLD, spx, 0), h2 = line_height(F_REGULAR, dpx, 0);
    float y = (bh - 4 * k - h1 - 4 * k - h2) / 2;
    draw_text(c, F_SEMIBOLD, spx, x, baseline(F_SEMIBOLD, spx, 0, y), C_ACC, "IDENTIFY", 2 * k);
    draw_text(c, F_REGULAR, dpx, x, baseline(F_REGULAR, dpx, 0, y + h1 + 4 * k), C_DIM, display && display[0] ? display : serial8, 0);
    dim_all(c, brightness);
    s_banner_w = w; s_banner_h = bh; s_banner_bright = brightness;
    snprintf(s_banner_key, sizeof(s_banner_key), "%s", key);
    *h = bh;
    return s_banner;
}
