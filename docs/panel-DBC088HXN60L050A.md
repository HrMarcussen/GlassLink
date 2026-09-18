# Panel: Aptus DBC088HXN60L050A (8.82" square, 768 x 768)

Facts extracted from the Aptus product specification v1.1 (2024-07-11, 23 pages). The PDF itself is kept locally
in `docs/datasheets/` and is not in git (vendor copyright). Everything a GlassLink DU board needs is summarised here.

## What the panel really is
- A **HannStar 1024 x 768 (XGA) a-Si TFT cut down to a square**: the spec says "cutted from resolution 1024x768",
  the pixel-format page shows 1024 pixels by 768 lines, and the pixel pitch of 0.20625 mm times 1024 is 211.2 mm,
  i.e. a 10.4" XGA glass of which 158.4 mm (768 columns) remains. The flex carries HannStar markings
  (6M1H400049-A2, [721H460408-B2]); the model name contains "HXN60".
- So electrically it is a **standard XGA LVDS panel**. Only 768 of the 1024 columns physically exist. Which 768
  (left, centre or right part of the line) is not stated and has to be found by experiment.
- Normally black, 16.2 M colours (6 bit + dithering), 500 cd/m2, contrast 600-900:1, viewing angle 80-85 degrees all
  round, response 30-40 ms, anti-glare 3H. Operating -20..70 C. Outline 184.8 x 179.75 x 7.7 mm.

## Interface (connector CN1 on the panel's flex)
- 60-pin FPC; recommended mating connector **STARCONN 089K60-000100-G2-R**.
- **Single-channel LVDS, 4 data pairs + clock, 8 bit, VESA mapping** (IND3 carries R6 R7 G6 G7 B6 B7):
  IND0 = R0-R5 G0, IND1 = G1-G5 B0 B1, IND2 = B2-B5 HS VS DE, IND3 = R6 R7 G6 G7 B6 B7.
- Receiver: |VID| 0.1-0.6 V, VCM 0.7 / 1.2 / 1.6 V.

| Pin | Symbol | Pin | Symbol | Pin | Symbol |
|---|---|---|---|---|---|
| 1 | AGND | 21 | PINC (clock +) | 41 | VCOM (not connected) |
| 2 | AVDD | 22 | NINC (clock -) | 42 | DITH (1 = dithering on) |
| 3 | DVDD | 23 | GND | 43 | GND |
| 4 | GND | 24 | PIND2 | 44 | DVDD |
| 5 | VCOM (not connected) | 25 | NIND2 | 45 | GND |
| 6 | DVDD | 26 | GND | 46-52 | V7..V1 gamma (not connected) |
| 7 | GND | 27 | PIND1 | 53 | GND |
| 8-14 | V14..V8 gamma (not connected) | 28 | NIND1 | 54 | DVDD |
| 15 | GND | 29 | GND | 55 | SELB (0 = 8 bit, 1 = 6 bit) |
| 16 | DVDD_LVDS | 30 | PIND0 | 56 | VGH |
| 17 | GND | 31 | NIND0 | 57 | DVDD (gate IC) |
| 18 | PIND3 | 32 | GND | 58 | VGL |
| 19 | NIND3 | 33 | GND_LVDS | 59 | GND (gate IC) |
| 20 | GND | 34 | GRB (reset, active low, 10 k / 0.1 uF) | 60 | NC |
| | | 35 | STBYB (1 = run, 0 = standby) | | |
| | | 36 | SHLR (1 = normal) | | |
| | | 37 | DVDD | | |
| | | 38 | UPDN (0 = normal) | | |
| | | 39 | AGND | | |
| | | 40 | AVDD | | |

Straps for normal use: SHLR = 1, UPDN = 0 (both can flip the picture in hardware: rotation by 180 degrees for free),
DITH = 1 and SELB = 0 for 8-bit input with 16.2 M colours, STBYB = 1, GRB through an RC.
Gamma (V1..V14) and VCOM are generated on the flex itself (resistor arrays, the 8-pin IC, VCOM test pad).

## Supplies the host board must provide
| Rail | Typ. | Current (white, 60 Hz) |
|---|---|---|
| DVDD, DVDD_LVDS | 3.3 V (3.0-3.6) | 25 mA (20-35) |
| AVDD | 12.1 V | 66 mA (55-80) |
| VGH | 23 V | 0.71 mA |
| VGL | -10 V | 1.5 mA |

Absolute maxima: DVDD 5 V, AVDD 15 V, VGH 42 V, VGL -20 V, VGH-VGL 40 V.
Power sequence (section 6.5): VDD, then STBYB/reset, then AVDD and VGL, then VGH, then data, backlight last; reverse
order on the way down; every step 5-50 ms. A single TFT bias IC (TPS65150 class) does all of this.

## Timing (DE mode, 60 Hz)
| | Min | Typ | Max |
|---|---|---|---|
| Pixel clock | 52 MHz | 65 MHz | 71 MHz |
| Horizontal active | | 1024 | |
| Horizontal total | 1114 | 1344 | 1400 |
| Horizontal blanking | 90 | 320 | 376 |
| Vertical active | | 768 | |
| Vertical total | 778 | 806 | 845 |
| Vertical blanking | 10 | 38 | 77 |

Plain VESA XGA (65 MHz, 1344 x 806) is the typical case. A DU only has 768 visible columns: either send 1024-wide
lines with the picture in the visible part, or (if the visible columns start at the first pixel) send a 768-wide
DE and treat the rest of the line as blanking.

## Backlight
- Separate 2-wire lead, JST **BHSR-02VS-01** plug, black and white wire.
- **7.5 W, LED current 240 mA**, which puts the string voltage near 31 V (to be confirmed with a meter). Needs a
  boost LED driver from 12 V with PWM dimming and enable.

## Consequences for GlassLink
- Today's blue scaler board (HDP802, "LVDS 8BIT" socket) is what generates AVDD / VGH / VGL, runs the XGA LVDS timing
  and drives the backlight. It converts our 768 x 768 HDMI picture into the visible part of the XGA line.
- An integrated DU board therefore needs five blocks: P4 core module, DSI-to-LVDS bridge (single channel, VESA 8 bit,
  52-71 MHz), TFT bias supply with sequencing, boost LED driver (~31 V / 240 mA, PWM + enable on P4 GPIOs), USB-C
  device port; powered from 12 V (about 11 W per DU: 7.5 W backlight, 2 W panel, 1.5 W P4).
- DSI bandwidth: 65 MHz x 24 bit = 1.56 Gbit/s, fine on the P4's two lanes.
