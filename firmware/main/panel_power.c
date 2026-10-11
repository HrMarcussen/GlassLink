#include "panel_power.h"

#include "display.h"
#include "driver/gpio.h"
#include "esp_log.h"
#include "esp_rom_sys.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs.h"

static const char *TAG = "panel";

#define PANEL_EN_GPIO 21        /* the carrier's 12 V switch for the scaler, pulled down (fail dark) */
#define CARRIER_ID_GPIO 23      /* tied low on the carrier through a resistor; floats (pull-up: high) on a plain NANO */
#define NO_USB_SCREEN_US (120LL * 1000000)   /* "No USB" for 2 minutes, then dark: the DU cannot tell a PC that is off
                                                from a pulled cable, and the time also covers a PC booting (Thomas) */
#define SEEN_GRACE_US (5LL * 1000000)        /* the scaler answers about 2 s after its 12 V comes back */
#define EDID_ADDR 0x50

typedef enum { WHY_NO_USB, WHY_NO_USB_TIMEOUT, WHY_BYE, WHY_HOST_NO_DMC, WHY_DMC_ON, WHY_DMC_OFF } why_t;
static const char *const WHY_TEXT[] = {"no_usb", "no_usb_timeout", "bye", "host_no_dmc", "dmc_on", "dmc_off"};

static bool s_present, s_on, s_seen;
static int s_why = -1;
static int64_t s_no_usb_since, s_on_since, s_seen_checked;
static bool s_had_usb;
static void (*s_blackout)(void);

void panel_power_set_blackout(void (*fn)(void)) { s_blackout = fn; }

static int nvs_carrier(void)
{
    nvs_handle_t nvs;
    int32_t v = -1;
    if (nvs_open("module", NVS_READONLY, &nvs) == ESP_OK) {
        nvs_get_i32(nvs, "carrier", &v);
        nvs_close(nvs);
    }
    return v;
}

void panel_power_init(void)
{
    int forced = nvs_carrier();
    if (forced < 0) {
        gpio_config_t id = {.pin_bit_mask = 1ULL << CARRIER_ID_GPIO, .mode = GPIO_MODE_INPUT, .pull_up_en = GPIO_PULLUP_ENABLE};
        gpio_config(&id);                      /* stays an input with the pull-up: a plain NANO's pin does not float */
        esp_rom_delay_us(50);
        s_present = gpio_get_level(CARRIER_ID_GPIO) == 0;
    } else {
        s_present = forced == 1;               /* the bench: NVS says */
    }
    if (!s_present) {
        return;                                /* a plain NANO: GPIO21 is never touched */
    }
    gpio_config_t en = {.pin_bit_mask = 1ULL << PANEL_EN_GPIO, .mode = GPIO_MODE_OUTPUT};
    gpio_set_level(PANEL_EN_GPIO, 0);
    gpio_config(&en);
    gpio_set_level(PANEL_EN_GPIO, 0);
    s_no_usb_since = esp_timer_get_time();     /* "No USB" counts from power-up */
    ESP_LOGI(TAG, "carrier board %s: panel power switch on GPIO%d", forced == 1 ? "set in NVS" : "found", PANEL_EN_GPIO);
}

bool panel_power_present(void) { return s_present; }

bool panel_power_is_on(void) { return s_on; }

const char *panel_power_why(void) { return s_why >= 0 ? WHY_TEXT[s_why] : WHY_TEXT[WHY_NO_USB]; }

bool panel_power_seen(void) { return s_seen; }

static void switch_panel(bool on)
{
    if (on == s_on) return;
    if (on) {
        gpio_set_level(PANEL_EN_GPIO, 1);
        s_on_since = esp_timer_get_time();
    } else {
        /* black first, then the power: the nearest to the panel's own power-down order (the next picture is drawn
         * into the dark panel as usual) */
        if (s_blackout) s_blackout();
        vTaskDelay(pdMS_TO_TICKS(100));
        gpio_set_level(PANEL_EN_GPIO, 0);
    }
    s_on = on;
    s_seen = false;
    s_seen_checked = 0;
}

void panel_power_update(bool usb, bool dmc, bool bye, int dmc_wants)
{
    if (!s_present) return;
    int64_t now = esp_timer_get_time();
    if (usb) {
        s_had_usb = true;
    } else if (s_had_usb) {
        s_had_usb = false;
        s_no_usb_since = now;                  /* the host went (a pulled cable, a crashed PC): "No USB" again for a while */
    }

    why_t why = dmc ? (dmc_wants == 0 ? WHY_DMC_OFF : WHY_DMC_ON)
              : bye ? WHY_BYE
              : usb ? WHY_HOST_NO_DMC
              : now - s_no_usb_since < NO_USB_SCREEN_US ? WHY_NO_USB : WHY_NO_USB_TIMEOUT;
    bool on = why == WHY_NO_USB || why == WHY_HOST_NO_DMC || why == WHY_DMC_ON;
    if ((int)why != s_why) {
        ESP_LOGI(TAG, "panel %s (%s)", on ? "on" : "off", WHY_TEXT[why]);
        s_why = (int)why;
    }
    switch_panel(on);

    if (s_on && now - s_on_since > SEEN_GRACE_US && now - s_seen_checked > 2000000) {
        s_seen_checked = now;
        bool seen = display_i2c_probe(EDID_ADDR);
        if (seen != s_seen) ESP_LOGI(TAG, "the scaler %s on I2C", seen ? "answers" : "does not answer");
        s_seen = seen;
    }
}
