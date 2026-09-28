/* USB vendor-class link for the GlassLink module.
 *
 * One vendor-specific interface with a bulk IN and a bulk OUT endpoint (512-byte packets at high speed).
 * A BOS descriptor with the Microsoft OS 2.0 platform capability tells Windows 8.1+ to bind WinUSB
 * automatically (compatible id "WINUSB") and registers a DeviceInterfaceGUID, so no driver install is needed.
 */
#include <string.h>
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "freertos/stream_buffer.h"
#include "tinyusb.h"
#include "tinyusb_default_config.h"
#include "tusb.h"
#include "usb_link.h"

static const char *TAG = "usb";

/* Espressif VID with the development PID (see docs/usb-protocol.md). */
#define XD_USB_VID 0x303A
#define XD_USB_PID 0x4001
#define VENDOR_REQUEST_MICROSOFT 0x20

enum { ITF_NUM_VENDOR = 0, ITF_NUM_TOTAL };
#define EPNUM_VENDOR_OUT 0x01
#define EPNUM_VENDOR_IN  0x81

/* ---- descriptors --------------------------------------------------------------------------- */
static const tusb_desc_device_t s_device_desc = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0210,               /* 2.1: required for BOS */
    .bDeviceClass = 0x00,
    .bDeviceSubClass = 0x00,
    .bDeviceProtocol = 0x00,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = XD_USB_VID,
    .idProduct = XD_USB_PID,
    .bcdDevice = 0x0103,   /* bump on descriptor changes: Windows caches MS OS descriptor results per VID/PID/bcdDevice */
    .iManufacturer = 0x01,
    .iProduct = 0x02,
    .iSerialNumber = 0x03,
    .bNumConfigurations = 0x01,
};

#define CONFIG_TOTAL_LEN (TUD_CONFIG_DESC_LEN + TUD_VENDOR_DESC_LEN)

static const uint8_t s_fs_config_desc[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN, 0x00, 100),
    TUD_VENDOR_DESCRIPTOR(ITF_NUM_VENDOR, 4, EPNUM_VENDOR_OUT, EPNUM_VENDOR_IN, 64),
};

static const uint8_t s_hs_config_desc[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN, 0x00, 100),
    TUD_VENDOR_DESCRIPTOR(ITF_NUM_VENDOR, 4, EPNUM_VENDOR_OUT, EPNUM_VENDOR_IN, 512),
};

static const tusb_desc_device_qualifier_t s_qualifier = {
    .bLength = sizeof(tusb_desc_device_qualifier_t),
    .bDescriptorType = TUSB_DESC_DEVICE_QUALIFIER,
    .bcdUSB = 0x0210,
    .bDeviceClass = 0x00,
    .bDeviceSubClass = 0x00,
    .bDeviceProtocol = 0x00,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .bNumConfigurations = 0x01,
    .bReserved = 0x00,
};

static char s_serial[40] = "0000000000000000";
static const char *s_string_desc[] = {
    (const char[]){0x09, 0x04},     /* 0: language id (English) */
    "GlassLink",                   /* 1: manufacturer */
    "GlassLink DU",            /* 2: product */
    s_serial,                       /* 3: serial (GUID) */
    "GlassLink link",              /* 4: vendor interface */
};

/* BOS + MS OS 2.0 descriptor set.
 * This is a single-function (non-composite) device, so the feature descriptors sit directly under the set
 * header: no configuration/function subset headers (those are for composite devices behind usbccgp; Windows
 * ignores a function subset on a non-composite device and then finds no compatible ID -> code 28). */
#define MS_OS_20_DESC_LEN 0xA2   /* 10 (set header) + 20 (compatible id) + 132 (registry property) */
#define BOS_TOTAL_LEN (TUD_BOS_DESC_LEN + TUD_BOS_MICROSOFT_OS_DESC_LEN)

static const uint8_t s_bos_desc[] = {
    TUD_BOS_DESCRIPTOR(BOS_TOTAL_LEN, 1),
    TUD_BOS_MS_OS_20_DESCRIPTOR(MS_OS_20_DESC_LEN, VENDOR_REQUEST_MICROSOFT),
};

static const uint8_t s_ms_os_20_desc[] = {
    /* Set header: length, type, windows version (8.1), total length */
    U16_TO_U8S_LE(0x000A), U16_TO_U8S_LE(MS_OS_20_SET_HEADER_DESCRIPTOR), U32_TO_U8S_LE(0x06030000), U16_TO_U8S_LE(MS_OS_20_DESC_LEN),
    /* Compatible ID: WINUSB */
    U16_TO_U8S_LE(0x0014), U16_TO_U8S_LE(MS_OS_20_FEATURE_COMPATBLE_ID), 'W', 'I', 'N', 'U', 'S', 'B', 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    /* Registry property: DeviceInterfaceGUIDs (REG_MULTI_SZ), length 0x84 */
    U16_TO_U8S_LE(0x0084), U16_TO_U8S_LE(MS_OS_20_FEATURE_REG_PROPERTY),
    U16_TO_U8S_LE(0x0007), U16_TO_U8S_LE(0x002A),
    'D', 0, 'e', 0, 'v', 0, 'i', 0, 'c', 0, 'e', 0, 'I', 0, 'n', 0, 't', 0, 'e', 0,
    'r', 0, 'f', 0, 'a', 0, 'c', 0, 'e', 0, 'G', 0, 'U', 0, 'I', 0, 'D', 0, 's', 0, 0, 0,
    U16_TO_U8S_LE(0x0050),
    /* {B7E8A4C2-6F0D-4E21-9C3A-5D2F1E8B7A60} = GlassLink module interface GUID, double NUL terminated */
    '{', 0, 'B', 0, '7', 0, 'E', 0, '8', 0, 'A', 0, '4', 0, 'C', 0, '2', 0, '-', 0,
    '6', 0, 'F', 0, '0', 0, 'D', 0, '-', 0, '4', 0, 'E', 0, '2', 0, '1', 0, '-', 0,
    '9', 0, 'C', 0, '3', 0, 'A', 0, '-', 0, '5', 0, 'D', 0, '2', 0, 'F', 0, '1', 0,
    'E', 0, '8', 0, 'B', 0, '7', 0, 'A', 0, '6', 0, '0', 0, '}', 0, 0, 0, 0, 0,
};
TU_VERIFY_STATIC(sizeof(s_ms_os_20_desc) == MS_OS_20_DESC_LEN, "MS OS 2.0 descriptor size");

/* TinyUSB callbacks (weak in the stack, overridden here) */
uint8_t const *tud_descriptor_bos_cb(void)
{
    return s_bos_desc;
}

bool tud_vendor_control_xfer_cb(uint8_t rhport, uint8_t stage, tusb_control_request_t const *request)
{
    if (stage != CONTROL_STAGE_SETUP) {
        return true;
    }
    if (request->bmRequestType_bit.type == TUSB_REQ_TYPE_VENDOR && request->bRequest == VENDOR_REQUEST_MICROSOFT &&
        request->wIndex == 7) {
        uint16_t total_len;
        memcpy(&total_len, s_ms_os_20_desc + 8, 2);
        return tud_control_xfer(rhport, request, (void *)(uintptr_t)s_ms_os_20_desc, total_len);
    }
    return false; /* stall unknown requests */
}

/* ---- API ------------------------------------------------------------------------------------- */
/* Receiving (#61). The vendor class runs unbuffered (CONFIG_TINYUSB_VENDOR_RX/TX_BUFSIZE 0) with RX transfers
 * started only by us (CFG_TUD_VENDOR_RX_MANUAL_XFER, firmware/CMakeLists.txt), and we keep the data in a stream
 * buffer. TinyUSB's own FIFO mode loses data: it marks the endpoint free before it has copied a finished transfer
 * into its FIFO, and a read by our task in that moment starts the next transfer, sized to room the finished one is
 * about to take; the rest is dropped (exactly one 16 KB transfer's worth, seen as "short payload" once the decode
 * ran while the next frame came in). Here the next transfer is started only when the stream buffer has room for a
 * whole one, after the finished one is in it, under one lock for both cores. */
#define RX_XFER CFG_TUD_VENDOR_RX_EPSIZE            /* one transfer: up to 16 KB, ends on a short packet or ZLP */
#define RX_STREAM_SIZE (3 * RX_XFER)
static StreamBufferHandle_t s_rx_stream;
static portMUX_TYPE s_rx_mux = portMUX_INITIALIZER_UNLOCKED;
static volatile bool s_rx_armed;                   /* a transfer is started and not yet in the stream buffer */
static bool s_was_mounted;
static SemaphoreHandle_t s_tx_done;                /* given when an IN transfer has gone out */

/* Starts the next OUT transfer if none is running and a whole one fits; from any task. */
static void rx_arm_if_room(void)
{
    bool arm = false;
    taskENTER_CRITICAL(&s_rx_mux);
    if (!s_rx_armed && xStreamBufferSpacesAvailable(s_rx_stream) >= RX_XFER) {
        s_rx_armed = arm = true;
    }
    taskEXIT_CRITICAL(&s_rx_mux);
    if (arm && !tud_vendor_n_read_xfer(0)) {
        s_rx_armed = false;                        /* not configured (yet): the reader tries again */
    }
}

/* Runs in the TinyUSB task with a finished OUT transfer. */
void tud_vendor_rx_cb(uint8_t idx, const uint8_t *buffer, uint16_t bufsize)
{
    (void)idx;
    if (bufsize) {
        xStreamBufferSend(s_rx_stream, buffer, bufsize, 0);    /* fits: started only with room for a whole one */
    }
    s_rx_armed = false;                            /* only now: the data is where the reader looks */
    rx_arm_if_room();
}

void tud_vendor_tx_cb(uint8_t idx, uint32_t sent_bytes)
{
    (void)idx; (void)sent_bytes;
    xSemaphoreGive(s_tx_done);
}

esp_err_t usb_link_start(const char *serial)
{
    strlcpy(s_serial, serial, sizeof(s_serial));
    if (!s_rx_stream) s_rx_stream = xStreamBufferCreate(RX_STREAM_SIZE, 1);
    if (!s_tx_done) s_tx_done = xSemaphoreCreateBinary();
    if (!s_rx_stream || !s_tx_done) return ESP_ERR_NO_MEM;
    /* High-speed port 0 of the ESP32-P4 (the Type-A socket on the NANO), default PHY and task settings. */
    tinyusb_config_t cfg = TINYUSB_CONFIG_HIGH_SPEED(NULL, NULL);
    /* The USB task must outrank the protocol task and run on its own core, otherwise the two time-slice at the
       tick rate and every 512-byte packet costs ~1 ms. Protocol/screen tasks run on core 0 (see main.c). */
    cfg.task.priority = 8;
    cfg.task.xCoreID = 1;
    cfg.task.size = 6144;
    cfg.descriptor.device = &s_device_desc;
    cfg.descriptor.full_speed_config = s_fs_config_desc;
#if (TUD_OPT_HIGH_SPEED)
    cfg.descriptor.high_speed_config = s_hs_config_desc;
    cfg.descriptor.qualifier = &s_qualifier;
#endif
    cfg.descriptor.string = s_string_desc;
    cfg.descriptor.string_count = sizeof(s_string_desc) / sizeof(s_string_desc[0]);
    esp_err_t err = tinyusb_driver_install(&cfg);
    ESP_LOGI(TAG, "usb device started (serial %s): %s", s_serial, esp_err_to_name(err));
    return err;
}

bool usb_link_connected(void)
{
    return tud_mounted() && tud_vendor_mounted();
}

/* One reader (the protocol task). Returns what is there (at least one byte) or 0 after the timeout. */
size_t usb_link_read(uint8_t *buf, size_t len, uint32_t timeout_ms)
{
    TickType_t deadline = xTaskGetTickCount() + pdMS_TO_TICKS(timeout_ms);
    for (;;) {
        bool mounted = usb_link_connected();
        if (mounted != s_was_mounted) {
            /* a new session (or none): the old one's bytes and its transfer are gone with the bus reset */
            s_was_mounted = mounted;
            taskENTER_CRITICAL(&s_rx_mux);
            s_rx_armed = false;
            taskEXIT_CRITICAL(&s_rx_mux);
            xStreamBufferReset(s_rx_stream);
        }
        if (mounted) {
            rx_arm_if_room();                      /* the first transfer of a session, or one we could not start */
        }
        TickType_t now = xTaskGetTickCount();
        TickType_t slice = pdMS_TO_TICKS(5);       /* awake at least every 5 ms to see the above */
        TickType_t wait = now >= deadline ? 0 : (deadline - now < slice ? deadline - now : slice);
        size_t n = xStreamBufferReceive(s_rx_stream, buf, len, wait);
        if (n) {
            rx_arm_if_room();                      /* room again: take the next transfer */
            return n;
        }
        if (xTaskGetTickCount() >= deadline) {
            return 0;
        }
    }
}

/* Callers serialise (main.c send_msg). The data is copied into the endpoint buffer, so buf may be reused at once.
 * A write that ends on a whole 512-byte packet gets a zero-length packet, or the host's read would wait for more. */
bool usb_link_write(const uint8_t *buf, size_t len, uint32_t timeout_ms)
{
    if (!usb_link_connected()) {
        return false;
    }
    TickType_t deadline = xTaskGetTickCount() + pdMS_TO_TICKS(timeout_ms);
    size_t off = 0;
    bool zlp = len > 0 && len % 512 == 0;
    while (off < len || zlp) {
        if (tud_vendor_write_available() > 0) {    /* the endpoint is idle */
            xSemaphoreTake(s_tx_done, 0);          /* forget an old completion */
            if (off < len) {
                off += tud_vendor_write(buf + off, len - off);
            } else {
                tud_vendor_write(buf, 0);
                zlp = false;
            }
            continue;
        }
        if (xTaskGetTickCount() >= deadline || !usb_link_connected()) {
            return false;
        }
        xSemaphoreTake(s_tx_done, pdMS_TO_TICKS(2));
    }
    return true;
}
