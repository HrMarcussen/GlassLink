/* LT8912B MIPI-DSI -> HDMI output (esp_lcd_lt8912b component) + hardware JPEG decode (esp_driver_jpeg).
 *
 * Pin assumptions for the Waveshare ESP32-P4-NANO: I2C on GPIO7 (SDA) / GPIO8 (SCL) is routed to the
 * 15-pin DSI connector (Raspberry Pi pinout), which the Olimex MIPI-HDMI adapter uses to control the LT8912B.
 * No reset GPIO (the adapter pulls reset up itself).
 */
#include <string.h>
#include "esp_log.h"
#include "esp_check.h"
#include "esp_heap_caps.h"
#include "esp_timer.h"
#include "driver/i2c_master.h"
#include "driver/jpeg_decode.h"
#include "driver/ppa.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_mipi_dsi.h"
#include "esp_lcd_lt8912b.h"
#include "esp_ldo_regulator.h"
#include "hal/mipi_dsi_host_ll.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "display.h"
#include "ident.h"

static const char *TAG = "display";

#define I2C_PORT        0
#define I2C_SDA_GPIO    7
#define I2C_SCL_GPIO    8
#define I2C_SPEED_HZ    400000
#define DSI_LANES       2
#define DSI_LDO_CHAN    3       /* MIPI DPHY power: LDO channel 3 at 2.5 V on the P4 */
#define DSI_LDO_MV      2500

/* Custom 768x768@60 timing for the 1:1 panels (CVT-like blanking, ~47 MHz pixel clock) */
#define XD_768_HFP 40
#define XD_768_HS  80
#define XD_768_HBP 104
#define XD_768_VFP 3
#define XD_768_VS  4
#define XD_768_VBP 20
#define XD_768_PCLK_MHZ 48

static esp_lcd_panel_handle_t s_panel;
static esp_lcd_panel_io_handle_t s_io_main, s_io_cec;   /* bridge register pages 0x48 / 0x49, kept for diagnostics */
static esp_lcd_panel_lt8912b_video_timing_t s_vt;
static display_info_t s_info;
static jpeg_decoder_handle_t s_jpeg;
static uint8_t *s_rgb;              /* decoder output buffer (RGB888, DMA capable) */
static size_t s_rgb_size;
static SemaphoreHandle_t s_draw_done;   /* given by the DPI driver when a draw_bitmap copy has finished */
static SemaphoreHandle_t s_draw_lock;   /* one draw at a time: the screen task and the protocol task both draw (#18) */
static SemaphoreHandle_t s_fb_free;     /* given when a frame buffer we flipped away from may be written again (#23) */
static volatile bool s_flip_wait;       /* a flip happened: wait for s_fb_free before rendering into the other buffer */
static int s_brightness = 100;
static uint8_t s_lut[256];          /* brightness lookup (CPU fallback), rebuilt by display_set_brightness */
/* Hardware dimming: the PPA blends the decoded frame over black with alpha = brightness. The CPU loop over
 * 1.7 MB of PSRAM cost ~78 ms per frame (9 fps cap at any brightness below 100); the PPA does it in a few ms. */
static ppa_client_handle_t s_ppa;
static uint8_t *s_dim;              /* PPA output buffer (same size/alignment as s_rgb) */
static size_t s_dim_size;
static uint8_t *s_black;            /* all-zero background picture for the blend */
static void *s_fb[2];               /* the DPI panel's two frame buffers */
static int s_front;                 /* index of the one on screen; full-size dimmed frames render into the other and flip */
static char s_overlay1[40], s_overlay2[40];   /* banner stamped on every frame while non-empty (IDENT) */
static int s_rotation = 0;

static void lt_write(esp_lcd_panel_io_handle_t io, uint8_t reg, uint8_t val);
static void stamp_overlay(uint8_t *bgr, int w, int h);

static esp_err_t make_panel(int mode, int dsivar)
{
    /* DSI PHY power */
    esp_ldo_channel_handle_t ldo = NULL;
    esp_ldo_channel_config_t ldo_cfg = {.chan_id = DSI_LDO_CHAN, .voltage_mv = DSI_LDO_MV};
    ESP_RETURN_ON_ERROR(esp_ldo_acquire_channel(&ldo_cfg, &ldo), TAG, "ldo");

    /* I2C master for the bridge */
    i2c_master_bus_handle_t i2c = NULL;
    i2c_master_bus_config_t i2c_cfg = {
        .i2c_port = I2C_PORT,
        .sda_io_num = I2C_SDA_GPIO,
        .scl_io_num = I2C_SCL_GPIO,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    ESP_RETURN_ON_ERROR(i2c_new_master_bus(&i2c_cfg, &i2c), TAG, "i2c");

    esp_lcd_panel_io_handle_t io_main = NULL, io_cec = NULL, io_avi = NULL;
    esp_lcd_panel_io_i2c_config_t c_main = LT8912B_IO_CFG(I2C_SPEED_HZ, LT8912B_IO_I2C_MAIN_ADDRESS);
    esp_lcd_panel_io_i2c_config_t c_cec = LT8912B_IO_CFG(I2C_SPEED_HZ, LT8912B_IO_I2C_CEC_ADDRESS);
    esp_lcd_panel_io_i2c_config_t c_avi = LT8912B_IO_CFG(I2C_SPEED_HZ, LT8912B_IO_I2C_AVI_ADDRESS);
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_i2c(i2c, &c_main, &io_main), TAG, "io main");
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_i2c(i2c, &c_cec, &io_cec), TAG, "io cec");
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_i2c(i2c, &c_avi, &io_avi), TAG, "io avi");
    s_io_main = io_main;
    s_io_cec = io_cec;

    /* DSI bus */
    esp_lcd_dsi_bus_handle_t dsi = NULL;
    esp_lcd_dsi_bus_config_t bus_cfg = LT8912B_PANEL_BUS_DSI_2CH_CONFIG();
    ESP_RETURN_ON_ERROR(esp_lcd_new_dsi_bus(&bus_cfg, &dsi), TAG, "dsi bus");

    /* Mode tables */
    static esp_lcd_dpi_panel_config_t dpi_768 = {
        .dpi_clk_src = MIPI_DSI_DPI_CLK_SRC_DEFAULT,
        .dpi_clock_freq_mhz = XD_768_PCLK_MHZ,
        .virtual_channel = 0,
        .in_color_format = LCD_COLOR_FMT_RGB888,
        .num_fbs = 2,
        .video_timing = {
            .h_size = 768, .v_size = 768,
            .hsync_back_porch = XD_768_HBP, .hsync_pulse_width = XD_768_HS, .hsync_front_porch = XD_768_HFP,
            .vsync_back_porch = XD_768_VBP, .vsync_pulse_width = XD_768_VS, .vsync_front_porch = XD_768_VFP,
        },
        .flags.disable_lp = true,
    };
    static esp_lcd_dpi_panel_config_t dpi_1024 = LT8912B_1024x768_PANEL_60HZ_DPI_CONFIG_WITH_FBS(2);
    static esp_lcd_dpi_panel_config_t dpi_800 = LT8912B_800x600_PANEL_60HZ_DPI_CONFIG_WITH_FBS(2);
    static esp_lcd_dpi_panel_config_t dpi_720 = LT8912B_1280x720_PANEL_60HZ_DPI_CONFIG_WITH_FBS(2);
    /* Copy decoded pictures into the frame buffer with the 2D-DMA engine instead of the CPU. */
    dpi_768.flags.use_dma2d = true;
    dpi_1024.flags.use_dma2d = true;
    dpi_800.flags.use_dma2d = true;
    dpi_720.flags.use_dma2d = true;

    esp_lcd_panel_lt8912b_video_timing_t vt_768 = {
        .hfp = XD_768_HFP, .hs = XD_768_HS, .hbp = XD_768_HBP, .hact = 768, .htotal = 768 + XD_768_HFP + XD_768_HS + XD_768_HBP,
        .vfp = XD_768_VFP, .vs = XD_768_VS, .vbp = XD_768_VBP, .vact = 768, .vtotal = 768 + XD_768_VFP + XD_768_VS + XD_768_VBP,
        .h_polarity = 1, .v_polarity = 1, .vic = 0, .aspect_ratio = LT8912B_ASPECT_RATION_NO, .pclk_mhz = XD_768_PCLK_MHZ,
    };
    esp_lcd_panel_lt8912b_video_timing_t vt_1024 = ESP_LCD_LT8912B_VIDEO_TIMING_1024x768_60Hz();
    esp_lcd_panel_lt8912b_video_timing_t vt_800 = ESP_LCD_LT8912B_VIDEO_TIMING_800x600_60Hz();
    esp_lcd_panel_lt8912b_video_timing_t vt_720 = ESP_LCD_LT8912B_VIDEO_TIMING_1280x720_60Hz();
    /* 1080p: one frame buffer (12 MB for two would not leave room for the decode buffers), 30 Hz on two DSI lanes */
    static esp_lcd_dpi_panel_config_t dpi_1080 = LT8912B_1920x1080_PANEL_30HZ_DPI_CONFIG_WITH_FBS(1);
    esp_lcd_panel_lt8912b_video_timing_t vt_1080 = ESP_LCD_LT8912B_VIDEO_TIMING_1920x1080_30Hz();
    dpi_1080.flags.use_dma2d = true;

    lt8912b_vendor_config_t vendor = {.mipi_config = {.dsi_bus = dsi, .lane_num = DSI_LANES}};
    switch (mode) {
    case 1: vendor.mipi_config.dpi_config = &dpi_1024; vendor.video_timing = vt_1024; s_info = (display_info_t){1024, 768, 1}; break;
    case 2: vendor.mipi_config.dpi_config = &dpi_800;  vendor.video_timing = vt_800;  s_info = (display_info_t){800, 600, 2}; break;
    case 3: vendor.mipi_config.dpi_config = &dpi_720;  vendor.video_timing = vt_720;  s_info = (display_info_t){1280, 720, 3}; break;
    case 4: vendor.mipi_config.dpi_config = &dpi_1080; vendor.video_timing = vt_1080; s_info = (display_info_t){1920, 1080, 4}; break;
    default: vendor.mipi_config.dpi_config = &dpi_768; vendor.video_timing = vt_768;  s_info = (display_info_t){768, 768, 0}; break;
    }
    s_vt = vendor.video_timing;
    esp_lcd_panel_dev_config_t panel_cfg = {
        .bits_per_pixel = 24,
        .rgb_ele_order = LCD_RGB_ELEMENT_ORDER_RGB,
        .reset_gpio_num = -1,
        .vendor_config = &vendor,
    };
    esp_lcd_panel_lt8912b_io_t io_all = {.main = io_main, .cec_dsi = io_cec, .avi = io_avi};
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_lt8912b(&io_all, &panel_cfg, &s_panel), TAG, "lt8912b");
    /* DSI host variants (bench diagnostics). ESP-IDF default: burst mode with sync pulses, EoTp packets on.
     * The Linux LT8912B driver asks for non-burst (sync events) and no EoTp packets. */
    if (dsivar >= 1) {
        mipi_dsi_host_ll_enable_tx_eotp(&MIPI_DSI_HOST, false, false);
    }
    if (dsivar == 2) {
        mipi_dsi_host_ll_dpi_set_video_burst_type(&MIPI_DSI_HOST, MIPI_DSI_LL_VIDEO_NON_BURST_WITH_SYNC_EVENTS);
    } else if (dsivar == 3) {
        mipi_dsi_host_ll_dpi_set_video_burst_type(&MIPI_DSI_HOST, MIPI_DSI_LL_VIDEO_NON_BURST_WITH_SYNC_PULSES);
    }
    ESP_LOGI(TAG, "DSI variant %d: eotp=%s video=%s", dsivar, dsivar >= 1 ? "off" : "on",
             dsivar == 2 ? "non-burst/sync-events" : dsivar == 3 ? "non-burst/sync-pulses" : "burst/sync-pulses");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_reset(s_panel), TAG, "reset");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_init(s_panel), TAG, "init");
    /* The bridge has no reset line and keeps its registers across our reboots: make sure the internal pattern
     * generator (cec page 0x70 bit 7) is off, otherwise it masks the DSI picture. */
    lt_write(s_io_cec, 0x70, 0x00);
    lt_write(s_io_cec, 0x71, 0x00);
    ESP_LOGI(TAG, "HDMI mode %dx%d ready=%d", s_info.width, s_info.height, esp_lcd_panel_lt8912b_is_ready(s_panel));
    return ESP_OK;
}

static int lt_read(esp_lcd_panel_io_handle_t io, uint8_t reg)
{
    uint8_t v = 0;
    return (io && esp_lcd_panel_io_rx_param(io, reg, &v, 1) == ESP_OK) ? v : -1;
}

static void lt_write(esp_lcd_panel_io_handle_t io, uint8_t reg, uint8_t val)
{
    esp_lcd_panel_io_tx_param(io, reg, (uint8_t[]) {val}, 1);
}

/* Diagnostics: what the bridge measures on its MIPI input (regs 0x9c..0x9f) and the HDMI hot-plug state. */
void display_log_bridge_status(void)
{
    int hl = lt_read(s_io_main, 0x9c), hh = lt_read(s_io_main, 0x9d);
    int vl = lt_read(s_io_main, 0x9e), vh = lt_read(s_io_main, 0x9f);
    ESP_LOGI(TAG, "LT8912B mipi-in h=%d v=%d (raw %02x%02x %02x%02x) hpd=%d",
             (hh << 8) | hl, (vh << 8) | vl, hh & 0xff, hl & 0xff, vh & 0xff, vl & 0xff,
             s_panel ? esp_lcd_panel_lt8912b_is_ready(s_panel) : -1);
}

/* Diagnostics: change the DSI receiver's P/N polarity and lane order at run time, then reset the MIPI RX logic
 * (same register sequence the driver uses at init: main 0x3e, cec 0x15, main 0x03/0x05 pulses). */
void display_diag_set_dsi(bool pn_swap, bool lane_swap)
{
    lt_write(s_io_main, 0x3e, pn_swap ? 0xf6 : 0xd6);
    lt_write(s_io_cec, 0x15, lane_swap ? 0xa8 : 0x00);
    lt_write(s_io_main, 0x03, 0x7f);
    vTaskDelay(pdMS_TO_TICKS(10));
    lt_write(s_io_main, 0x03, 0xff);
    lt_write(s_io_main, 0x05, 0xfb);
    vTaskDelay(pdMS_TO_TICKS(10));
    lt_write(s_io_main, 0x05, 0xff);
    ESP_LOGI(TAG, "DSI rx: pn_swap=%d lane_swap=%d", pn_swap, lane_swap);
}

/* Diagnostics: HDMI (infoframes, 0x01) or DVI (plain RGB, 0x00) output mode, register 0xB2 on the main page. */
void display_diag_set_dvi(bool dvi)
{
    lt_write(s_io_main, 0xB2, dvi ? 0x00 : 0x01);
    ESP_LOGI(TAG, "bridge output mode: %s", dvi ? "DVI" : "HDMI");
}

/* Diagnostics: switch the bridge to its internal pattern generator at the current HDMI timing. The DSI input is
 * ignored while this is on; a picture on the panel proves HDMI -> scaler -> panel. Mirrors the driver's
 * ENABLE_TEST_PATTERN code. Only a reboot restores normal operation. */
void display_diag_test_pattern(void)
{
    const esp_lcd_panel_lt8912b_video_timing_t *v = &s_vt;
    lt_write(s_io_cec, 0x72, 0x12);
    lt_write(s_io_cec, 0x73, (uint8_t)((v->hs + v->hbp) % 256));
    lt_write(s_io_cec, 0x74, (uint8_t)((v->hs + v->hbp) / 256));
    lt_write(s_io_cec, 0x75, (uint8_t)((v->vs + v->vbp) % 256));
    lt_write(s_io_cec, 0x76, (uint8_t)(v->hact % 256));
    lt_write(s_io_cec, 0x77, (uint8_t)(v->vact % 256));
    lt_write(s_io_cec, 0x78, (uint8_t)(((v->vact / 256) << 4) + (v->hact / 256)));
    lt_write(s_io_cec, 0x79, (uint8_t)(v->htotal % 256));
    lt_write(s_io_cec, 0x7a, (uint8_t)(v->vtotal % 256));
    lt_write(s_io_cec, 0x7b, (uint8_t)(((v->vtotal / 256) << 4) + (v->htotal / 256)));
    lt_write(s_io_cec, 0x7c, (uint8_t)(v->hs % 256));
    lt_write(s_io_cec, 0x7d, (uint8_t)(((v->hs / 256) << 6) + (v->vs % 256)));
    lt_write(s_io_cec, 0x70, 0x80);
    lt_write(s_io_cec, 0x71, 0x51);
    lt_write(s_io_cec, 0x42, 0x12);
    lt_write(s_io_cec, 0x1e, 0x67);
    uint32_t dds = (uint32_t)(v->pclk_mhz * 0x16C16);
    lt_write(s_io_cec, 0x4e, dds & 0xff);
    lt_write(s_io_cec, 0x4f, (dds >> 8) & 0xff);
    lt_write(s_io_cec, 0x50, (dds >> 16) & 0xff);
    lt_write(s_io_cec, 0x51, 0x80);
    ESP_LOGW(TAG, "bridge internal test pattern enabled (%dx%d, pclk %lu MHz)", v->hact, v->vact, (unsigned long)v->pclk_mhz);
}

static bool IRAM_ATTR on_draw_done(esp_lcd_panel_handle_t panel, esp_lcd_dpi_panel_event_data_t *edata, void *ctx)
{
    /* DMA2D copies complete in an ISR; the no-copy (frame buffer flip) path calls this from the drawing task */
    if (!xPortInIsrContext()) {
        xSemaphoreGive(s_draw_done);
        return false;
    }
    BaseType_t hp = pdFALSE;
    xSemaphoreGiveFromISR(s_draw_done, &hp);
    return hp == pdTRUE;
}

static bool IRAM_ATTR on_fb_complete(esp_lcd_panel_handle_t panel, esp_lcd_dpi_panel_event_data_t *edata, void *ctx)
{
    BaseType_t hp = pdFALSE;
    if (s_fb_free) xSemaphoreGiveFromISR(s_fb_free, &hp);
    return hp == pdTRUE;
}

/* draw_bitmap is asynchronous with DMA2D: wait until the copy is done so callers may reuse/free their buffer.
 * Every draw goes through here, under one lock: two tasks drawing at once made the second draw fail (#18). */
static esp_err_t draw_sync(int x, int y, int w, int h, const uint8_t *rgb)
{
    if (!s_panel) return ESP_ERR_INVALID_STATE;      /* no display (headless after an init failure) */
    xSemaphoreTake(s_draw_lock, portMAX_DELAY);
    xSemaphoreTake(s_draw_done, 0);                  /* clear a stale token */
    esp_err_t err = esp_lcd_panel_draw_bitmap(s_panel, x, y, x + w, y + h, rgb);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "draw_bitmap %dx%d at %d,%d: %s", w, h, x, y, esp_err_to_name(err));
    } else if (xSemaphoreTake(s_draw_done, pdMS_TO_TICKS(200)) != pdTRUE) {
        ESP_LOGW(TAG, "draw_bitmap completion timeout");
        err = ESP_ERR_TIMEOUT;
    }
    xSemaphoreGive(s_draw_lock);
    return err;
}

/* The decoder writes whole MCUs (16 x 16 pixels for 4:2:0, 16 x 8 for 4:2:2, 8 x 8 for 4:4:4 and grey), so its
 * rows are the width rounded up to that block (#15). */
static uint32_t mcu_width(const jpeg_decode_picture_info_t *pic)
{
    return (pic->sample_method == JPEG_DOWN_SAMPLING_YUV420 || pic->sample_method == JPEG_DOWN_SAMPLING_YUV422) ? 16 : 8;
}

static size_t align16(size_t v) { return (v + 15) & ~(size_t)15; }

esp_err_t display_init(int mode, int dsivar)
{
    if (!s_draw_done) s_draw_done = xSemaphoreCreateBinary();
    if (!s_draw_lock) s_draw_lock = xSemaphoreCreateMutex();
    if (!s_fb_free) s_fb_free = xSemaphoreCreateBinary();
    ESP_RETURN_ON_ERROR(make_panel(mode, dsivar), TAG, "panel");
    esp_lcd_dpi_panel_event_callbacks_t cbs = {.on_color_trans_done = on_draw_done, .on_frame_buf_complete = on_fb_complete};
    ESP_RETURN_ON_ERROR(esp_lcd_dpi_panel_register_event_callbacks(s_panel, &cbs, NULL), TAG, "callbacks");

    jpeg_decode_engine_cfg_t eng = {.intr_priority = 0, .timeout_ms = 200};
    ESP_RETURN_ON_ERROR(jpeg_new_decoder_engine(&eng, &s_jpeg), TAG, "jpeg engine");
    /* Output buffer for the largest picture we accept: the panel size rounded up to whole 16 x 16 MCUs, because the
     * decoder refuses a buffer smaller than its padded output (1920 x 1080 decodes as 1920 x 1088) (#15). */
    size_t buf_bytes = align16(s_info.width) * align16(s_info.height) * 3;
    jpeg_decode_memory_alloc_cfg_t mem = {.buffer_direction = JPEG_DEC_ALLOC_OUTPUT_BUFFER};
    s_rgb = jpeg_alloc_decoder_mem(buf_bytes, &mem, &s_rgb_size);
    ESP_RETURN_ON_FALSE(s_rgb, ESP_ERR_NO_MEM, TAG, "rgb buffer");
    /* hardware dimming resources; if any of this fails the CPU lookup table is used instead */
    size_t black_size = 0;
    s_dim = jpeg_alloc_decoder_mem(buf_bytes, &mem, &s_dim_size);
    s_black = jpeg_alloc_decoder_mem(buf_bytes, &mem, &black_size);
    ppa_client_config_t ppa_cfg = {.oper_type = PPA_OPERATION_BLEND};
    if (s_dim && s_black && ppa_register_client(&ppa_cfg, &s_ppa) == ESP_OK) {
        memset(s_black, 0, black_size);
        /* two frame buffers except in 1080p, which has room for one only (asking for two logs an error) */
        if (s_info.mode == 4 || esp_lcd_dpi_panel_get_frame_buffer(s_panel, 2, &s_fb[0], &s_fb[1]) != ESP_OK) {
            s_fb[0] = s_fb[1] = NULL;
        }
        ESP_LOGI(TAG, "hardware dimming (PPA blend) ready%s", s_fb[1] ? ", rendering into the back buffer" : "");
    } else {
        s_ppa = NULL;
        ESP_LOGW(TAG, "PPA not available: dimming on the CPU");
    }
    display_fill(0x000000);
    return ESP_OK;
}

display_info_t display_get_info(void)
{
    return s_info;
}

void display_fill(uint32_t rgb)
{
    if (!s_rgb) return;
    uint8_t r = rgb >> 16, g = rgb >> 8, b = rgb;
    for (size_t i = 0; i + 2 < s_rgb_size; i += 3) {
        s_rgb[i] = b; s_rgb[i + 1] = g; s_rgb[i + 2] = r;   /* frame buffer byte order is B,G,R */
    }
    draw_sync(0, 0, s_info.width, s_info.height, s_rgb);
}

bool display_ready(void)
{
    return s_panel != NULL;
}

void display_stamp_overlay(uint8_t *bgr, int w, int h)
{
    stamp_overlay(bgr, w, h);
}

esp_err_t display_show_rgb(const uint8_t *rgb, int w, int h)
{
    if (w > s_info.width || h > s_info.height) {
        return ESP_ERR_INVALID_SIZE;
    }
    int x = (s_info.width - w) / 2, y = (s_info.height - h) / 2;
    return draw_sync(x, y, w, h, rgb);
}

void display_set_brightness(int percent)
{
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;
    s_brightness = percent;
    for (int i = 0; i < 256; i++) {
        s_lut[i] = (uint8_t)((i * percent + 50) / 100);
    }
}

void display_set_rotation(int degrees)
{
    /* The DPI/LT8912B path has no hardware rotation; rotation would have to be done in software on the
       decoded picture. Remembered for INFO/STATS, not applied yet (the 1:1 panels are mounted upright). */
    s_rotation = degrees;
    if (degrees) {
        ESP_LOGW(TAG, "rotation %d requested: not implemented on this display path", degrees);
    }
}

static uint32_t s_last_draw_us;

uint32_t display_last_draw_us(void)
{
    return s_last_draw_us;
}

void display_set_overlay(const char *line1, const char *line2)
{
    snprintf(s_overlay1, sizeof(s_overlay1), "%s", line1 ? line1 : "");
    snprintf(s_overlay2, sizeof(s_overlay2), "%s", line2 ? line2 : "");
}

/* Dark band across the top of the picture with two lines of text (B,G,R buffer of w x h). */
static void stamp_overlay(uint8_t *bgr, int w, int h)
{
    if (!s_overlay1[0] && !s_overlay2[0]) return;
    int big = w / 100, small = big / 2 > 0 ? big / 2 : 1;      /* ~7 px and ~4 px glyph columns at 768 */
    int band = 7 * big + 7 * small + 4 * big;
    if (band > h) band = h;
    for (int y = 0; y < band; y++) {
        uint8_t *row = bgr + (size_t)y * w * 3;
        for (int x = 0; x < w; x++) {                            /* darken, so the picture stays visible */
            row[x * 3] >>= 2; row[x * 3 + 1] >>= 2; row[x * 3 + 2] >>= 2;
        }
    }
    ident_draw_text(bgr, w, h, big, big, big, 0xFF8000, s_overlay1);
    ident_draw_text(bgr, w, h, big, 2 * big + 7 * big, small, 0xFFFFFF, s_overlay2);
}

/* The picture goes centred into the box at_x, at_y, box_w, box_h: the whole panel for a FRAME, one tile for a TILE. */
static esp_err_t show_jpeg(const uint8_t *jpeg, size_t len, int at_x, int at_y, int box_w, int box_h, bool allow_flip, uint32_t *decode_ms)
{
    if (!s_jpeg || !s_rgb) return ESP_ERR_INVALID_STATE;
    jpeg_decode_picture_info_t pic;
    ESP_RETURN_ON_ERROR(jpeg_decoder_get_info(jpeg, len, &pic), TAG, "jpeg info");
    if (pic.width > (uint32_t)box_w || pic.height > (uint32_t)box_h) {
        ESP_LOGW(TAG, "frame %ux%u larger than its %dx%d box", (unsigned)pic.width, (unsigned)pic.height, box_w, box_h);
        return ESP_ERR_INVALID_SIZE;
    }
    jpeg_decode_cfg_t cfg = {
        .output_format = JPEG_DECODE_OUT_FORMAT_RGB888,
        .rgb_order = JPEG_DEC_RGB_ELEMENT_ORDER_BGR,   /* the DSI frame buffer takes bytes as B,G,R */
        .conv_std = JPEG_YUV_RGB_CONV_STD_BT601,
    };
    int64_t t0 = esp_timer_get_time();
    uint32_t out_len = 0;
    ESP_RETURN_ON_ERROR(jpeg_decoder_process(s_jpeg, &cfg, jpeg, len, s_rgb, s_rgb_size, &out_len), TAG, "jpeg decode");
    uint32_t stride = (pic.width + mcu_width(&pic) - 1) / mcu_width(&pic) * mcu_width(&pic);
    if (stride != pic.width) {
        /* rows come out padded to whole MCUs: pack them so every later step sees pic.width pixels per row (a width
         * that is a multiple of 8 but not 16 was drawn sheared) */
        for (uint32_t y = 1; y < pic.height; y++) {
            memmove(s_rgb + (size_t)y * pic.width * 3, s_rgb + (size_t)y * stride * 3, (size_t)pic.width * 3);
        }
    }
    uint8_t *shown = s_rgb;             /* the buffer that goes to the panel */
    if (s_brightness < 100) {
        bool done = false;
        if (s_ppa) {
            ppa_blend_oper_config_t b = {
                .in_bg = {.buffer = s_black, .pic_w = pic.width, .pic_h = pic.height, .block_w = pic.width,
                          .block_h = pic.height, .blend_cm = PPA_BLEND_COLOR_MODE_RGB888},
                .in_fg = {.buffer = s_rgb, .pic_w = pic.width, .pic_h = pic.height, .block_w = pic.width,
                          .block_h = pic.height, .blend_cm = PPA_BLEND_COLOR_MODE_RGB888},
                .out = {.buffer = s_dim, .buffer_size = s_dim_size, .pic_w = pic.width, .pic_h = pic.height,
                        .blend_cm = PPA_BLEND_COLOR_MODE_RGB888},
                .bg_alpha_update_mode = PPA_ALPHA_FIX_VALUE, .bg_alpha_fix_val = 255,
                .fg_alpha_update_mode = PPA_ALPHA_FIX_VALUE, .fg_alpha_fix_val = (uint32_t)(s_brightness * 255 / 100),
                .mode = PPA_TRANS_MODE_BLOCKING,
            };
            /* A full-size frame goes straight into the frame buffer that is not on screen; drawing a buffer
             * that lies inside a frame buffer makes the panel driver flip to it instead of copying: no 12 ms
             * copy and no tearing. Smaller pictures keep the copy path so the surround stays intact. */
            size_t fb_bytes = (size_t)s_info.width * s_info.height * 3;
            bool flip = allow_flip && s_fb[1] && pic.width == (uint32_t)s_info.width && pic.height == (uint32_t)s_info.height
                        && fb_bytes % 64 == 0;
            if (flip) {
                if (s_flip_wait) {
                    /* the buffer we are about to render into was on screen until the last flip: the DMA may still be
                     * scanning it out, so wait until the driver says it is free (tearing, #23) */
                    xSemaphoreTake(s_fb_free, pdMS_TO_TICKS(40));
                    s_flip_wait = false;
                }
                b.out.buffer = s_fb[1 - s_front];
                b.out.buffer_size = fb_bytes;
            }
            esp_err_t perr = ppa_do_blend(s_ppa, &b);
            if (perr == ESP_OK) {
                shown = flip ? (uint8_t *)s_fb[1 - s_front] : s_dim;
                done = true;
            } else {
                ESP_LOGW(TAG, "PPA blend failed (%s): CPU dimming from now on", esp_err_to_name(perr));
                s_ppa = NULL;
            }
        }
        if (!done) {
            size_t n = (size_t)pic.width * pic.height * 3;
            for (size_t i = 0; i < n; i++) {
                s_rgb[i] = s_lut[s_rgb[i]];
            }
        }
    }
    stamp_overlay(shown, pic.width, pic.height);
    if (decode_ms) {
        *decode_ms = (uint32_t)((esp_timer_get_time() - t0) / 1000);
    }
    int x = at_x + (box_w - (int)pic.width) / 2, y = at_y + (box_h - (int)pic.height) / 2;
    int64_t t1 = esp_timer_get_time();
    esp_err_t err = draw_sync(x, y, pic.width, pic.height, shown);
    if (err == ESP_OK && s_fb[1] && shown == (uint8_t *)s_fb[1 - s_front]) {
        s_front = 1 - s_front;          /* the driver flipped to the buffer we rendered into */
        xSemaphoreTake(s_fb_free, 0);   /* forget an old token: only the completion of this flip counts */
        s_flip_wait = true;
    }
    s_last_draw_us = (uint32_t)(esp_timer_get_time() - t1);
    return err;
}

esp_err_t display_show_jpeg(const uint8_t *jpeg, size_t len, uint32_t *decode_ms)
{
    return show_jpeg(jpeg, len, 0, 0, s_info.width, s_info.height, true, decode_ms);
}

esp_err_t display_show_jpeg_at(const uint8_t *jpeg, size_t len, int x, int y, int w, int h, uint32_t *decode_ms)
{
    if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > s_info.width || y + h > s_info.height) {
        return ESP_ERR_INVALID_ARG;
    }
    return show_jpeg(jpeg, len, x, y, w, h, false, decode_ms);
}

esp_err_t display_show_rgb_at(const uint8_t *rgb, int w, int h, int x, int y)
{
    if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > s_info.width || y + h > s_info.height) {
        return ESP_ERR_INVALID_ARG;
    }
    return draw_sync(x, y, w, h, rgb);
}
