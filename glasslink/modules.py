"""USB display modules: protocol framing, WinUSB transport, per-module workers and hot-plug manager.

Implements the host side of docs/usb-protocol.md. Modules are ESP32-P4 units that enumerate as a
vendor-specific USB device (bound to WinUSB by Windows automatically); each carries a serial GUID and
is assigned to a cockpit display in config.json under "modules": {"<serial>": {"display": "pfd"}}.

The transport is abstracted (Transport protocol) so the logic runs against a fake in tests.
"""

from __future__ import annotations

import json
import logging
import struct
import threading
from collections import deque
import time
from typing import Any, Protocol

from .config import save_config
from .hub import FrameHub

log = logging.getLogger(__name__)

# ---------------------------------------------------------------------------------------------
# Protocol constants (docs/usb-protocol.md)
# ---------------------------------------------------------------------------------------------
MAGIC = b"XD"
PROTO_VERSION = 1
HEADER = struct.Struct("<2sBBIII")  # magic, version, type, length, seq, arg = 16 bytes
HEADER_SIZE = HEADER.size
MAX_PAYLOAD = 4 * 1024 * 1024
USB_PACKET = 512

# host -> module
T_FRAME = 0x01
T_GET_INFO = 0x02
T_SET_BRIGHTNESS = 0x03
T_SET_ROTATION = 0x04
T_SHOW_IDENT = 0x05
T_PING = 0x06
T_SET_ASSIGNED = 0x07
T_OTA_BEGIN = 0x10
T_OTA_DATA = 0x11
T_OTA_END = 0x12
T_REBOOT = 0x20
# module -> host
T_READY = 0x81
T_INFO = 0x82
T_STATS = 0x83
T_PONG = 0x84
T_LOG = 0x85
T_OTA_RESULT = 0x90

TYPE_NAMES = {
    T_FRAME: "FRAME", T_GET_INFO: "GET_INFO", T_SET_BRIGHTNESS: "SET_BRIGHTNESS", T_SET_ROTATION: "SET_ROTATION",
    T_SHOW_IDENT: "SHOW_IDENT", T_PING: "PING", T_SET_ASSIGNED: "SET_ASSIGNED", T_OTA_BEGIN: "OTA_BEGIN", T_OTA_DATA: "OTA_DATA",
    T_OTA_END: "OTA_END", T_REBOOT: "REBOOT", T_READY: "READY", T_INFO: "INFO", T_STATS: "STATS",
    T_PONG: "PONG", T_LOG: "LOG", T_OTA_RESULT: "OTA_RESULT",
}

# Espressif VID with the development PID; the final PID is added here once granted.
USB_IDS: tuple[tuple[int, int], ...] = ((0x303A, 0x4001),)


def pack(msg_type: int, payload: bytes = b"", seq: int = 0, arg: int = 0) -> bytes:
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("payload too large")
    return HEADER.pack(MAGIC, PROTO_VERSION, msg_type, len(payload), seq & 0xFFFFFFFF, arg & 0xFFFFFFFF) + payload


class Message:
    __slots__ = ("type", "seq", "arg", "payload")

    def __init__(self, msg_type: int, seq: int, arg: int, payload: bytes) -> None:
        self.type, self.seq, self.arg, self.payload = msg_type, seq, arg, payload

    @property
    def name(self) -> str:
        return TYPE_NAMES.get(self.type, f"0x{self.type:02x}")

    def json(self) -> Any:
        try:
            return json.loads(self.payload.decode("utf-8"))
        except Exception:  # noqa: BLE001
            return None

    def __repr__(self) -> str:
        return f"<{self.name} seq={self.seq} arg={self.arg} len={len(self.payload)}>"


def parse_header(buf: bytes) -> tuple[int, int, int, int]:
    """Returns (type, length, seq, arg); raises ValueError on a bad header."""
    if len(buf) < HEADER_SIZE:
        raise ValueError("short header")
    magic, version, msg_type, length, seq, arg = HEADER.unpack_from(buf)
    if magic != MAGIC:
        raise ValueError(f"bad magic {magic!r}")
    if version != PROTO_VERSION:
        raise ValueError(f"unsupported protocol version {version}")
    if length > MAX_PAYLOAD:
        raise ValueError(f"payload length {length} too large")
    return msg_type, length, seq, arg


# ---------------------------------------------------------------------------------------------
# Transports
# ---------------------------------------------------------------------------------------------
class Transport(Protocol):
    serial: str
    description: str

    def read_chunk(self, timeout_ms: int) -> bytes | None:
        """Return the next bulk-IN transfer (any length, possibly a partial message), None on timeout."""

    def write(self, data: bytes, timeout_ms: int) -> None: ...

    def close(self) -> None: ...


class MessageReader:
    """Reassembles messages from bulk-IN chunks. If the stream is out of step (a previous host session ended in
    the middle of a message, so its tail is still in the DU's send buffer), the garbage is skipped up to the next
    valid header instead of failing: the same idea as the byte-wise resync in the DU firmware."""

    def __init__(self) -> None:
        self._buf = bytearray()
        self.resyncs = 0          # how many times garbage had to be skipped
        self.skipped = 0          # bytes dropped while doing so

    def _valid_at(self, i: int) -> bool:
        try:
            msg_type, _length, _seq, _arg = parse_header(bytes(self._buf[i:i + HEADER_SIZE]))
        except ValueError:
            return False
        return msg_type in TYPE_NAMES       # a known message type, so payload bytes cannot fake a header

    def feed(self, chunk: bytes) -> list[Message]:
        self._buf += chunk
        out: list[Message] = []
        while len(self._buf) >= HEADER_SIZE:
            if not self._valid_at(0):
                # slide to the next position where a plausible header starts; keep a possible partial header
                limit = len(self._buf) - HEADER_SIZE
                i = 1
                while i <= limit and not self._valid_at(i):
                    nxt = self._buf.find(MAGIC, i + 1)
                    i = nxt if nxt != -1 else limit + 1
                i = min(i, len(self._buf) - (HEADER_SIZE - 1)) if i > limit else i
                self.resyncs += 1
                self.skipped += i
                del self._buf[:i]
                continue
            msg_type, length, seq, arg = parse_header(self._buf)
            total = HEADER_SIZE + length
            if len(self._buf) < total:
                break
            out.append(Message(msg_type, seq, arg, bytes(self._buf[HEADER_SIZE:total])))
            del self._buf[:total]
        return out

    def reset(self) -> None:
        self._buf.clear()


class UsbTransport:
    """pyusb/libusb transport for a vendor-class module bound to WinUSB."""

    def __init__(self, dev: Any) -> None:
        import usb.util

        self.dev = dev
        self.serial = usb.util.get_string(dev, dev.iSerialNumber) or f"{dev.bus}-{dev.address}"
        product = usb.util.get_string(dev, dev.iProduct) or "GlassLink DU"
        self.description = f"{product} {dev.idVendor:04x}:{dev.idProduct:04x} bus {dev.bus} addr {dev.address}"
        try:
            dev.set_configuration()
        except Exception:  # noqa: BLE001  (already configured / not needed on WinUSB)
            pass
        cfg = dev.get_active_configuration()
        intf = None
        for candidate in cfg:
            if candidate.bInterfaceClass == 0xFF:
                intf = candidate
                break
        if intf is None:
            intf = cfg[(0, 0)]
        self.intf = intf
        usb.util.claim_interface(dev, intf.bInterfaceNumber)
        self.ep_in = next(e for e in intf if usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_IN
                          and usb.util.endpoint_type(e.bmAttributes) == usb.util.ENDPOINT_TYPE_BULK)
        self.ep_out = next(e for e in intf if usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_OUT
                           and usb.util.endpoint_type(e.bmAttributes) == usb.util.ENDPOINT_TYPE_BULK)
        self._wlock = threading.Lock()

    def read_chunk(self, timeout_ms: int) -> bytes | None:
        import usb.core

        try:
            data = self.ep_in.read(64 * 1024, timeout=timeout_ms)
        except usb.core.USBTimeoutError:
            return None
        return bytes(data)

    def write(self, data: bytes, timeout_ms: int) -> None:
        with self._wlock:
            self.ep_out.write(data, timeout=timeout_ms)
            if len(data) % USB_PACKET == 0:
                self.ep_out.write(b"", timeout=timeout_ms)  # ZLP terminates the transfer

    def close(self) -> None:
        import usb.util

        try:
            usb.util.release_interface(self.dev, self.intf.bInterfaceNumber)
        except Exception:  # noqa: BLE001
            pass
        try:
            usb.util.dispose_resources(self.dev)
        except Exception:  # noqa: BLE001
            pass


def find_usb_modules(ids: tuple[tuple[int, int], ...] = USB_IDS) -> list[Any]:
    import libusb_package
    import usb.core

    backend = libusb_package.get_libusb1_backend()
    found: list[Any] = []
    for vid, pid in ids:
        found.extend(usb.core.find(find_all=True, idVendor=vid, idProduct=pid, backend=backend) or [])
    return found


class FakeModuleTransport:
    """In-process stand-in for a module (for UI development without hardware: `serve --fake-modules 2`).
    Answers GET_INFO, sends READY after every FRAME, reports made-up stats."""

    def __init__(self, serial: str, panel: tuple[int, int] = (768, 768)) -> None:
        import queue

        self.serial = serial
        self.description = f"fake module (in-process) {serial[:8]}"
        self.panel = panel
        self._inbox: "queue.Queue[bytes]" = queue.Queue()
        self._reader = MessageReader()
        self._frames = 0
        self._last_stats = time.time()
        self._inbox.put(pack(T_READY, seq=0))

    def read_chunk(self, timeout_ms: int) -> bytes | None:
        import queue

        if time.time() - self._last_stats > 2.0:
            self._last_stats = time.time()
            self._inbox.put(pack(T_STATS, json.dumps({"fps": 30.0, "decode_ms": 11.2, "dropped": 0}).encode()))
        try:
            return self._inbox.get(timeout=timeout_ms / 1000)
        except queue.Empty:
            return None

    def write(self, data: bytes, timeout_ms: int) -> None:
        if not data:
            return
        for msg in self._reader.feed(data):
            if msg.type == T_GET_INFO:
                info = {"fw": "0.0.0-fake", "hw": "fake", "panel": list(self.panel), "decoder": "none", "uptime_s": 0}
                self._inbox.put(pack(T_INFO, json.dumps(info).encode()))
            elif msg.type == T_FRAME:
                self._frames += 1
                time.sleep(0.03)  # pretend to decode
                self._inbox.put(pack(T_READY, seq=msg.seq))
            elif msg.type == T_PING:
                self._inbox.put(pack(T_PONG, arg=msg.arg))
            elif msg.type == T_SHOW_IDENT:
                self._inbox.put(pack(T_LOG, f"IDENT shown for {msg.arg} s".encode(), arg=1))

    def close(self) -> None:
        pass


def fake_module_finder(count: int):
    """Returns a finder() producing `count` stable fake modules (serials derived from the index)."""
    import hashlib

    fakes = [FakeModuleTransport(hashlib.md5(f"glasslink-fake-{i}".encode()).hexdigest()) for i in range(count)]
    return lambda: fakes


# ---------------------------------------------------------------------------------------------
# Module worker
# ---------------------------------------------------------------------------------------------
class ModuleWorker(threading.Thread):
    """Drives one module: answers READY with the newest frame of the assigned display, collects stats."""

    def __init__(self, transport: Transport, manager: "ModuleManager") -> None:
        super().__init__(name=f"module-{transport.serial[:8]}", daemon=True)
        self.transport = transport
        self.manager = manager
        self.serial = transport.serial
        self.hub = manager.hub
        self.alive = True
        self.error = ""
        self.info: dict[str, Any] = {}
        self.stats: dict[str, Any] = {}
        self.logs: list[str] = []
        self.connected_at = time.time()
        self.frames_sent = 0
        self.bytes_sent = 0
        self.last_seq_sent = 0
        self.last_seq_display: str | None = None   # display the last frame was taken from
        self.ident_until = 0.0                      # host-side view of the module's ident overlay
        self.brightness_sent: int | None = None     # last SET_BRIGHTNESS value (None = resend)
        self.brightness_sim: float | None = None    # sim value 0..1 driving it, None = manual
        self.brightness_source = "manual"
        self._ping_sent: dict[int, float] = {}      # nonce -> time sent
        self.ping_ms: float | None = None           # last measured round trip
        self.last_ready_seq = 0
        self.ready_pending = False
        self.last_msg_at = time.time()
        self._reader = MessageReader()
        self._stop_evt = threading.Event()
        # Health log: one sample per STATS message (every ~2 s), warnings to the log file when out of range,
        # so a stall can be diagnosed after the fact without anyone watching a status page.
        self.history: deque = deque(maxlen=300)
        self._hist_sent = 0
        self._hist_t = time.time()
        self._hist_dropped = 0
        self._health_bad = False

    # -- public ------------------------------------------------------------------------------
    @property
    def display(self) -> str | None:
        return self.manager.assignment(self.serial)

    def send(self, msg_type: int, payload: bytes = b"", seq: int = 0, arg: int = 0) -> None:
        self.transport.write(pack(msg_type, payload, seq, arg), timeout_ms=2000)

    def stop(self) -> None:
        self._stop_evt.set()

    def as_dict(self) -> dict[str, Any]:
        return {
            "serial": self.serial,
            "description": getattr(self.transport, "description", ""),
            "display": self.display,
            "alive": self.alive,
            "error": self.error,
            "info": self.info,
            "stats": self.stats,
            "frames_sent": self.frames_sent,
            "bytes_sent": self.bytes_sent,
            "last_seq_sent": self.last_seq_sent,
            "ident_active": bool(self.stats["ident"]) if "ident" in self.stats else time.time() < self.ident_until,
            "ping_ms": self.ping_ms,
            "brightness": {"sent": self.brightness_sent, "sim": self.brightness_sim, "source": self.brightness_source},
            "connected_s": round(time.time() - self.connected_at, 1),
            "last_msg_age_s": round(time.time() - self.last_msg_at, 1),
            "log_tail": self.logs[-5:],
            "history": list(self.history)[-60:],
            "settings": self.manager.settings(self.serial),
            "label": self.manager.settings(self.serial).get("label", ""),
        }

    # -- thread ------------------------------------------------------------------------------
    def run(self) -> None:
        log.info("module %s connected (%s)", self.serial, getattr(self.transport, "description", ""))
        try:
            self.send(T_GET_INFO)
            while not self._stop_evt.is_set():
                chunk = self.transport.read_chunk(timeout_ms=250)
                if chunk:
                    self.last_msg_at = time.time()
                    try:
                        msgs = self._reader.feed(chunk)
                    except ValueError as exc:
                        log.warning("module %s: %s; resyncing", self.serial, exc)
                        self._reader.reset()
                        msgs = []
                    for m in msgs:
                        self._on_message(m)
                if self.ready_pending:
                    self._serve_ready()
        except Exception as exc:  # noqa: BLE001
            self.error = f"{type(exc).__name__}: {exc}"
            log.info("module %s disconnected: %s", self.serial, self.error)
        finally:
            self.alive = False
            try:
                self.transport.close()
            except Exception:  # noqa: BLE001
                pass

    def _on_message(self, m: Message) -> None:
        if m.type == T_READY:
            self.last_ready_seq = m.seq
            self.ready_pending = True
        elif m.type == T_INFO:
            self.info = m.json() or {}
            log.info("module %s info: %s", self.serial, self.info)
            self._apply_settings()
            # A module answering GET_INFO is idle and can take a frame; its initial READY may have gone to a
            # previous host session (or the module was plugged in before the server started).
            self.ready_pending = True
        elif m.type == T_STATS:
            self.stats = m.json() or {}
            if "ident" in self.stats:
                self.ident_until = float("inf") if self.stats["ident"] else 0.0
            self._health_check()
        elif m.type == T_LOG:
            text = m.payload.decode("utf-8", "replace").strip()
            self.logs.append(text)
            del self.logs[:-50]
            log.debug("module %s log: %s", self.serial, text)
        elif m.type == T_PONG:
            t0 = self._ping_sent.pop(m.arg, None)
            if t0 is not None:
                self.ping_ms = round((time.time() - t0) * 1000, 1)
                log.info("module %s pong %.1f ms", self.serial[:8], self.ping_ms)
        elif m.type == T_OTA_RESULT:
            log.info("module %s OTA result: %s", self.serial, m.arg)
        else:
            log.debug("module %s: unhandled %r", self.serial, m)

    def _apply_settings(self) -> None:
        settings = self.manager.settings(self.serial)
        self.send(T_SET_ASSIGNED, arg=1 if self.display else 0)
        self.brightness_sent = None          # the brightness loop sends the effective value within its next tick
        if settings.get("rotation") is not None:
            self.send(T_SET_ROTATION, arg=int(settings["rotation"]))

    def _health_check(self) -> None:
        now = time.time()
        dt = max(now - self._hist_t, 1e-3)
        sent_rate = (self.frames_sent - self._hist_sent) / dt
        self._hist_sent, self._hist_t = self.frames_sent, now
        name = self.display
        st = self.hub.get(name) if name else None
        src_fps = st.fps() if st else 0.0
        s = self.stats
        dropped = int(s.get("dropped") or 0)
        new_drops = dropped - self._hist_dropped
        self._hist_dropped = dropped
        sample = {
            "t": round(now, 1), "src_fps": round(src_fps, 1), "sent_rate": round(sent_rate, 1),
            "mod_fps": s.get("fps"), "rx_ms": s.get("rx_ms"), "decode_ms": s.get("decode_ms"),
            "draw_ms": s.get("draw_ms"), "dropped": dropped,
        }
        self.history.append(sample)
        if not name:
            return
        reasons = []
        # A static source (0 fps) is normal for a DU that is not changing; only a module falling behind a
        # moving source, slow transfers/decodes or drops are worth a warning.
        if src_fps >= 5 and sent_rate < 0.6 * src_fps and self.connected_at < now - 10:
            reasons.append(f"sending {sent_rate:.1f} fps of {src_fps:.1f} source")
        for key, limit in (("rx_ms", 15), ("decode_ms", 30), ("draw_ms", 30)):
            v = s.get(key)
            if isinstance(v, (int, float)) and v > limit:
                reasons.append(f"{key} {v}")
        if new_drops > 0:
            reasons.append(f"{new_drops} dropped")
        if reasons:
            self._health_bad = True
            log.warning("module %s stall: %s | %s", self.serial[:8], "; ".join(reasons), sample)
        elif self._health_bad:
            self._health_bad = False
            log.info("module %s recovered | %s", self.serial[:8], sample)

    def _serve_ready(self) -> None:
        """A READY is pending: send the newest frame of the assigned display if it is newer than what the
        module has. Non-blocking beyond a short wait so commands and reads keep flowing."""
        name = self.display
        if not name:
            return  # stays pending until assigned; the module shows its own idle screen meanwhile
        st = self.hub.get(name)
        if st is None:
            return
        if name != self.last_seq_display:
            self.last_seq_sent = 0          # new display: its sequence numbers are unrelated, send its newest frame
            self.last_seq_display = name
        seq, jpeg = st.latest()
        if jpeg is None or seq <= self.last_seq_sent:
            # Wait for a newer frame. A condition (not an event) so a signal from a frame we already sent
            # cannot satisfy this wait and leave us blocking in the USB read while new frames pile up.
            with st.tcond:
                st.tcond.wait_for(lambda: st.jpeg is not None and st.seq > self.last_seq_sent, timeout=0.2)
                seq, jpeg = st.latest()
            if jpeg is None or seq <= self.last_seq_sent:
                return
        self.send(T_FRAME, jpeg, seq=seq)
        self.frames_sent += 1
        self.bytes_sent += len(jpeg)
        self.last_seq_sent = seq
        self.ready_pending = False


# ---------------------------------------------------------------------------------------------
# Manager: hot-plug, assignment, commands
# ---------------------------------------------------------------------------------------------
class ModuleManager(threading.Thread):
    def __init__(self, cfg: dict[str, Any], hub: FrameHub, config_path=None, scan_interval: float = 2.0,
                 finder=find_usb_modules, transport_factory=None) -> None:
        super().__init__(name="module-manager", daemon=True)
        self.cfg = cfg
        self.hub = hub
        self.config_path = config_path
        self.scan_interval = scan_interval
        self.finder = finder
        self.transport_factory = transport_factory or UsbTransport
        self.workers: dict[str, ModuleWorker] = {}
        self._stop_evt = threading.Event()
        self._lock = threading.Lock()
        self.last_scan_error = ""
        cfg.setdefault("modules", {})
        self.simvars = None
        self._bright_thread: threading.Thread | None = None
        self._bright_map: dict[str, str] = {}

    # -- brightness: cockpit knobs (sim L:vars) x module trim slider -> SET_BRIGHTNESS ----------------
    def start(self) -> None:
        super().start()
        bcfg = self.cfg.get("brightness") or {}
        if bcfg.get("enabled", True):
            interval = float(bcfg.get("interval_s", 0.1) or 0.1)
            if str(bcfg.get("source", "simconnect")).lower() == "fsuipc_wasm":
                from .simvars import WapiVars

                self.simvars = WapiVars(bcfg.get("wapi_dll"), interval, int(bcfg.get("wasm_hz", 12) or 0))
            else:
                from .simvars import SimConnectVars

                self.simvars = SimConnectVars(interval)
            self.simvars.start()
        self._bright_thread = threading.Thread(target=self._brightness_loop, name="brightness", daemon=True)
        self._bright_thread.start()

    def brightness_map(self) -> dict[str, str]:
        """display name -> L:var for the loaded aircraft. With SimConnect the profile is chosen by the aircraft
        title (as for pop-outs); with the FSUIPC WASM backend by which variables the sim lists."""
        sv = self.simvars
        if not sv or not sv.running:
            self._bright_map = {}
            self._bright_key = None
            return self._bright_map
        from .popout import profiles, select_profile

        title = sv.title()
        if title is not None:
            key, prof = select_profile(self.cfg, title)
            if key != getattr(self, "_bright_key", None):
                self._bright_key = key
                self._bright_map = dict((prof or {}).get("brightness") or {})
                log.info("brightness: aircraft '%s' -> profile %s, variables %s", title, key, self._bright_map)
            return self._bright_map
        if not sv.names:
            self._bright_map = {}
            return self._bright_map
        if self._bright_map and any(sv.has(v) for v in self._bright_map.values()):
            return self._bright_map
        for prof in profiles(self.cfg).values():
            m = prof.get("brightness") or {}
            if m and any(sv.has(v) for v in m.values()):
                self._bright_map = dict(m)
                log.info("brightness: using profile variables %s", self._bright_map)
                return self._bright_map
        self._bright_map = {}
        return self._bright_map

    def brightness_status(self) -> dict[str, Any]:
        out: dict[str, Any] = {"enabled": self.simvars is not None, "map": dict(self._bright_map)}
        if self.simvars:
            out.update(self.simvars.status())
        return out

    def _brightness_loop(self) -> None:
        interval = float((self.cfg.get("brightness") or {}).get("interval_s", 0.1) or 0.1)
        while not self._stop_evt.wait(interval):
            try:
                self._brightness_tick()
            except Exception:  # noqa: BLE001
                log.exception("brightness tick failed")

    def _brightness_tick(self) -> None:
        m = self.brightness_map()
        sv = self.simvars
        for w in list(self.workers.values()):
            if not w.alive:
                continue
            manual = self.settings(w.serial).get("brightness")
            manual = 100 if manual is None else int(manual)
            var = m.get(w.display) if w.display else None
            sim = sv.get(var) if (var and sv and sv.running) else None
            if sim is None:
                pct, src = manual, "manual"
            else:
                pct, src = int(round(max(0.0, min(1.0, float(sim))) * manual)), "sim"
            w.brightness_sim = None if sim is None else round(float(sim), 3)
            w.brightness_source = src
            if pct != w.brightness_sent:
                try:
                    w.send(T_SET_BRIGHTNESS, arg=pct)
                    w.brightness_sent = pct
                except Exception as exc:  # noqa: BLE001
                    log.debug("brightness send to %s failed: %s", w.serial[:8], exc)

    # -- config ------------------------------------------------------------------------------
    def settings(self, serial: str) -> dict[str, Any]:
        return dict(self.cfg["modules"].get(serial, {}))

    def assignment(self, serial: str) -> str | None:
        d = self.cfg["modules"].get(serial, {}).get("display")
        return d if d in self.cfg["displays"] else None

    def assign(self, serial: str, display: str | None, **settings: Any) -> dict[str, Any]:
        with self._lock:
            entry = self.cfg["modules"].setdefault(serial, {})
            if display is not None:
                if display and display not in self.cfg["displays"]:
                    raise ValueError(f"unknown display '{display}'")
                entry["display"] = display or None
            for k in ("brightness", "rotation", "label"):
                if k in settings and settings[k] is not None:
                    entry[k] = settings[k]
            save_config(self.cfg, self.config_path)
        w = self.workers.get(serial)
        if w and w.alive:
            if display is not None:
                w.send(T_SET_ASSIGNED, arg=1 if entry.get("display") else 0)
            if "brightness" in settings and settings["brightness"] is not None:
                w.brightness_sent = None     # recomputed and sent by the brightness loop
            if "rotation" in settings and settings["rotation"] is not None:
                w.send(T_SET_ROTATION, arg=int(settings["rotation"]))
        return dict(entry)

    def command(self, serial: str, cmd: str, arg: int = 0) -> bool:
        w = self.workers.get(serial)
        if not w or not w.alive:
            return False
        if cmd == "ident":
            label = str(self.cfg["modules"].get(serial, {}).get("label") or "")
            w.send(T_SHOW_IDENT, payload=label.encode("utf-8")[:31], arg=arg)   # arg = seconds, 0 = off
            w.ident_until = time.time() + arg if arg > 0 else 0.0
        elif cmd == "ping":
            w._ping_sent[arg] = time.time()
            w.send(T_PING, arg=arg)
        elif cmd == "reboot":
            w.send(T_REBOOT)
        elif cmd == "info":
            w.send(T_GET_INFO)
        else:
            raise ValueError(f"unknown command '{cmd}'")
        return True

    # -- status --------------------------------------------------------------------------------
    def status(self) -> dict[str, Any]:
        out = {serial: w.as_dict() for serial, w in self.workers.items()}
        # Unplugged modules stay listed only while they are worth remembering (assigned or labelled);
        # an unconfigured unit simply disappears when unplugged, so the list cannot fill with ghosts.
        for serial, entry in self.cfg["modules"].items():
            if serial not in out and (entry.get("display") or entry.get("label")):
                out[serial] = {"serial": serial, "display": entry.get("display"), "alive": False,
                               "label": entry.get("label", ""), "error": "not connected", "settings": dict(entry)}
        return out

    def forget(self, serial: str) -> bool:
        """Drop a module's saved assignment/label. Only for modules that are not connected."""
        w = self.workers.get(serial)
        if w and w.alive:
            raise ValueError("module is connected; unplug it first")
        with self._lock:
            removed = self.cfg["modules"].pop(serial, None) is not None
            if removed:
                save_config(self.cfg, self.config_path)
        return removed

    # -- thread ---------------------------------------------------------------------------------
    def stop(self) -> None:
        self._stop_evt.set()
        if self.simvars:
            self.simvars.stop()
        for w in list(self.workers.values()):
            w.stop()

    def scan_once(self) -> None:
        # drop dead workers
        for serial, w in list(self.workers.items()):
            if not w.alive:
                del self.workers[serial]
        try:
            devices = self.finder()
            self.last_scan_error = ""
        except Exception as exc:  # noqa: BLE001
            self.last_scan_error = f"{type(exc).__name__}: {exc}"
            return
        for dev in devices:
            try:
                transport = self.transport_factory(dev)
            except Exception as exc:  # noqa: BLE001
                log.debug("module open failed: %s", exc)
                continue
            if transport.serial in self.workers:
                transport.close()  # already being served
                continue
            w = ModuleWorker(transport, self)
            self.workers[transport.serial] = w
            w.start()

    def run(self) -> None:
        while not self._stop_evt.is_set():
            self.scan_once()
            self._stop_evt.wait(self.scan_interval)
