using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlassLink.Capture.Windows;
using GlassLink.Core.Config;
using GlassLink.Core.Du;
using GlassLink.Sim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GlassLink.Dmc;

/// <summary>
/// The HTTP API, identical to the Python DMC's (glasslink/server.py), so the same status page and viewer work
/// against either: same routes, same JSON field names.
/// </summary>
public static class Api
{
    public static void Map(WebApplication app, DmcRuntime dmc, Action shutdown)
    {
        var statics = Path.Combine(AppContext.BaseDirectory, "static");
        IResult Page(string file) => Results.File(Path.Combine(statics, file), "text/html; charset=utf-8");

        app.MapGet("/", () => Page("admin.html"));
        app.MapGet("/view/{name}", (string name) => dmc.Displays.Get(name) is null ? Results.NotFound() : Page("viewer.html"));
        app.MapGet("/status", () => Json(Status(dmc)));

        // -- pictures ----------------------------------------------------------------------------------
        app.MapGet("/snapshot/{file}", (string file) =>
            dmc.Displays.Slot(Path.GetFileNameWithoutExtension(file))?.Latest is { } frame ? Results.Bytes(frame.Jpeg.ToArray(), "image/jpeg") : Results.NotFound());
        app.MapGet("/stream/{name}", (string name, HttpContext http) => Mjpeg(dmc, name, http));
        app.Map("/ws/{name}", (string name, HttpContext http) => Viewer(dmc, name, http));

        // -- display units -------------------------------------------------------------------------------
        app.MapGet("/modules", () => Json(new JsonObject { ["modules"] = Modules(dmc), ["displays"] = new JsonArray([.. dmc.Displays.All.Select(e => (JsonNode)e.Name)]) }));
        app.MapPost("/modules/{serial}", async (string serial, HttpRequest request) => await Guarded(async () =>
        {
            var body = await Body(request);
            if (body.ContainsKey("display") || body.ContainsKey("brightness") || body.ContainsKey("label"))
            {
                dmc.Dus.Assign(serial, Str(body["display"]), Str(body["label"]), body["brightness"] is { } b && b.GetValueKind() == JsonValueKind.Number ? (int)b.AsDouble() : null);
            }

            if (Str(body["command"]) is { Length: > 0 } command && !dmc.Command(serial, command, body["arg"] is { } a && a.GetValueKind() == JsonValueKind.Number ? (int)a.AsDouble() : 0))
            {
                return Plain(404, "module not connected");
            }

            return Json(Modules(dmc)[serial]?.DeepClone() ?? new JsonObject());
        }));
        app.MapDelete("/modules/{serial}", (string serial) =>
            dmc.Dus.Forget(serial) ? Json(new JsonObject { ["forgotten"] = true }) : Plain(409, "module is connected; unplug it first"));

        // -- displays (editor), pop-outs, learning ------------------------------------------------------------
        app.MapGet("/displays", () =>
        {
            var profile = Profiles.Select(dmc.Config.Root, dmc.Camera.Title) ?? Profiles.Select(dmc.Config.Root, dmc.Config.Read(r => Str((r["popout"] as JsonObject)?["aircraft"])));
            return Json(new JsonObject { ["profile"] = profile?.Key, ["displays"] = dmc.Displays.Describe(profile) });
        });
        app.MapPost("/displays", async (HttpRequest request) => await Guarded(async () =>
        {
            var body = await Body(request);
            return Json(dmc.Displays.Add(Str(body["name"]), body));
        }));
        app.MapPost("/displays/{name}", async (string name, HttpRequest request) => await Guarded(async () => Json(dmc.Displays.Update(name, await Body(request)))));
        app.MapDelete("/displays/{name}", (string name) => GuardedSync(() =>
        {
            dmc.Displays.Remove(name);
            return Json(new JsonObject { ["removed"] = name });
        }));
        app.MapPost("/displays/{name}/learn", async (string name, HttpRequest request) =>
        {
            try
            {
                var view = Str((await Body(request))["view"]) ?? "standard";
                dmc.Learner.Start(name, view == "mine" ? "copilot" : view);
                return Json(Learn(dmc));
            }
            catch (InvalidOperationException ex)
            {
                return Plain(409, ex.Message);
            }
        });
        app.MapPost("/learn/cancel", () =>
        {
            dmc.Learner.Cancel();
            return Json(Learn(dmc));
        });
        app.MapPost("/displays/{name}/close", (string name) =>
        {
            if (dmc.Displays.Get(name) is not { } entry)
            {
                return Plain(404, $"unknown display '{name}'");
            }

            if (dmc.Learner.State.Busy)
            {
                return Plain(409, "busy learning a display");
            }

            var window = entry.Capture.Window;
            if (window is not null && entry.Capture.HasWindow)
            {
                WindowFinder.Close(window.Handle);
                dmc.Auto?.Retry(name);
            }

            return Json(new JsonObject { ["closed"] = window is not null });
        });
        app.MapPost("/popouts/close-strays", () =>
        {
            var strays = PopoutProcedure.StrayPopouts();
            strays.ForEach(w => WindowFinder.Close(w.Handle));
            return Json(new JsonObject { ["closed"] = strays.Count });
        });
        app.MapGet("/popout/settings", () => Json(new JsonObject { ["camera_restore_key"] = PopoutSettings.From(dmc.Config.Root).RestoreKey }));
        app.MapPost("/popout/settings", async (HttpRequest request) =>
        {
            var key = (Str((await Body(request))["camera_restore_key"]) ?? "").Trim().ToLowerInvariant();
            try
            {
                if (key.Length > 0)
                {
                    Input.ParseCombo(key);
                }
            }
            catch (FormatException ex)
            {
                return Plain(400, ex.Message);
            }

            dmc.Config.Update(root => ConfigFile.Section(root, "popout")["camera_restore_key"] = key.Length > 0 ? key : null);
            return Json(new JsonObject { ["camera_restore_key"] = key.Length > 0 ? key : null });
        });

        // -- stopping: only from this PC ------------------------------------------------------------------------
        app.MapPost("/shutdown", (HttpContext http) =>
        {
            if (http.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
            {
                return Results.StatusCode(403);
            }

            shutdown();
            return Json(new JsonObject { ["stopping"] = true });
        });
    }

    // -- /status ---------------------------------------------------------------------------------------------
    public static JsonObject Status(DmcRuntime dmc)
    {
        var popout = dmc.Auto?.State ?? new AutoPopoutState("off", "auto pop-out disabled", [], null);
        var brightness = dmc.Brightness.Status();
        var strays = PopoutProcedure.SimMainWindow() is null ? [] : PopoutProcedure.StrayPopouts();
        var dus = dmc.Dus.Status();
        var advice = dmc.Advisor.Advise(new AdvisorInput(
            dmc.Displays.All.Select(e => (e.Name, e.Capture.Counters.Received, InUse(dmc, e), e.Capture.HasWindow, e.Capture.CurrentFps, dmc.Displays.IsSimDisplay(e.Name))).ToList(),
            dus.Select(d => (d.Serial, d.Label, d.Alive, d.Display, Firmware.IsOutdated(d.Info?.Firmware, dmc.FirmwareVersion), d.Health)).ToList(),
            strays.Count, popout.Status, popout.Detail, popout.Missing, brightness.Running, brightness.Map.Count == 0, brightness.Standdown, brightness.Aircraft));

        return new JsonObject
        {
            ["version"] = dmc.Version,
            ["build"] = dmc.Build,
            ["engine"] = ".NET",
            ["firmware_version"] = dmc.FirmwareVersion,
            ["firmware_image"] = FirmwareSummary(dmc),
            ["process"] = new JsonObject { ["priority"] = dmc.Process.Priority, ["affinity"] = new JsonArray([.. dmc.Process.Affinity.Select(c => (JsonNode)c)]) },
            ["displays"] = Displays(dmc),
            ["modules"] = Modules(dmc),
            ["usb"] = new JsonObject { ["enabled"] = true, ["scan_error"] = dmc.Dus.LastScanError },
            ["brightness"] = new JsonObject
            {
                ["enabled"] = dmc.BrightnessEnabled, ["source"] = "simconnect", ["running"] = brightness.Running, ["aircraft"] = brightness.Aircraft,
                ["standdown"] = brightness.Standdown, ["error"] = brightness.Running ? "" : "sim not running",
                ["map"] = new JsonObject(brightness.Map.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            },
            ["sim"] = new JsonObject { ["connected"] = dmc.Sim.Connected, ["fps"] = dmc.Sim.SimFps is { } f ? Math.Round(f, 1) : null, ["in_cockpit"] = dmc.Camera.InCockpit },
            ["learn"] = Learn(dmc),
            ["strays"] = new JsonArray([.. strays.Select(w => (JsonNode)new JsonObject
            {
                ["hwnd"] = w.Handle, ["title"] = w.Title, ["size"] = new JsonArray(w.Client.Width, w.Client.Height), ["position"] = new JsonArray(w.Window.Left, w.Window.Top),
            })]),
            ["popout"] = new JsonObject
            {
                ["status"] = popout.Status, ["detail"] = popout.Detail, ["missing"] = new JsonArray([.. popout.Missing.Select(n => (JsonNode)n)]),
                ["last_attempt"] = popout.LastAttempt?.ToString("HH:mm:ss"),
            },
            ["advice"] = new JsonArray([.. advice.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id, ["level"] = a.Level, ["tab"] = a.Tab, ["title"] = a.Title, ["detail"] = a.Detail, ["steps"] = new JsonArray([.. a.Steps.Select(s => (JsonNode)s)]),
            })]),
            ["source_fps"] = new JsonObject(dmc.Advisor.Rates.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Math.Round(kv.Value, 1)))),
        };
    }

    private static bool InUse(DmcRuntime dmc, DisplayEntry e) => dmc.Dus.IsShown(e.Name) || e.Slot.Clients > 0;

    private static JsonObject Displays(DmcRuntime dmc)
    {
        var result = new JsonObject();
        foreach (var e in dmc.Displays.All)
        {
            var c = e.Capture.Counters;
            var window = e.Capture.HasWindow ? e.Capture.Window : null;
            result[e.Name] = new JsonObject
            {
                ["seq"] = e.Slot.Latest?.Seq ?? 0,
                ["size"] = new JsonArray(e.Slot.Width, e.Slot.Height),
                ["fps"] = e.Slot.Fps(),
                ["last_frame_age_s"] = e.Slot.LastPublished is { } at ? Math.Round((DateTime.UtcNow - at).TotalSeconds, 2) : null,
                ["jpeg_bytes"] = c.JpegBytes,
                ["backend"] = "wgc",
                ["error"] = window is null ? e.Capture.Error : "",
                ["clients"] = e.Slot.Clients,
                ["du_assigned"] = dmc.Dus.Status().Count(d => d.Alive && d.Display == e.Name),
                ["capture_fps"] = e.Capture.CurrentFps,
                ["in_use"] = InUse(dmc, e),
                ["encode_ms"] = Math.Round(c.EncodeMs, 2),
                ["counters"] = new JsonObject { ["received"] = c.Received, ["throttled"] = c.Skipped, ["unchanged"] = c.Unchanged, ["published"] = c.Published },
                ["window"] = new JsonObject { ["hwnd"] = window is null ? null : $"0x{window.Handle:x}", ["title"] = window?.Title },
            };
        }

        return result;
    }

    private static JsonObject Modules(DmcRuntime dmc)
    {
        var result = new JsonObject();
        foreach (var d in dmc.Dus.Status())
        {
            var conn = dmc.Dus.Connection(d.Serial);
            if (!d.Alive && d.Display.Length == 0 && d.Label.Length == 0)
            {
                continue;                                    // an unconfigured DU simply disappears when unplugged: no ghosts
            }

            result[d.Serial] = new JsonObject
            {
                ["serial"] = d.Serial,
                ["description"] = conn?.Description ?? "",
                ["display"] = d.Display.Length > 0 ? d.Display : null,
                ["label"] = d.Label,
                ["alive"] = d.Alive,
                ["error"] = d.Alive ? "" : d.Error.Length > 0 ? d.Error : "not connected",
                ["info"] = conn?.InfoJson is { } info ? JsonNode.Parse(info.GetRawText()) : null,
                ["stats"] = conn?.StatsJson is { } stats ? JsonNode.Parse(stats.GetRawText()) : null,
                ["frames_sent"] = d.FramesSent,
                ["ident_active"] = d.Stats?.Ident ?? false,
                ["ping_ms"] = d.PingMs is { } ping ? Math.Round(ping, 1) : null,
                ["ota"] = new JsonObject { ["state"] = d.Ota.State.ToString().ToLowerInvariant(), ["progress"] = Math.Round(d.Ota.Progress, 3), ["message"] = d.Ota.Message },
                ["fw_outdated"] = Firmware.IsOutdated(d.Info?.Firmware, dmc.FirmwareVersion),
                ["brightness"] = new JsonObject { ["sent"] = d.BrightnessSent, ["sim"] = d.BrightnessSim is { } s ? Math.Round(s, 3) : null, ["source"] = d.BrightnessSim is null ? "manual" : "sim" },
                ["connected_s"] = conn is null ? null : Math.Round((DateTime.UtcNow - conn.ConnectedAt).TotalSeconds, 1),
                ["last_msg_age_s"] = conn is null ? null : Math.Round((DateTime.UtcNow - conn.LastMessageAt).TotalSeconds, 1),
                ["health"] = new JsonObject { ["bad"] = d.Health.Count > 0, ["reasons"] = new JsonArray([.. d.Health.Select(h => (JsonNode)h)]) },
                ["settings"] = new JsonObject { ["display"] = d.Display, ["label"] = d.Label, ["brightness"] = d.Trim },
                ["log_tail"] = new JsonArray(),
            };
        }

        return result;
    }

    private static JsonObject Learn(DmcRuntime dmc)
    {
        var s = dmc.Learner.State;
        return new JsonObject { ["status"] = s.Status, ["display"] = s.Display, ["detail"] = s.Detail, ["point"] = s.Point is { } p ? new JsonArray(p[0], p[1]) : null };
    }

    private static (JsonObject Value, long At) _firmware = (new JsonObject(), long.MinValue / 2);

    private static JsonObject FirmwareSummary(DmcRuntime dmc)
    {
        lock (typeof(Api))
        {
            if (Environment.TickCount64 - _firmware.At > 5000)
            {
                JsonObject summary;
                try
                {
                    var image = Firmware.Load(dmc.FirmwareImagePath);
                    summary = new JsonObject { ["available"] = true, ["version"] = image.Version, ["size"] = image.Size, ["modified"] = image.Modified.ToString("yyyy-MM-dd HH:mm"), ["path"] = image.Path };
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    summary = new JsonObject { ["available"] = false, ["error"] = ex.Message };
                }

                _firmware = (summary, Environment.TickCount64);
            }

            return (JsonObject)_firmware.Value.DeepClone();
        }
    }

    // -- viewers ---------------------------------------------------------------------------------------------
    /// <summary>The viewer protocol: the client asks for the next frame with "n" and gets the newest one as soon as it
    /// is newer than what it has (8 bytes: sequence number and server milliseconds, then the JPEG).</summary>
    private static async Task Viewer(DmcRuntime dmc, string name, HttpContext http)
    {
        if (dmc.Displays.Slot(name) is not { } slot || !http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = dmc.Displays.Slot(name) is null ? 404 : 400;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        slot.AddClient();
        try
        {
            var hello = new JsonObject { ["type"] = "hello", ["name"] = name, ["size"] = new JsonArray(slot.Width, slot.Height), ["version"] = dmc.Version };
            await socket.SendAsync(Encoding.UTF8.GetBytes(hello.ToJsonString()), WebSocketMessageType.Text, true, http.RequestAborted);
            var buffer = new byte[64];
            uint last = 0;
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, http.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (received.MessageType != WebSocketMessageType.Text || received.Count != 1 || buffer[0] != (byte)'n')
                {
                    continue;
                }

                Frame? frame = null;
                while (frame is null && socket.State == WebSocketState.Open && !http.RequestAborted.IsCancellationRequested)
                {
                    var after = last;
                    frame = await Task.Run(() => slot.WaitNewer(after, 1000));
                }

                if (frame is null)
                {
                    break;
                }

                var message = new byte[8 + frame.Jpeg.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(message, frame.Seq);
                BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)(Environment.TickCount64 & 0xFFFFFFFF));
                frame.Jpeg.CopyTo(message.AsMemory(8));
                await socket.SendAsync(message, WebSocketMessageType.Binary, true, http.RequestAborted);
                last = frame.Seq;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
        finally
        {
            slot.RemoveClient();
        }
    }

    private static async Task Mjpeg(DmcRuntime dmc, string name, HttpContext http)
    {
        if (dmc.Displays.Slot(name) is not { } slot)
        {
            http.Response.StatusCode = 404;
            return;
        }

        const string boundary = "glasslinkframe";
        http.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        slot.AddClient();
        try
        {
            uint last = 0;
            while (!http.RequestAborted.IsCancellationRequested)
            {
                var after = last;
                if (await Task.Run(() => slot.WaitNewer(after, 1000)) is not { } frame)
                {
                    continue;
                }

                await http.Response.Body.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Jpeg.Length}\r\n\r\n"), http.RequestAborted);
                await http.Response.Body.WriteAsync(frame.Jpeg, http.RequestAborted);
                await http.Response.Body.WriteAsync("\r\n"u8.ToArray(), http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                last = frame.Seq;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
        finally
        {
            slot.RemoveClient();
        }
    }

    // -- helpers ---------------------------------------------------------------------------------------------
    /// <summary>Errors are plain text: the status page shows them to the user as they are.</summary>
    private static IResult Plain(int status, string message) => Results.Text(message, "text/plain; charset=utf-8", Encoding.UTF8, status);

    private static IResult Json(JsonNode node) => Results.Text(node.ToJsonString(), "application/json");

    private static string? Str(JsonNode? node) => node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static async Task<JsonObject> Body(HttpRequest request)
    {
        if (request.ContentLength is null or 0)
        {
            return [];
        }

        try
        {
            return await JsonNode.ParseAsync(request.Body) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DisplayException ex)
        {
            return Plain(400, ex.Message);
        }
    }

    private static IResult GuardedSync(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (DisplayException ex)
        {
            return Plain(400, ex.Message);
        }
    }
}
