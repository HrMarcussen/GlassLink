#!/usr/bin/env python3
"""Probe a few Fenix L:vars through the FSUIPC WebSocket Server (optional, documents the data path
for a future text-display renderer such as a DCDU/MCDU).

Requires FSUIPC7 running with the WebSocket Server started (C:\\FSUIPC7\\Utils\\FSUIPCWebSocketServer.exe,
listens on localhost:2048 by default) and MSFS with the Fenix loaded.

    python tools/probe_lvars.py [--url ws://localhost:2048/fsuipc/] [--vars L:S_OH_...,L:...]
"""

from __future__ import annotations

import argparse
import asyncio
import json

import websockets

DEFAULT_VARS = [
    "L:A_DISPLAY_BRIGHTNESS_CO",     # captain DU brightness knob (Fenix)
    "L:A_DISPLAY_BRIGHTNESS_FO",
    "L:S_DISPLAY_ATT_HDG",
    "L:S_DISPLAY_EIS_DMC",
    "L:S_FCU_ALTITUDE",
    "L:I_FCU_TRACK_FPA_MODE",
]


async def probe(url: str, names: list[str], seconds: float) -> None:
    async with websockets.connect(url, max_size=None) as ws:
        # 1) declare a variable group
        await ws.send(json.dumps({"command": "vars.declare", "name": "probe", "vars": [{"name": n} for n in names]}))
        print("<-", await ws.recv())
        # 2) one-shot read
        await ws.send(json.dumps({"command": "vars.read", "name": "probe"}))
        print("<-", await ws.recv())
        # 3) stream changes
        await ws.send(json.dumps({"command": "vars.start", "name": "probe", "interval": 250, "changesOnly": True}))
        print("<-", await ws.recv())
        try:
            async with asyncio.timeout(seconds):
                async for msg in ws:
                    print("<-", msg)
        except TimeoutError:
            pass
        await ws.send(json.dumps({"command": "vars.stop", "name": "probe"}))


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://localhost:2048/fsuipc/")
    ap.add_argument("--vars", default=",".join(DEFAULT_VARS), help="comma separated L:var names")
    ap.add_argument("--seconds", type=float, default=10.0)
    args = ap.parse_args()
    asyncio.run(probe(args.url, [v.strip() for v in args.vars.split(",") if v.strip()], args.seconds))


if __name__ == "__main__":
    main()
