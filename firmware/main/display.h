/* Panel output through the Olimex MIPI-HDMI adapter (LT8912B) and the hardware JPEG decoder. */
#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "esp_err.h"

typedef struct {
    int width;      /* active pixels of the HDMI mode */
    int height;
    int mode;       /* 0 = 768x768@60 (custom), 1 = 1024x768@60, 2 = 800x600@60, 3 = 1280x720@60 */
} display_info_t;

/* dsivar: 0 = IDF default (burst, EoTp on), 1 = EoTp off, 2 = + non-burst sync events, 3 = + non-burst sync pulses */
esp_err_t display_init(int mode, int dsivar);
display_info_t display_get_info(void);

/* Decode a JPEG with the hardware decoder and show it centred on the panel. Returns decode time in ms. */
esp_err_t display_show_jpeg(const uint8_t *jpeg, size_t len, uint32_t *decode_ms);
/* The same inside one tile of a layout: centred in the rectangle x, y, w, h; the rest of the screen is untouched. */
esp_err_t display_show_jpeg_at(const uint8_t *jpeg, size_t len, int x, int y, int w, int h, uint32_t *decode_ms);
/* Show an RGB888 buffer of w x h at x, y. */
esp_err_t display_show_rgb_at(const uint8_t *rgb, int w, int h, int x, int y);

/* Fill the panel with a colour (RGB888 as 0xRRGGBB). */
void display_fill(uint32_t rgb);

/* Show an RGB888 buffer of w x h (copied into the frame buffer, centred). */
esp_err_t display_show_rgb(const uint8_t *rgb, int w, int h);

/* Software brightness 0..100 applied to subsequent frames (the scaler board has no backlight input). */
void display_set_brightness(int percent);
/* Two-line banner stamped on every subsequent frame (empty strings = off). */
void display_set_overlay(const char *line1, const char *line2);
uint32_t display_last_draw_us(void);
void display_log_bridge_status(void);
void display_diag_set_dsi(bool pn_swap, bool lane_swap);
void display_diag_test_pattern(void);
void display_diag_set_dvi(bool dvi);
void display_set_rotation(int degrees);
