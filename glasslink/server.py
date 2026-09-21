"""aiohttp server: viewer page, WebSocket (pull/ack, latest-only), MJPEG, snapshot, status."""

from __future__ import annotations

import asyncio
import logging
import struct
import time
from pathlib import Path
from typing import Any

from aiohttp import WSMsgType, web

from . import __version__, build_id, firmware_version
from .display import DisplayWorker
from .hub import FrameHub

log = logging.getLogger(__name__)
STATIC_DIR = Path(__file__).parent / "static"

# Binary WebSocket frame layout: uint32 seq | uint32 server_ms | JPEG bytes
HEADER = struct.Struct("<II")


def _server_ms() -> int:
    return int(time.monotonic() * 1000) & 0xFFFFFFFF


async def index(request: web.Request) -> web.Response:
    return web.FileResponse(STATIC_DIR / "admin.html", headers={"Cache-Control": "no-cache"})


_fw_cache: dict[str, Any] = {"t": 0.0, "v": {}}


def _firmware_summary(cfg: dict[str, Any]) -> dict[str, Any]:
    """What an update would install; re-read at most every 5 s (the status page polls every 2 s)."""
    from .firmware import summary

    if time.time() - _fw_cache["t"] > 5:
        _fw_cache["v"] = summary(cfg)
        _fw_cache["t"] = time.time()
    return _fw_cache["v"]


async def status(request: web.Request) -> web.Response:
    hub: FrameHub = request.app["hub"]
    mm = request.app.get("modules")
    body = _status_body(request, hub, mm)
    try:
        body["advice"] = request.app["advisor"].advice(body)
        body["source_fps"] = {k: round(v, 1) for k, v in request.app["advisor"].rates.items()}
    except Exception:  # noqa: BLE001 - advice must never break the status page
        log.exception("advice failed")
        body["advice"] = []
    return web.json_response(body)


def _status_body(request: web.Request, hub: "FrameHub", mm: Any) -> dict[str, Any]:
    return ({
        "version": __version__,
        "build": build_id(),
        "firmware_version": firmware_version,
        "firmware_image": _firmware_summary(request.app["cfg"]),
        "process": dict(PROCESS_STATE),
        "displays": hub.status(),
        "modules": mm.status() if mm else {},
        "usb": {"enabled": mm is not None, "scan_error": mm.last_scan_error if mm else ""},
        "brightness": mm.brightness_status() if mm else {},
        "learn": dict(request.app["learner"].state) if request.app.get("learner") else {"status": "idle"},
        "strays": _strays(request.app),
        "popout": (request.app.get("auto_popout").state if request.app.get("auto_popout") else {"status": "off", "detail": "auto pop-out disabled"}),
    })


async def modules_list(request: web.Request) -> web.Response:
    mm = request.app.get("modules")
    if mm is None:
        raise web.HTTPServiceUnavailable(text="usb modules disabled")
    return web.json_response({"modules": mm.status(), "displays": list(request.app["hub"].displays)})


async def modules_update(request: web.Request) -> web.Response:
    """POST /modules/<serial>  {"display": "pfd", "brightness": 80, "rotation": 0, "label": "...", "command": "ident"}"""
    mm = request.app.get("modules")
    if mm is None:
        raise web.HTTPServiceUnavailable(text="usb modules disabled")
    serial = request.match_info["serial"]
    body = await request.json()
    try:
        if any(k in body for k in ("display", "brightness", "rotation", "label")):
            mm.assign(serial, body.get("display"), brightness=body.get("brightness"),
                      rotation=body.get("rotation"), label=body.get("label"))
        if body.get("command"):
            if not mm.command(serial, body["command"], int(body.get("arg", 0))):
                raise web.HTTPNotFound(text="module not connected")
    except ValueError as exc:
        raise web.HTTPBadRequest(text=str(exc))
    return web.json_response(mm.status().get(serial, {}))


def _profile_points(cfg: dict[str, Any], request: web.Request) -> tuple[str | None, dict[str, Any]]:
    """Pop-out click points of the profile that matches the loaded aircraft (or the configured default)."""
    from .popout import profiles, select_profile

    mm = request.app.get("modules")
    title = mm.simvars.title() if (mm and mm.simvars) else None
    key, prof = select_profile(cfg, title) if title else (None, None)
    if prof is None:
        key = (cfg.get("popout") or {}).get("aircraft")
        prof = next((p for k, p in profiles(cfg).items() if key and key.lower() in k.lower()), None)
    return key, dict((prof or {}).get("points") or {})


async def displays_list(request: web.Request) -> web.Response:
    cfg = request.app["cfg"]
    key, points = _profile_points(cfg, request)
    out = request.app["registry"].describe(points)
    for name, d in out.items():
        st = request.app["hub"].get(name)
        d["has_window"] = bool(st is not None and st.window_hwnd)
    return web.json_response({"profile": key, "displays": out})


async def displays_edit(request: web.Request) -> web.Response:
    """POST /displays {"name": "fo_pfd", ...} adds; POST /displays/<name> {...} changes; DELETE removes."""
    from .registry import DisplayError

    reg = request.app["registry"]
    name = request.match_info.get("name")
    try:
        if request.method == "DELETE":
            reg.remove(name)
            return web.json_response({"removed": name})
        body = await request.json()
        if name is None:
            return web.json_response({"added": body.get("name"), "display": reg.add(body.get("name", ""), body)})
        return web.json_response({"display": reg.update(name, body)})
    except DisplayError as exc:
        raise web.HTTPBadRequest(text=str(exc))
    except (TypeError, ValueError) as exc:
        raise web.HTTPBadRequest(text=f"invalid value: {exc}")


_strays_cache: dict[str, Any] = {"t": 0.0, "v": []}


def _strays(app: web.Application) -> list[dict[str, Any]]:
    if app.get("learner") is None or not hasattr(app["learner"].io, "stray_popouts"):
        return []
    if time.monotonic() - _strays_cache["t"] > 4.0:
        try:
            _strays_cache.update(t=time.monotonic(), v=app["learner"].io.stray_popouts())
        except Exception:  # noqa: BLE001
            _strays_cache.update(t=time.monotonic(), v=[])
    return _strays_cache["v"]


async def strays_close(request: web.Request) -> web.Response:
    """POST /popouts/close-strays closes the sim's pop-out windows that GlassLink did not make."""
    learner = request.app["learner"]
    n = await asyncio.get_running_loop().run_in_executor(None, learner.io.close_strays)
    _strays_cache["t"] = 0.0
    return web.json_response({"closed": n})


async def popout_settings(request: web.Request) -> web.Response:
    """GET/POST /popout/settings: the key that loads the user's own view after a pop-out (camera_restore_key)."""
    from .config import save_config
    from .popout import parse_combo

    cfg = request.app["cfg"]
    pc = cfg.setdefault("popout", {})
    if request.method == "POST":
        body = await request.json()
        key = str(body.get("camera_restore_key") or "").strip().lower()
        if key:
            try:
                parse_combo(key)
            except ValueError as exc:
                raise web.HTTPBadRequest(text=str(exc))
        pc["camera_restore_key"] = key or None
        save_config(cfg, cfg.get("_path"))
    return web.json_response({"camera_restore_key": pc.get("camera_restore_key")})


async def displays_close(request: web.Request) -> web.Response:
    """POST /displays/<name>/close closes the display's pop-out window. The windows are parked off-screen, so the user
    cannot do it by hand. The automatic pop-out opens it again if the display has a click point."""
    name = request.match_info["name"]
    if name not in request.app["cfg"].get("displays", {}):
        raise web.HTTPNotFound(text=f"unknown display '{name}'")
    learner = request.app["learner"]
    if learner.busy:
        raise web.HTTPConflict(text="busy learning a display")
    closed = await asyncio.get_running_loop().run_in_executor(None, learner.io.close_existing, name)
    if closed and request.app.get("auto_popout"):
        request.app["auto_popout"].retry(name)
    return web.json_response({"closed": bool(closed)})


async def displays_learn(request: web.Request) -> web.Response:
    """POST /displays/<name>/learn starts learning that display's pop-out click point; POST /learn/cancel stops it."""
    from .learn import LearnError

    learner = request.app["learner"]
    name = request.match_info.get("name")
    if name is None:
        learner.cancel()
        return web.json_response(dict(learner.state))
    try:
        body = await request.json() if request.can_read_body else {}
        learner.start(name, str(body.get("view") or "standard"))
    except LearnError as exc:
        raise web.HTTPConflict(text=str(exc))
    return web.json_response(dict(learner.state))


async def shutdown(request: web.Request) -> web.Response:
    """POST /shutdown (localhost only): stop capture sessions and exit exactly like Ctrl+C in the console.
    Lets a launcher or tooling stop the server without killing a process that holds capture sessions."""
    if request.remote not in ("127.0.0.1", "::1"):
        raise web.HTTPForbidden(text="local only")
    import signal

    log.info("shutdown requested over http")
    loop = asyncio.get_running_loop()

    def _go() -> None:
        for hook in list(_shutdown_hooks):
            try:
                hook()
            except Exception:  # noqa: BLE001
                pass
        # Same as Ctrl+C: KeyboardInterrupt in the main thread, which aiohttp's run_app turns into a clean
        # cleanup (the console ctrl event route is not reliable when the process has no interactive console).
        signal.raise_signal(signal.SIGINT)

    loop.call_later(0.2, _go)
    return web.json_response({"stopping": True})


async def modules_forget(request: web.Request) -> web.Response:
    """DELETE /modules/<serial>: forget a disconnected module (its assignment and label)."""
    mm = request.app.get("modules")
    if mm is None:
        raise web.HTTPServiceUnavailable(text="usb modules disabled")
    try:
        removed = mm.forget(request.match_info["serial"])
    except ValueError as exc:
        raise web.HTTPConflict(text=str(exc))
    if not removed:
        raise web.HTTPNotFound(text="unknown module")
    return web.json_response({"removed": True})


async def view(request: web.Request) -> web.Response:
    name = request.match_info["name"]
    if name not in request.app["hub"].displays:
        raise web.HTTPNotFound(text=f"unknown display '{name}'")
    return web.FileResponse(STATIC_DIR / "viewer.html", headers={"Cache-Control": "no-cache"})


async def snapshot(request: web.Request) -> web.Response:
    hub: FrameHub = request.app["hub"]
    st = hub.get(request.match_info["name"])
    if st is None:
        raise web.HTTPNotFound()
    if st.jpeg is None:
        raise web.HTTPServiceUnavailable(text="no frame yet")
    return web.Response(body=st.jpeg, content_type="image/jpeg", headers={"Cache-Control": "no-cache"})


async def ws_handler(request: web.Request) -> web.WebSocketResponse:
    hub: FrameHub = request.app["hub"]
    st = hub.get(request.match_info["name"])
    if st is None:
        raise web.HTTPNotFound()
    ws = web.WebSocketResponse(heartbeat=15, max_msg_size=0)
    await ws.prepare(request)
    st.clients += 1
    peer = request.remote
    log.info("[%s] ws client connected: %s", st.name, peer)

    credit = asyncio.Event()   # set when the client asks for the next frame ("n")
    closed = asyncio.Event()

    async def reader() -> None:
        try:
            async for msg in ws:
                if msg.type == WSMsgType.TEXT and msg.data == "n":
                    credit.set()
                elif msg.type in (WSMsgType.CLOSE, WSMsgType.ERROR):
                    break
        finally:
            closed.set()
            credit.set()

    reader_task = asyncio.create_task(reader())
    last_seq = 0
    try:
        await ws.send_json({"type": "hello", "name": st.name, "size": [st.width, st.height], "version": __version__})
        while not closed.is_set():
            await credit.wait()
            if closed.is_set():
                break
            credit.clear()
            while not closed.is_set():
                res = await hub.wait_newer(st, last_seq, timeout=1.0)
                if res is not None:
                    break
            if closed.is_set():
                break
            seq, jpeg = res
            await ws.send_bytes(HEADER.pack(seq, _server_ms()) + jpeg)
            st.bytes_out += len(jpeg)
            last_seq = seq
    except (ConnectionResetError, asyncio.CancelledError):
        pass
    except Exception:  # noqa: BLE001
        log.exception("[%s] ws error", st.name)
    finally:
        reader_task.cancel()
        st.clients -= 1
        log.info("[%s] ws client disconnected: %s", st.name, peer)
        if not ws.closed:
            await ws.close()
    return ws


async def mjpeg(request: web.Request) -> web.StreamResponse:
    hub: FrameHub = request.app["hub"]
    st = hub.get(request.match_info["name"])
    if st is None:
        raise web.HTTPNotFound()
    boundary = "glasslinkframe"
    resp = web.StreamResponse(
        status=200,
        headers={
            "Content-Type": f"multipart/x-mixed-replace; boundary={boundary}",
            "Cache-Control": "no-cache, no-store",
            "Pragma": "no-cache",
        },
    )
    await resp.prepare(request)
    st.clients += 1
    last_seq = 0
    try:
        while True:
            res = await hub.wait_newer(st, last_seq, timeout=1.0)
            if res is None:
                continue
            seq, jpeg = res
            await resp.write(
                f"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {len(jpeg)}\r\n\r\n".encode() + jpeg + b"\r\n"
            )
            st.bytes_out += len(jpeg)
            last_seq = seq
    except (ConnectionResetError, asyncio.CancelledError):
        pass
    finally:
        st.clients -= 1
    return resp


def build_app(cfg: dict[str, Any]) -> web.Application:
    app = web.Application()
    app["cfg"] = cfg

    async def on_startup(app: web.Application) -> None:
        loop = asyncio.get_running_loop()
        hub = FrameHub(loop)
        app["hub"] = hub
        from .registry import DisplayRegistry

        registry = DisplayRegistry(cfg, hub, lambda name, dcfg: DisplayWorker(name, dcfg, cfg, hub), cfg.get("_path"))
        registry.start_all()
        app["registry"] = registry
        workers = registry.workers              # name -> worker, kept current by the registry

        def stop_all() -> None:
            registry.stop_all()
            if app.get("modules"):
                app["modules"].stop()

        _shutdown_hooks.append(stop_all)
        app["modules"] = None
        if cfg.get("usb", {}).get("enabled", True):
            from .modules import ModuleManager

            kwargs = {}
            n_fake = int(cfg["usb"].get("fake_modules", 0) or 0)
            if n_fake:
                from .modules import fake_module_finder

                kwargs = {"finder": fake_module_finder(n_fake), "transport_factory": lambda d: d}
                log.warning("using %d FAKE usb modules (usb.fake_modules) - no real hardware is served", n_fake)
            mm = ModuleManager(cfg, hub, cfg.get("_path"), scan_interval=float(cfg["usb"].get("scan_interval_s", 2)), **kwargs)
            mm.start()
            app["modules"] = mm

            def _unassign(display: str) -> None:        # a removed display must not stay assigned to a DU
                for serial, entry in list(cfg.get("modules", {}).items()):
                    if entry.get("display") == display:
                        mm.assign(serial, "")

            registry.on_removed = _unassign
            log.info("usb module manager started")
        from .learn import PopoutLearner

        def _pause_auto(paused: bool) -> None:
            if app.get("auto_popout"):
                app["auto_popout"].paused = paused

        from .advisor import Advisor

        def _sim_displays() -> set[str]:
            from .registry import SIM_PROCESS

            return {n for n, d in cfg.get("displays", {}).items() if (d.get("match") or {}).get("process") == SIM_PROCESS}

        app["advisor"] = Advisor(sim_displays=_sim_displays)
        app["learner"] = PopoutLearner(cfg, cfg.get("_path"), pause_auto=_pause_auto)
        app["auto_popout"] = None
        if cfg.get("popout", {}).get("auto"):
            from .popout import AutoPopout

            app["auto_popout"] = AutoPopout(cfg, hub, cfg.get("_path"))
            app["auto_popout"].start()
            log.info("auto pop-out enabled (aircraft '%s')", cfg["popout"].get("aircraft"))
        if not workers:
            log.warning("no displays configured - run `python -m glasslink assign <name>` first")

    async def on_cleanup(app: web.Application) -> None:
        if app.get("modules"):
            app["modules"].stop()
        if app.get("auto_popout"):
            app["auto_popout"].stop()
        if app.get("registry"):
            app["registry"].stop_all()
        await asyncio.sleep(0.5)  # let capture threads release their sessions before the process exits
        log.info("capture sessions stopped, exiting")

    app.on_startup.append(on_startup)
    app.on_cleanup.append(on_cleanup)
    app.add_routes(
        [
            web.get("/", index),
            web.get("/status", status),
            web.get("/view/{name}", view),
            web.get("/ws/{name}", ws_handler),
            web.get("/stream/{name}", mjpeg),
            web.get("/snapshot/{name}.jpg", snapshot),
            web.get("/modules", modules_list),
            web.post("/modules/{serial}", modules_update),
            web.delete("/modules/{serial}", modules_forget),
            web.post("/shutdown", shutdown),
            web.get("/displays", displays_list),
            web.post("/displays", displays_edit),
            web.post("/displays/{name}", displays_edit),
            web.delete("/displays/{name}", displays_edit),
            web.post("/displays/{name}/learn", displays_learn),
            web.post("/learn/cancel", displays_learn),
            web.post("/displays/{name}/close", displays_close),
            web.post("/popouts/close-strays", strays_close),
            web.get("/popout/settings", popout_settings),
            web.post("/popout/settings", popout_settings),
            web.static("/static", STATIC_DIR),
        ]
    )
    return app


_shutdown_hooks: list = []


def _install_console_handler() -> None:
    """Windows: when the console window is closed (or the user logs off / shuts down), stop all capture
    sessions before the process dies. Killing a process that holds Windows.Graphics.Capture sessions on the
    sim's DirectX 12 windows has been observed to trigger GPU driver timeouts on this machine."""
    import ctypes
    import ctypes.wintypes as wt

    HANDLER = ctypes.WINFUNCTYPE(wt.BOOL, wt.DWORD)

    def handler(ctrl_type: int) -> bool:
        # 0 = Ctrl+C, 1 = Ctrl+Break, 2 = close window, 5 = logoff, 6 = shutdown
        log.info("console event %s: stopping capture sessions", ctrl_type)
        for hook in list(_shutdown_hooks):
            try:
                hook()
            except Exception:  # noqa: BLE001
                pass
        if ctrl_type in (0, 1):
            return False  # let Python raise KeyboardInterrupt -> aiohttp cleanup runs too
        time.sleep(0.5)  # give the capture threads a moment to release their sessions
        return True

    _install_console_handler.ref = HANDLER(handler)  # keep alive
    ctypes.windll.kernel32.SetConsoleCtrlHandler(_install_console_handler.ref, True)


def pick_affinity(n_logical: int) -> list[int] | None:
    """CPUs for "auto": the last third of the logical processors on machines with 8 or more, else no pinning.
    The simulator's main thread lives on the first cores in practice; the last ones are the quiet end."""
    if n_logical < 8:
        return None
    k = max(2, n_logical // 3)
    return list(range(n_logical - k, n_logical))


PROCESS_STATE: dict[str, Any] = {}


def tune_process(cfg: dict[str, Any]) -> dict[str, Any]:
    """Below-normal priority and CPU pinning so the DMC never competes with the simulator for a core."""
    import psutil

    pcfg = cfg.get("process") or {}
    p = psutil.Process()
    out: dict[str, Any] = {}
    prio = str(pcfg.get("priority", "below_normal") or "normal").lower()
    classes = {"idle": psutil.IDLE_PRIORITY_CLASS, "below_normal": psutil.BELOW_NORMAL_PRIORITY_CLASS,
               "normal": psutil.NORMAL_PRIORITY_CLASS, "above_normal": psutil.ABOVE_NORMAL_PRIORITY_CLASS}
    try:
        p.nice(classes.get(prio, psutil.NORMAL_PRIORITY_CLASS))
        out["priority"] = prio if prio in classes else "normal"
    except Exception as exc:  # noqa: BLE001
        out["priority"] = f"unchanged ({exc})"
    aff = pcfg.get("affinity", "auto")
    cpus = pick_affinity(psutil.cpu_count(logical=True) or 1) if aff == "auto" else (list(aff) if aff else None)
    try:
        if cpus:
            p.cpu_affinity(cpus)
        out["affinity"] = p.cpu_affinity()
    except Exception as exc:  # noqa: BLE001
        out["affinity"] = f"unchanged ({exc})"
    PROCESS_STATE.clear()
    PROCESS_STATE.update(out)
    return out


def serve(cfg: dict[str, Any]) -> None:
    host = cfg["server"]["host"]
    port = int(cfg["server"]["port"])
    log.info("process: %s", tune_process(cfg))
    try:
        _install_console_handler()
    except Exception:  # noqa: BLE001
        log.debug("console control handler not installed", exc_info=True)
    log.info("GlassLink DMC %s (%s) listening on http://%s:%s/  (displays: %s)", __version__, build_id() or "no git", host, port,
             ", ".join(cfg["displays"]) or "none")
    try:
        web.run_app(build_app(cfg), host=host, port=port, print=None, access_log=None)
    except OSError as exc:
        if getattr(exc, "errno", None) in (10048, 98):
            log.error("Port %s is already in use: another GlassLink server is probably still running. "
                      "Close its window (or the tray icon) and start again, or use --port to pick another port.", port)
            raise SystemExit(2)
        raise
