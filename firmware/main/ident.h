/* Idle / identify screens rendered without a font library: big blocky digits from a 5x7 pattern. */
#pragma once
#include <stdint.h>
#include <stddef.h>

/* Draw `text` (0-9, A-F, a few letters, '-') as blocky glyphs into an RGB888 buffer of w x h at (x, y). */
void ident_draw_text(uint8_t *rgb, int w, int h, int x, int y, int scale, uint32_t colour, const char *text);
