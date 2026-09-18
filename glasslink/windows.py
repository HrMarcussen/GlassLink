"""Win32 window helpers: DPI awareness, enumeration, matching, rename/resize, crop geometry.

All rectangles are in physical pixels (the process is made per-monitor DPI aware on import
of this module through set_dpi_aware(), which must run before any other user32 call).
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import re
from dataclasses import dataclass
from typing import Any, Iterable

import psutil
import win32con
import win32gui
import win32process

user32 = ctypes.windll.user32
dwmapi = ctypes.windll.dwmapi

_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = ctypes.c_void_p(-4 & 0xFFFFFFFFFFFFFFFF)
_DWMWA_EXTENDED_FRAME_BOUNDS = 9
_DWMWA_CLOAKED = 14


def set_dpi_aware() -> None:
    """Make this process per-monitor DPI aware so all rects are physical pixels."""
    try:
        fn = user32.SetProcessDpiAwarenessContext
        fn.argtypes = [ctypes.c_void_p]
        fn.restype = wt.BOOL
        if fn(_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2):
            return
    except Exception:
        pass
    try:
        user32.SetProcessDPIAware()
    except Exception:
        pass


set_dpi_aware()


@dataclass(frozen=True)
class Rect:
    left: int
    top: int
    right: int
    bottom: int

    @property
    def width(self) -> int:
        return self.right - self.left

    @property
    def height(self) -> int:
        return self.bottom - self.top

    @property
    def size(self) -> tuple[int, int]:
        return (self.width, self.height)

    def as_list(self) -> list[int]:
        return [self.left, self.top, self.right, self.bottom]


@dataclass
class WindowInfo:
    hwnd: int
    title: str
    cls: str
    pid: int
    process: str
    visible: bool
    minimized: bool
    cloaked: bool
    rect: Rect          # GetWindowRect (includes invisible resize borders on Win10/11)
    client: Rect        # client area in screen coordinates
    frame: Rect         # DWM extended frame bounds (what the user sees, no shadow)
    style: int
    exstyle: int

    def as_dict(self) -> dict[str, Any]:
        return {
            "hwnd": self.hwnd,
            "hwnd_hex": hex(self.hwnd),
            "title": self.title,
            "class": self.cls,
            "pid": self.pid,
            "process": self.process,
            "visible": self.visible,
            "minimized": self.minimized,
            "cloaked": self.cloaked,
            "rect": self.rect.as_list(),
            "client": self.client.as_list(),
            "client_size": list(self.client.size),
            "frame": self.frame.as_list(),
            "style": hex(self.style),
            "exstyle": hex(self.exstyle),
        }


_proc_names: dict[int, str] = {}


def process_name(pid: int) -> str:
    name = _proc_names.get(pid)
    if name is None:
        try:
            name = psutil.Process(pid).name()
        except Exception:
            name = "?"
        _proc_names[pid] = name
    return name


def window_rect(hwnd: int) -> Rect:
    return Rect(*win32gui.GetWindowRect(hwnd))


def client_rect_screen(hwnd: int) -> Rect:
    l, t, r, b = win32gui.GetClientRect(hwnd)
    x, y = win32gui.ClientToScreen(hwnd, (l, t))
    return Rect(x, y, x + (r - l), y + (b - t))


def extended_frame_rect(hwnd: int) -> Rect:
    rect = wt.RECT()
    hr = dwmapi.DwmGetWindowAttribute(
        wt.HWND(hwnd), _DWMWA_EXTENDED_FRAME_BOUNDS, ctypes.byref(rect), ctypes.sizeof(rect)
    )
    if hr != 0:
        return window_rect(hwnd)
    return Rect(rect.left, rect.top, rect.right, rect.bottom)


def is_cloaked(hwnd: int) -> bool:
    val = wt.DWORD()
    hr = dwmapi.DwmGetWindowAttribute(wt.HWND(hwnd), _DWMWA_CLOAKED, ctypes.byref(val), ctypes.sizeof(val))
    return hr == 0 and val.value != 0


def window_info(hwnd: int) -> WindowInfo | None:
    try:
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        return WindowInfo(
            hwnd=hwnd,
            title=win32gui.GetWindowText(hwnd),
            cls=win32gui.GetClassName(hwnd),
            pid=pid,
            process=process_name(pid),
            visible=bool(win32gui.IsWindowVisible(hwnd)),
            minimized=bool(win32gui.IsIconic(hwnd)),
            cloaked=is_cloaked(hwnd),
            rect=window_rect(hwnd),
            client=client_rect_screen(hwnd),
            frame=extended_frame_rect(hwnd),
            style=win32gui.GetWindowLong(hwnd, win32con.GWL_STYLE),
            exstyle=win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE),
        )
    except Exception:
        return None


def enum_windows(include_hidden: bool = False, process: str | None = None, pid: int | None = None) -> list[WindowInfo]:
    """Top-level windows. By default only visible, non-cloaked windows with a non-empty client area."""
    hwnds: list[int] = []
    win32gui.EnumWindows(lambda h, _: hwnds.append(h) or True, None)
    out: list[WindowInfo] = []
    for h in hwnds:
        info = window_info(h)
        if info is None:
            continue
        if pid is not None and info.pid != pid:
            continue
        if process is not None and info.process.lower() != process.lower():
            continue
        if not include_hidden:
            if not info.visible or info.cloaked:
                continue
            if info.client.width <= 0 or info.client.height <= 0:
                continue
        out.append(info)
    return out


def match_window(match: dict[str, Any], candidates: Iterable[WindowInfo] | None = None) -> WindowInfo | None:
    """Find a window matching the config rule.

    Rule keys (all optional, all must hold): process, class, title (substring, case-insensitive),
    title_exact, title_regex, client_size [w, h], hwnd (only valid within one session).
    """
    if candidates is None:
        candidates = enum_windows(include_hidden=bool(match.get("include_hidden")))
    hits: list[WindowInfo] = []
    for w in candidates:
        if match.get("hwnd") and w.hwnd != int(match["hwnd"]):
            continue
        if match.get("process") and w.process.lower() != str(match["process"]).lower():
            continue
        if match.get("class") and w.cls != match["class"]:
            continue
        if match.get("title_exact") is not None and w.title != match["title_exact"]:
            continue
        if match.get("title") and str(match["title"]).lower() not in w.title.lower():
            continue
        if match.get("title_regex") and not re.search(match["title_regex"], w.title):
            continue
        if match.get("client_size") and list(w.client.size) != list(match["client_size"]):
            continue
        hits.append(w)
    if not hits:
        return None
    # Deterministic choice: prefer non-minimized, then smallest (pop-outs are smaller than the sim).
    hits.sort(key=lambda w: (w.minimized, w.client.width * w.client.height))
    return hits[0]


def rename_window(hwnd: int, title: str) -> None:
    win32gui.SetWindowText(hwnd, title)


def set_client_size(hwnd: int, width: int, height: int, x: int | None = None, y: int | None = None) -> None:
    """Resize (and optionally move) a window so that its client area is width x height."""
    wr = window_rect(hwnd)
    cr = client_rect_screen(hwnd)
    extra_w = wr.width - cr.width
    extra_h = wr.height - cr.height
    flags = win32con.SWP_NOZORDER | win32con.SWP_NOACTIVATE
    if x is None or y is None:
        flags |= win32con.SWP_NOMOVE
        x, y = 0, 0
    win32gui.SetWindowPos(hwnd, 0, int(x), int(y), int(width + extra_w), int(height + extra_h), flags)


def move_window(hwnd: int, x: int, y: int) -> None:
    flags = win32con.SWP_NOZORDER | win32con.SWP_NOACTIVATE | win32con.SWP_NOSIZE
    win32gui.SetWindowPos(hwnd, 0, int(x), int(y), 0, 0, flags)


def show_window_noactivate(hwnd: int) -> None:
    win32gui.ShowWindow(hwnd, win32con.SW_SHOWNOACTIVATE)


def send_to_back(hwnd: int) -> None:
    flags = win32con.SWP_NOACTIVATE | win32con.SWP_NOSIZE | win32con.SWP_NOMOVE
    win32gui.SetWindowPos(hwnd, win32con.HWND_BOTTOM, 0, 0, 0, 0, flags)


def is_window(hwnd: int) -> bool:
    return bool(win32gui.IsWindow(hwnd))


def compute_crop(hwnd: int, frame_w: int, frame_h: int) -> tuple[int, int, int, int]:
    """Client-area crop (x, y, w, h) inside a captured frame of size frame_w x frame_h.

    Capture backends deliver either the DWM extended-frame rect or the GetWindowRect rect
    (which includes the invisible resize borders). We pick whichever size matches the frame.
    """
    cr = client_rect_screen(hwnd)
    fr = extended_frame_rect(hwnd)
    wr = window_rect(hwnd)
    if (frame_w, frame_h) == fr.size:
        ox, oy = fr.left, fr.top
    elif (frame_w, frame_h) == wr.size:
        ox, oy = wr.left, wr.top
    else:
        # Unknown geometry (e.g. DPI change mid-flight); assume window rect and clamp.
        ox, oy = wr.left, wr.top
    x = max(0, cr.left - ox)
    y = max(0, cr.top - oy)
    w = min(cr.width, frame_w - x)
    h = min(cr.height, frame_h - y)
    if w <= 0 or h <= 0:
        return (0, 0, frame_w, frame_h)
    return (x, y, w, h)


def main_window_of(process: str) -> WindowInfo | None:
    """Largest visible window of a process (used to exclude the sim's main window)."""
    wins = enum_windows(process=process)
    if not wins:
        return None
    return max(wins, key=lambda w: w.client.width * w.client.height)


def hwnd_set(process: str, cls: str | None = None) -> set[int]:
    return {w.hwnd for w in enum_windows(process=process) if cls is None or w.cls == cls}
