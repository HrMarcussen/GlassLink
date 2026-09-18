/* USB vendor-class link (bulk in/out, WinUSB via MS OS 2.0 descriptors) */
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

/* Start the USB device on the high-speed port with the given serial string (32 hex chars). */
esp_err_t usb_link_start(const char *serial);

/* True while the host has configured the device. */
bool usb_link_connected(void);

/* Blocking read of up to `len` bytes from the bulk OUT pipe; returns bytes read (0 on timeout). */
size_t usb_link_read(uint8_t *buf, size_t len, uint32_t timeout_ms);

/* Write a whole buffer to the bulk IN pipe (blocks until queued). Returns false if not connected. */
bool usb_link_write(const uint8_t *buf, size_t len, uint32_t timeout_ms);
