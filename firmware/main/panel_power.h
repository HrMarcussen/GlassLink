/* Panel power on an integrated DU (#67, docs/usb-protocol.md 6a): the carrier board switches the scaler's 12 V from
 * GPIO21 (pulled down on the carrier, so the panel is dark while no firmware drives it). A plain NANO has no carrier:
 * then nothing here touches a pin and the DU reports nothing about panel power. */
#pragma once
#include <stdbool.h>
#include <stdint.h>

/* Looks for the carrier (GPIO23 strap low, or NVS "carrier": 1 = there, 0 = not, absent = the strap decides) and
 * leaves the panel off. Call after the NVS and the display are up. */
void panel_power_init(void);

bool panel_power_present(void);

/* Draws black before the panel loses its power (under the caller's frame lock). */
void panel_power_set_blackout(void (*fn)(void));

/* What the DU knows, every pass of the protocol loop: a USB host (configured, not suspended), a DMC talking (a message
 * within 6 s), whether the last DMC said BYE, and what the DMC asked for (-1 not said, 0 off, 1 on). Switches the panel
 * when the answer changes. */
void panel_power_update(bool usb, bool dmc, bool bye, int dmc_wants);

bool panel_power_is_on(void);

/* Why the panel is as it is: "no_usb", "no_usb_timeout", "bye", "host_no_dmc", "dmc_on", "dmc_off". */
const char *panel_power_why(void);

/* The scaler answers on I2C (its EDID at 0x50): it has power. Checked 5 s after switching on, then every 2 s. */
bool panel_power_seen(void);
