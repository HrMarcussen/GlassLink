"""Unit tests for the USB module path using a fake transport (no hardware needed).

    .venv\\Scripts\\python -m unittest discover -s tests -v
"""

from __future__ import annotations

import asyncio
import json
import queue
import sys
import threading
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from glasslink import modules as m  # noqa: E402
from glasslink.hub import FrameHub  # noqa: E402


class FakeTransport:
    """Behaves like a module: answers GET_INFO with INFO, sends READY, and after every FRAME sends READY again."""

    def __init__(self, serial: str, panel=(768, 768)) -> None:
        self.serial = serial
        self.description = f"fake {serial}"
        self.panel = panel
        self.inbox: queue.Queue[bytes] = queue.Queue()   # module -> host
        self.received: list[m.Message] = []              # host -> module (parsed)
        self.frames: list[tuple[int, bytes]] = []
        self.closed = False
        self._reader = m.MessageReader()
        self.ota = bytearray()                           # firmware image received so far
        self.ota_expected = 0
        self.ota_installed: bytes | None = None
        self.ota_corrupt_at: int | None = None           # simulate a flash write error at this offset
        self.inbox.put(m.pack(m.T_READY, seq=0))

    def read_chunk(self, timeout_ms: int) -> bytes | None:
        try:
            data = self.inbox.get(timeout=timeout_ms / 1000)
        except queue.Empty:
            return None
        # split into 512-byte USB packets to exercise reassembly
        return data

    def write(self, data: bytes, timeout_ms: int) -> None:
        if data == b"":
            return  # ZLP
        for msg in self._reader.feed(data):
            self.received.append(msg)
            if msg.type == m.T_GET_INFO:
                info = {"fw": "0.0.1", "hw": "fake", "panel": list(self.panel), "decoder": "sw"}
                self.inbox.put(m.pack(m.T_INFO, json.dumps(info).encode()))
            elif msg.type == m.T_FRAME:
                self.frames.append((msg.seq, msg.payload))
                self.inbox.put(m.pack(m.T_STATS, json.dumps({"fps": 30.0}).encode()))
                self.inbox.put(m.pack(m.T_READY, seq=msg.seq))
            elif msg.type == m.T_PING:
                self.inbox.put(m.pack(m.T_PONG, arg=msg.arg))
            elif msg.type == m.T_OTA_BEGIN:
                self.ota = bytearray()
                self.ota_expected = msg.arg
                self.inbox.put(m.pack(m.T_OTA_PROGRESS, arg=0))
            elif msg.type == m.T_OTA_DATA:
                if msg.arg != len(self.ota):
                    self.inbox.put(m.pack(m.T_OTA_RESULT, arg=7))
                elif self.ota_corrupt_at is not None and msg.arg >= self.ota_corrupt_at:
                    self.inbox.put(m.pack(m.T_OTA_RESULT, arg=2))
                else:
                    self.ota += msg.payload
                    self.inbox.put(m.pack(m.T_OTA_PROGRESS, arg=len(self.ota)))
            elif msg.type == m.T_OTA_END:
                import zlib

                if len(self.ota) != self.ota_expected:
                    self.inbox.put(m.pack(m.T_OTA_RESULT, arg=5))
                elif zlib.crc32(bytes(self.ota)) & 0xFFFFFFFF != msg.arg:
                    self.inbox.put(m.pack(m.T_OTA_RESULT, arg=4))
                else:
                    self.ota_installed = bytes(self.ota)
                    self.inbox.put(m.pack(m.T_OTA_RESULT, arg=0))

    def close(self) -> None:
        self.closed = True


def make_hub_with_display(name: str) -> tuple[FrameHub, asyncio.AbstractEventLoop, threading.Thread]:
    loop = asyncio.new_event_loop()
    t = threading.Thread(target=loop.run_forever, daemon=True)
    t.start()
    hub = FrameHub(loop)
    hub.add(name)
    return hub, loop, t


class FramingTests(unittest.TestCase):
    def test_pack_and_parse_roundtrip(self) -> None:
        data = m.pack(m.T_FRAME, b"abc", seq=42, arg=7)
        self.assertEqual(len(data), m.HEADER_SIZE + 3)
        msgs = m.MessageReader().feed(data)
        self.assertEqual(len(msgs), 1)
        self.assertEqual((msgs[0].type, msgs[0].seq, msgs[0].arg, msgs[0].payload), (m.T_FRAME, 42, 7, b"abc"))

    def test_reassembly_across_chunks(self) -> None:
        payload = bytes(range(256)) * 10
        data = m.pack(m.T_LOG, payload, arg=1) + m.pack(m.T_READY, seq=5)
        r = m.MessageReader()
        got = []
        for i in range(0, len(data), 512):
            got += r.feed(data[i : i + 512])
        self.assertEqual([x.type for x in got], [m.T_LOG, m.T_READY])
        self.assertEqual(got[0].payload, payload)
        self.assertEqual(got[1].seq, 5)

    def test_bad_magic_is_skipped_not_fatal(self) -> None:
        r = m.MessageReader()
        self.assertEqual(r.feed(b"ZZ" + bytes(14)), [])
        self.assertEqual(r.resyncs, 1)
        self.assertEqual([x.type for x in r.feed(m.pack(m.T_READY, seq=3))], [m.T_READY])
        with self.assertRaises(ValueError):
            m.parse_header(b"ZZ" + bytes(14))          # the low-level parser still rejects it


class ModuleFlowTests(unittest.TestCase):
    def setUp(self) -> None:
        self.hub, self.loop, self.loop_thread = make_hub_with_display("pfd")
        self.hub.add("nd")
        self.cfg = {"displays": {"pfd": {}, "nd": {}}, "modules": {}}
        self.fakes: dict[str, FakeTransport] = {}

    def tearDown(self) -> None:
        self.loop.call_soon_threadsafe(self.loop.stop)
        self.loop_thread.join(2)

    def _manager(self, serials: list[str]) -> m.ModuleManager:
        devs = [FakeTransport(s) for s in serials]
        for d in devs:
            self.fakes[d.serial] = d
        mm = m.ModuleManager(self.cfg, self.hub, config_path=Path("nonexistent-test-config.json"),
                             scan_interval=0.1, finder=lambda: devs, transport_factory=lambda d: d)
        # never write the config file during tests
        mm.assign = _no_save_assign.__get__(mm, m.ModuleManager)
        return mm

    def _publish(self, name: str, jpeg: bytes) -> None:
        self.hub.publish_threadsafe(name, jpeg, 768, 768, time.monotonic())

    def _wait(self, cond, timeout=3.0) -> bool:
        t0 = time.time()
        while time.time() - t0 < timeout:
            if cond():
                return True
            time.sleep(0.02)
        return False

    def test_unassigned_module_gets_info_but_no_frames(self) -> None:
        mm = self._manager(["AAAA"])
        mm.scan_once()
        w = mm.workers["AAAA"]
        self.assertTrue(self._wait(lambda: w.info.get("hw") == "fake"))
        self._publish("pfd", b"\xff\xd8frame1")
        time.sleep(0.5)
        self.assertEqual(self.fakes["AAAA"].frames, [])
        self.assertIsNone(w.display)
        mm.stop()

    def test_assignment_streams_newest_frame_and_flow_control(self) -> None:
        mm = self._manager(["AAAA"])
        mm.scan_once()
        w = mm.workers["AAAA"]
        mm.assign("AAAA", "pfd", brightness=70)
        self.assertEqual(w.display, "pfd")
        self._publish("pfd", b"\xff\xd8f1")
        self.assertTrue(self._wait(lambda: len(self.fakes["AAAA"].frames) >= 1))
        self.assertEqual(self.fakes["AAAA"].frames[0][1], b"\xff\xd8f1")
        # two frames published quickly: module must end up with the newest, never a stale one
        self._publish("pfd", b"\xff\xd8f2")
        self._publish("pfd", b"\xff\xd8f3")
        self.assertTrue(self._wait(lambda: self.fakes["AAAA"].frames[-1][1] == b"\xff\xd8f3"))
        seqs = [s for s, _ in self.fakes["AAAA"].frames]
        self.assertEqual(seqs, sorted(seqs))
        self.assertTrue(any(x.type == m.T_SET_BRIGHTNESS and x.arg == 70 for x in self.fakes["AAAA"].received))
        self.assertTrue(self._wait(lambda: w.stats.get("fps") == 30.0))
        mm.stop()

    def test_two_modules_two_displays(self) -> None:
        mm = self._manager(["AAAA", "BBBB"])
        mm.scan_once()
        mm.assign("AAAA", "pfd")
        mm.assign("BBBB", "nd")
        self._publish("pfd", b"\xff\xd8PFD")
        self._publish("nd", b"\xff\xd8ND")
        self.assertTrue(self._wait(lambda: self.fakes["AAAA"].frames and self.fakes["BBBB"].frames))
        self.assertEqual(self.fakes["AAAA"].frames[-1][1], b"\xff\xd8PFD")
        self.assertEqual(self.fakes["BBBB"].frames[-1][1], b"\xff\xd8ND")
        st = mm.status()
        self.assertEqual({st["AAAA"]["display"], st["BBBB"]["display"]}, {"pfd", "nd"})
        mm.stop()

    def test_reassign_switches_display(self) -> None:
        mm = self._manager(["AAAA"])
        mm.scan_once()
        mm.assign("AAAA", "pfd")
        self._publish("pfd", b"\xff\xd8P1")
        self._publish("nd", b"\xff\xd8N1")
        self.assertTrue(self._wait(lambda: self.fakes["AAAA"].frames))
        mm.assign("AAAA", "nd")
        self.assertTrue(self._wait(lambda: self.fakes["AAAA"].frames[-1][1] == b"\xff\xd8N1"))
        mm.stop()

    def test_unplug_and_replug(self) -> None:
        mm = self._manager(["AAAA"])
        mm.scan_once()
        w = mm.workers["AAAA"]
        mm.assign("AAAA", "pfd")
        # simulate a USB error on read
        def boom(timeout_ms):
            raise OSError("device gone")
        self.fakes["AAAA"].read_chunk = boom  # type: ignore[method-assign]
        self.assertTrue(self._wait(lambda: not w.alive))
        self.assertTrue(self.fakes["AAAA"].closed)
        # replug: a new transport object with the same serial
        new = FakeTransport("AAAA")
        mm.finder = lambda: [new]
        mm.scan_once()
        w2 = mm.workers["AAAA"]
        self.assertIsNot(w2, w)
        self.assertEqual(w2.display, "pfd")  # assignment survives
        self._publish("pfd", b"\xff\xd8again")
        self.assertTrue(self._wait(lambda: new.frames))
        mm.stop()

    def test_command_ident(self) -> None:
        mm = self._manager(["AAAA"])
        mm.scan_once()
        self.assertTrue(mm.command("AAAA", "ident", 5))
        self.assertTrue(self._wait(lambda: any(x.type == m.T_SHOW_IDENT and x.arg == 5 for x in self.fakes["AAAA"].received)))
        self.assertFalse(mm.command("ZZZZ", "ident"))
        mm.stop()


def _no_save_assign(self, serial, display, **settings):
    entry = self.cfg["modules"].setdefault(serial, {})
    if display is not None:
        if display and display not in self.cfg["displays"]:
            raise ValueError(f"unknown display '{display}'")
        entry["display"] = display or None
    for k in ("brightness", "rotation", "label"):
        if k in settings and settings[k] is not None:
            entry[k] = settings[k]
    w = self.workers.get(serial)
    if w and w.alive:
        w.last_seq_sent = 0
        if settings.get("brightness") is not None:
            w.send(m.T_SET_BRIGHTNESS, arg=int(settings["brightness"]))
        if settings.get("rotation") is not None:
            w.send(m.T_SET_ROTATION, arg=int(settings["rotation"]))
    return dict(entry)


class FirmwareUpdateTests(ModuleFlowTests):
    """Stop-and-wait firmware transfer against the fake DU."""

    def _image(self, size: int = 100_000) -> bytes:
        return bytes((i * 7) & 0xFF for i in range(size))

    def test_update_transfers_the_whole_image_and_reports_ok(self) -> None:
        mm = self._manager(["AAAA"])
        mm.scan_once()
        w = mm.workers["AAAA"]
        self.assertTrue(self._wait(lambda: w.info.get("hw") == "fake"))
        image = self._image()
        mm.ota_status["AAAA"] = {"state": "running", "progress": 0.0}
        w._ota_image = image
        self.assertTrue(self._wait(lambda: mm.ota_status["AAAA"].get("state") in ("ok", "error"), timeout=10))
        self.assertEqual(mm.ota_status["AAAA"]["state"], "ok", mm.ota_status["AAAA"])
        self.assertEqual(self.fakes["AAAA"].ota_installed, image)
        chunks = [x for x in self.fakes["AAAA"].received if x.type == m.T_OTA_DATA]
        self.assertEqual(len(chunks), -(-len(image) // m.OTA_CHUNK))
        self.assertEqual([c.arg for c in chunks], list(range(0, len(image), m.OTA_CHUNK)))
        self.assertEqual(w.as_dict()["ota"]["progress"], 1.0)
        mm.stop()

    def test_update_reports_a_write_error_from_the_du(self) -> None:
        mm = self._manager(["AAAA"])
        mm.scan_once()
        w = mm.workers["AAAA"]
        self.assertTrue(self._wait(lambda: w.info.get("hw") == "fake"))
        self.fakes["AAAA"].ota_corrupt_at = m.OTA_CHUNK
        mm.ota_status["AAAA"] = {"state": "running", "progress": 0.0}
        w._ota_image = self._image()
        self.assertTrue(self._wait(lambda: mm.ota_status["AAAA"].get("state") in ("ok", "error"), timeout=10))
        self.assertEqual(mm.ota_status["AAAA"]["state"], "error")
        self.assertIn("flash write failed", mm.ota_status["AAAA"]["message"])
        self.assertIsNone(self.fakes["AAAA"].ota_installed)
        self.assertTrue(w.alive)                         # a failed update leaves the DU connected and streaming
        mm.stop()

    def test_image_descriptor_is_checked(self) -> None:
        import struct

        from glasslink import firmware

        with self.assertRaises(ValueError):
            firmware.describe(b"not an image" * 100)
        img = bytearray(1024)
        img[0] = 0xE9
        struct.pack_into("<I", img, 32, 0xABCD5432)
        img[32 + 16:32 + 16 + 5] = b"9.9.9"
        img[32 + 48:32 + 48 + 12] = b"glasslink_du"
        self.assertEqual(firmware.describe(bytes(img))["version"], "9.9.9")
        img[32 + 48:32 + 48 + 12] = b"other_projec"
        with self.assertRaises(ValueError):
            firmware.describe(bytes(img))


class FirmwareVersionTests(unittest.TestCase):
    def test_outdated_only_below_the_firmware_release(self):
        import glasslink
        from glasslink.modules import _fw_outdated

        old = glasslink.firmware_version
        try:
            glasslink.firmware_version = "0.2.0"
            self.assertTrue(_fw_outdated("0.1.0"))
            self.assertFalse(_fw_outdated("0.2.0"))
            self.assertFalse(_fw_outdated("0.2.5"))      # built from a later patch release: fine
            self.assertFalse(_fw_outdated(None))         # no info yet
            self.assertTrue(_fw_outdated("0.1.9-dirty"))
        finally:
            glasslink.firmware_version = old


class ProcessTuningTests(unittest.TestCase):
    def test_pick_affinity(self):
        from glasslink.server import pick_affinity

        self.assertIsNone(pick_affinity(4))                      # small machines: no pinning
        self.assertEqual(pick_affinity(8), [6, 7])
        self.assertEqual(pick_affinity(12), [8, 9, 10, 11])      # i7-8700K: the last two physical cores
        self.assertEqual(pick_affinity(32), list(range(22, 32)))


class ReaderResyncTests(unittest.TestCase):
    def test_garbage_before_and_between_messages_is_skipped(self):
        from glasslink.modules import MessageReader, T_PONG, T_READY, pack

        r = MessageReader()
        stale_tail = b'w"\x00\x13 tail of a message cut off by the previous host session'
        stream = stale_tail + pack(T_READY, seq=7) + b"XDjunk" + pack(T_PONG, arg=4660)
        got = []
        for i in range(0, len(stream), 5):                 # arbitrary chunking
            got += r.feed(stream[i:i + 5])
        self.assertEqual([(m.type, m.seq, m.arg) for m in got], [(T_READY, 7, 0), (T_PONG, 0, 4660)])
        self.assertGreaterEqual(r.resyncs, 2)

    def test_clean_stream_needs_no_resync(self):
        from glasslink.modules import MessageReader, T_STATS, pack

        r = MessageReader()
        got = r.feed(pack(T_STATS, b'{"fps": 1}') * 3)
        self.assertEqual(len(got), 3)
        self.assertEqual(r.resyncs, 0)


if __name__ == "__main__":
    unittest.main()
