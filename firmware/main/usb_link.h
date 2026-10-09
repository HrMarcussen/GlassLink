/* USB vendor-class link (bulk in/out, WinUSB via MS OS 2.0 descriptors) */
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

/* Start the USB device on the high-speed port with the given serial string (24 hex characters). */
esp_err_t usb_link_start(const char *serial);

/* True while the host has configured the device. */
bool usb_link_connected(void);

/* True while a host is configured AND the bus is awake. The DU is powered on its own and has no VBUS sensing, so pulling
 * the cable (or the PC going to sleep or off) only shows as a suspended bus: it stays "configured" (#81). */
bool usb_link_host_present(void);

/* Changes with every configuration and unmount by the host, also when a bus reset and a new configuration follow each
 * other too quickly for usb_link_connected() to be seen false in between: a new host session (#80). */
uint32_t usb_link_session(void);

/* Blocking read of up to `len` bytes from the bulk OUT pipe; returns bytes read (0 on timeout). */
size_t usb_link_read(uint8_t *buf, size_t len, uint32_t timeout_ms);

/* Write a whole buffer to the bulk IN pipe (blocks until queued). Returns false if not connected. */
bool usb_link_write(const uint8_t *buf, size_t len, uint32_t timeout_ms);
