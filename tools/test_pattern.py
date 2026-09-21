#!/usr/bin/env python3
"""Synthetic 'display' window for testing without the sim.

Draws a moving bar, a frame counter and a millisecond clock at a fixed rate, so you can
assign it (`python -m glasslink assign pattern --title "GlassLink pattern"`) and check
capture, change detection, fps and glass-to-glass latency (compare the clock on this
window with the clock in the viewer on a phone photo/video).

    python tools/test_pattern.py [--size 600x600] [--fps 30] [--title "GlassLink pattern A"]
"""

from __future__ import annotations

import argparse
import math
import time

import pygame


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", default="600x600")
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--title", default="GlassLink pattern", help="window title (run several with different titles)")
    args = ap.parse_args()
    w, h = (int(v) for v in args.size.lower().split("x"))

    try:                                   # physical pixels: a 768x768 window must be 768x768 for the capture
        import ctypes

        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:  # noqa: BLE001
        pass
    pygame.init()
    screen = pygame.display.set_mode((w, h))
    pygame.display.set_caption(args.title)
    big = pygame.font.SysFont("monospace", max(16, h // 8), bold=True)
    small = pygame.font.SysFont("monospace", max(12, h // 20))
    clock = pygame.time.Clock()
    t0 = time.monotonic()
    n = 0
    running = True
    while running:
        for ev in pygame.event.get():
            if ev.type == pygame.QUIT:
                running = False
        t = time.monotonic() - t0
        screen.fill((0, 0, 0))
        # moving bar
        x = int((0.5 + 0.45 * math.sin(t * 2.0)) * w)
        pygame.draw.rect(screen, (0, 200, 0), (x - 6, 0, 12, h))
        # attitude-like horizon line
        ang = 15 * math.sin(t)
        cx, cy = w // 2, h // 2
        dx, dy = math.cos(math.radians(ang)) * w, math.sin(math.radians(ang)) * w
        pygame.draw.line(screen, (255, 255, 255), (cx - dx, cy - dy), (cx + dx, cy + dy), 3)
        ms = int(t * 1000)
        screen.blit(big.render(f"{ms // 1000:4d}.{ms % 1000:03d}", True, (255, 255, 0)), (20, 20))
        # machine-readable wall clock: 20 cells across the bottom edge = milliseconds since epoch mod 2^20
        # (white = 1, black = 0, MSB first). tools/measure_latency.py decodes this from received frames.
        stamp = int(time.time() * 1000) & 0xFFFFF
        cell = w // 20
        for i in range(20):
            bit = (stamp >> (19 - i)) & 1
            pygame.draw.rect(screen, (255, 255, 255) if bit else (0, 0, 0), (i * cell, h - 16, cell, 16))
        screen.blit(small.render(f"frame {n}", True, (0, 255, 255)), (20, h - 40))
        # colour check: RED left, BLUE right. Green/white/cyan look the same with R and B swapped, these do not.
        pygame.draw.rect(screen, (255, 0, 0), (w - 140, 20, 50, 50))
        pygame.draw.rect(screen, (0, 0, 255), (w - 80, 20, 50, 50))
        screen.blit(small.render("R", True, (255, 255, 255)), (w - 130, 75))
        screen.blit(small.render("B", True, (255, 255, 255)), (w - 70, 75))
        pygame.display.flip()
        n += 1
        clock.tick(args.fps)
    pygame.quit()


if __name__ == "__main__":
    main()
