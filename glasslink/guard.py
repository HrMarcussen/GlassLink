"""Who may do what through the HTTP API (same rules as the .NET DMC's RequestGuard).

The server listens on every interface so a phone can open the status page and the viewer; that must not let a web
page or another device change anything:
  Host    a request must be addressed to this PC (localhost, its name or one of its addresses): DNS rebinding fails;
  Origin  a request from a browser page on another site is refused (tools send no Origin);
  JSON    a POST must be Content-Type application/json, which a foreign page cannot send without a CORS preflight;
  LAN     changes (POST, DELETE) come from this PC only, unless server.allow_lan_control is true.
"""
from __future__ import annotations

import ipaddress
import logging
import socket
import time
from urllib.parse import urlsplit

from aiohttp import web

log = logging.getLogger(__name__)
MUTATING = {"POST", "PUT", "PATCH", "DELETE"}


def local_names() -> set[str]:
    names = {"localhost", "127.0.0.1", "::1"}
    host = socket.gethostname().lower()
    names |= {host, host + ".local", socket.getfqdn().lower()}
    try:
        for info in socket.getaddrinfo(host, None):
            names.add(info[4][0].split("%")[0].lower())
    except OSError:
        pass
    return names


def _this_pc(remote: str | None, names: set[str]) -> bool:
    if not remote:
        return False
    try:
        ip = ipaddress.ip_address(remote.split("%")[0])
    except ValueError:
        return False
    if getattr(ip, "ipv4_mapped", None):
        ip = ip.ipv4_mapped
    return ip.is_loopback or str(ip) in names


def check(method: str, host_header: str, origin: str | None, content_type: str, remote: str | None,
          names: set[str], allow_lan_control: bool) -> str | None:
    """None when the request may go on, else the reason it may not."""
    host = urlsplit("//" + host_header).hostname or ""
    if host.lower() not in names:
        return f"this DMC answers only to its own names and addresses, not '{host}'"
    if origin and origin != "null" and urlsplit(origin).netloc.lower() != host_header.lower():
        return "requests from other web sites are refused"
    if method not in MUTATING:
        return None
    if method == "POST" and not content_type.lower().startswith("application/json"):
        return "changes must be sent as application/json"
    if not allow_lan_control and not _this_pc(remote, names):
        return "changes are accepted from this PC only (set server.allow_lan_control to true in config.json to allow other devices)"
    return None


def middleware(cfg: dict):
    state = {"names": local_names(), "at": time.monotonic(), "logged": set()}

    @web.middleware
    async def guard(request: web.Request, handler):
        if time.monotonic() - state["at"] > 60:
            state["names"], state["at"] = local_names(), time.monotonic()
        reason = check(request.method, request.host, request.headers.get("Origin"), request.content_type or "",
                       request.remote, state["names"], bool((cfg.get("server") or {}).get("allow_lan_control")))
        if reason is None:
            return await handler(request)
        key = (reason, request.path)
        if key not in state["logged"]:
            state["logged"].add(key)
            log.warning("refused %s %s from %s: %s", request.method, request.path, request.remote, reason)
        raise web.HTTPForbidden(text=reason)

    return guard
