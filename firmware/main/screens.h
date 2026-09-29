/* The DU's own screens (#81): what it shows when it has no picture to show, in GlassLink's look (the status page's dark
 * colours and its font, Inter), drawn with a real font renderer so any text fits at any size. */
#pragma once
#include <stdint.h>
#include "esp_err.h"

typedef enum {
    SCREEN_NO_USB,          /* no USB host: waiting for the PC */
    SCREEN_NO_HOST,         /* USB is there, but no GlassLink DMC talks to the DU */
    SCREEN_UNASSIGNED,      /* a DMC is there, no display is assigned to this DU */
    SCREEN_WAITING,         /* a display is assigned, its picture has not come yet (the sim is not showing it) */
    SCREEN_IDENTIFY,        /* Identify from the status page, with no picture on the DU */
    SCREEN_UPDATING,        /* a firmware update is being received */
} screen_kind_t;

typedef struct {
    screen_kind_t kind;
    const char *label;      /* the DU's name on the status page ("DU1"); empty when not known yet */
    const char *display;    /* the assigned display ("Captain PFD"); empty when none */
    const char *serial;     /* 32 hex characters */
    const char *fw;         /* this firmware's version */
    int progress;           /* SCREEN_UPDATING: percent done */
    int brightness;         /* 0..100: the screen is dimmed like a picture would be */
} screen_t;

/* Loads the fonts; false if they cannot be read (the old 5x7 text is then used). */
esp_err_t screens_init(void);

/* Draws the whole screen and shows it. */
esp_err_t screens_show(const screen_t *s);

/* The Identify banner laid over live pictures: w x h B,G,R pixels ready to copy over the top rows of a picture w pixels
 * wide (kept until the next call with other text or width; NULL if it cannot be made). */
const uint8_t *screens_banner(int w, const char *label, const char *display, const char *serial, int brightness, int *h);
