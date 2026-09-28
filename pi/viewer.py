#!/usr/bin/env python3
"""Native fullscreen viewer for one GlassLink stream (Raspberry Pi, Linux, Windows).

    python viewer.py --url ws://sim-pc:8765/ws/pfd [--rotate 90] [--windowed] [--fps]

Dependencies: websockets, simplejpeg, pygame  (pip install websockets simplejpeg pygame)
"""

from __future__ import annotations

import argparse
import asyncio
import os
import struct
import sys
import time

import pygame
import simplejpeg
import websockets

HEADER = struct.Struct("<II")


class Viewer:
    def __init__(self, url: str, rotate: int, windowed: bool, show_fps: bool, size: tuple[int, int] | None) -> None:
        self.url = url
        self.rotate = rotate % 360
        self.show_fps = show_fps
        pygame.init()
        pygame.mouse.set_visible(False)
        flags = 0 if windowed else pygame.FULLSCREEN
        self.screen = pygame.display.set_mode(size or (0, 0), flags)
        pygame.display.set_caption(f"GlassLink {url}")
        self.font = pygame.font.SysFont("monospace", 14) if show_fps else None
        self.frames = 0
        self.bytes = 0
        self.stat_t = time.monotonic()
        self.stat_text = ""
        self.running = True

    def pump(self) -> None:
        for ev in pygame.event.get():
            if ev.type == pygame.QUIT or (ev.type == pygame.KEYDOWN and ev.key in (pygame.K_ESCAPE, pygame.K_q)):
                self.running = False

    def draw_message(self, text: str) -> None:
        self.screen.fill((0, 0, 0))
        if self.font:
            surf = self.font.render(text, True, (120, 120, 120))
            self.screen.blit(surf, (10, 10))
        pygame.display.flip()

    def draw(self, jpeg: bytes) -> None:
        rgb = simplejpeg.decode_jpeg(jpeg, colorspace="RGB")
        h, w = rgb.shape[:2]
        surf = pygame.image.frombuffer(rgb.tobytes(), (w, h), "RGB")
        if self.rotate:
            surf = pygame.transform.rotate(surf, -self.rotate)
            w, h = surf.get_size()
        sw, sh = self.screen.get_size()
        scale = min(sw / w, sh / h)
        tw, th = max(1, int(w * scale)), max(1, int(h * scale))
        if (tw, th) != (w, h):
            surf = pygame.transform.smoothscale(surf, (tw, th))
        self.screen.fill((0, 0, 0))
        self.screen.blit(surf, ((sw - tw) // 2, (sh - th) // 2))
        self.frames += 1
        self.bytes += len(jpeg)
        now = time.monotonic()
        if self.show_fps and now - self.stat_t >= 1.0:
            dt = now - self.stat_t
            self.stat_text = f"{self.frames / dt:.1f} fps  {self.bytes * 8 / dt / 1000:.0f} kbit/s  {w}x{h}"
            self.frames, self.bytes, self.stat_t = 0, 0, now
        if self.font and self.stat_text:
            self.screen.blit(self.font.render(self.stat_text, True, (0, 255, 0)), (6, 6))
        pygame.display.flip()

    async def run(self) -> None:
        backoff = 0.5
        while self.running:
            self.pump()
            try:
                self.draw_message(f"connecting {self.url}")
                async with websockets.connect(self.url, max_size=None, ping_interval=10, ping_timeout=10) as ws:
                    backoff = 0.5
                    await ws.send("n")
                    while self.running:
                        try:
                            msg = await asyncio.wait_for(ws.recv(), timeout=0.25)
                        except asyncio.TimeoutError:
                            self.pump()
                            continue
                        if isinstance(msg, (bytes, bytearray)):
                            self.draw(bytes(msg[HEADER.size:]))
                            await ws.send("n")
                        self.pump()
            except (OSError, websockets.WebSocketException, asyncio.TimeoutError) as exc:
                self.draw_message(f"disconnected: {exc}")
            if self.running:
                await asyncio.sleep(backoff)
                backoff = min(backoff * 2, 5.0)
        pygame.quit()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", required=True, help="ws://host:8765/ws/<display>")
    ap.add_argument("--rotate", type=int, default=0, help="rotate the picture by 0/90/180/270 degrees")
    ap.add_argument("--windowed", action="store_true", help="window instead of fullscreen (testing)")
    ap.add_argument("--size", help="window size WxH for --windowed, e.g. 800x800")
    ap.add_argument("--fps", action="store_true", help="show fps/bandwidth overlay")
    args = ap.parse_args()
    size = tuple(int(v) for v in args.size.lower().split("x")) if args.size else None
    if sys.platform.startswith("linux") and not os.environ.get("DISPLAY") and not os.environ.get("SDL_VIDEODRIVER"):
        os.environ["SDL_VIDEODRIVER"] = "kmsdrm"  # run straight on the panel without X/Wayland
    viewer = Viewer(args.url, args.rotate, args.windowed, args.fps, size)
    asyncio.run(viewer.run())


if __name__ == "__main__":
    main()
