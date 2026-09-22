/* GlassLink USB module protocol - mirror of docs/usb-protocol.md and glasslink/modules.py */
#pragma once
#include <stdint.h>

#define XD_MAGIC0 'X'
#define XD_MAGIC1 'D'
#define XD_PROTO_VERSION 1
#define XD_HEADER_SIZE 16
#define XD_MAX_PAYLOAD (4u * 1024u * 1024u)

/* host -> module */
#define XD_T_FRAME          0x01
#define XD_T_GET_INFO       0x02
#define XD_T_SET_BRIGHTNESS 0x03
#define XD_T_SET_ROTATION   0x04
#define XD_T_SHOW_IDENT     0x05
#define XD_T_PING           0x06
#define XD_T_SET_ASSIGNED   0x07   /* arg 1 = a display is assigned, 0 = show NOT ASSIGNED */
#define XD_T_SET_MODE       0x09   /* arg = HDMI mode (0 768x768, 1 1024x768, 2 800x600, 3 1280x720): stored, then reboot */
#define XD_T_OTA_BEGIN      0x10
#define XD_T_OTA_DATA       0x11
#define XD_T_OTA_END        0x12
#define XD_T_REBOOT         0x20
/* module -> host */
#define XD_T_READY          0x81
#define XD_T_INFO           0x82
#define XD_T_STATS          0x83
#define XD_T_PONG           0x84
#define XD_T_LOG            0x85
#define XD_T_OTA_RESULT     0x90
#define XD_T_OTA_PROGRESS   0x91   /* arg = image bytes written so far; the host sends the next chunk after it */

typedef struct __attribute__((packed)) {
    uint8_t magic[2];
    uint8_t version;
    uint8_t type;
    uint32_t length;
    uint32_t seq;
    uint32_t arg;
} xd_header_t;

_Static_assert(sizeof(xd_header_t) == XD_HEADER_SIZE, "header size");
