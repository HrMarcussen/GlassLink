using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Http;

namespace GlassLink.Dmc;

/// <summary>
/// Who may do what through the HTTP API. The DMC listens on every interface so a phone can open the status page and
/// the viewer; that must not let a web page or another device change anything:
///   Host     a request must be addressed to this PC (localhost, its name or one of its addresses): a page that
///            re-points its own domain at 127.0.0.1 (DNS rebinding) is refused;
///   Origin   a request from a browser page on another site is refused (the header is absent for tools);
///   JSON     a POST must say Content-Type: application/json, which a foreign page cannot send without a CORS
///            preflight, and the DMC answers no preflight: no cross-site request forgery;
///   LAN      changes (POST, DELETE) come from this PC only, unless server.allow_lan_control is true; reading (the
///            page, /status, pictures, the viewer) stays open to the LAN.
/// </summary>
public static class RequestGuard
{
    private static readonly HashSet<string> Mutating = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public static Func<RequestDelegate, RequestDelegate> Middleware(Func<bool> allowLanControl, Action<string> log)
    {
        var names = LocalNames();
        var refreshed = DateTime.UtcNow;
        var logged = new HashSet<string>();
        return next => async http =>
        {
            if ((DateTime.UtcNow - refreshed).TotalMinutes > 1)
            {
                names = LocalNames();                        // a new DHCP address or network is picked up
                refreshed = DateTime.UtcNow;
            }

            var reason = Check(http.Request, http.Connection.RemoteIpAddress, names, allowLanControl());
            if (reason is null)
            {
                await next(http);
                return;
            }

            lock (logged)
            {
                if (logged.Add(reason + http.Request.Path))  // once per kind and path: a scanner cannot flood the log
                {
                    log($"refused {http.Request.Method} {http.Request.Path} from {http.Connection.RemoteIpAddress}: {reason}");
                }
            }

            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync(reason);
        };
    }

    /// <summary>Null when the request may go on, else the reason it may not (shown to the caller).</summary>
    public static string? Check(HttpRequest request, IPAddress? remote, IReadOnlySet<string> localNames, bool allowLanControl)
    {
        var host = request.Host.Host.Trim('[', ']').ToLowerInvariant();
        if (!localNames.Contains(host))
        {
            return $"this DMC answers only to its own names and addresses, not '{request.Host.Host}'";
        }

        if (request.Headers.Origin is { Count: > 0 } origins && origins.ToString() is { Length: > 0 } origin && origin != "null"
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return "requests from other web sites are refused";
        }

        if (!Mutating.Contains(request.Method))
        {
            return null;
        }

        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && !(request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return "changes must be sent as application/json";
        }

        if (!allowLanControl && !IsThisPc(remote, localNames))
        {
            return "changes are accepted from this PC only (set server.allow_lan_control to true in config.json to allow other devices)";
        }

        return null;
    }

    /// <summary>Loopback, or one of this PC's own addresses (the page opened through the LAN address on the sim PC).</summary>
    private static bool IsThisPc(IPAddress? remote, IReadOnlySet<string> localNames) =>
        remote is not null && (IPAddress.IsLoopback(remote) || localNames.Contains((remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString().ToLowerInvariant()));

    /// <summary>The names and addresses this PC answers to, lower case, IPv6 without brackets.</summary>
    public static IReadOnlySet<string> LocalNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1" };
        var machine = Environment.MachineName.ToLowerInvariant();
        names.Add(machine);
        names.Add(machine + ".local");
        try
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (domain.Length > 0)
            {
                names.Add($"{machine}.{domain}".ToLowerInvariant());
            }

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    var text = a.Address.ToString().ToLowerInvariant();
                    names.Add(text);
                    if (text.IndexOf('%') is var pct and > 0)
                    {
                        names.Add(text[..pct]);              // fe80::1%12 is also typed as fe80::1
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // loopback and the machine name still work
        }

        return names;
    }
}
