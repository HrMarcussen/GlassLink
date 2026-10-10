using System.Net;
using GlassLink.Core.Du;
using GlassLink.Dmc;
using Microsoft.AspNetCore.Http;

namespace GlassLink.Core.Tests;

public class RequestGuardTests
{
    private static readonly IReadOnlySet<string> Names = new HashSet<string> { "localhost", "127.0.0.1", "::1", "simpc", "192.168.1.10" };

    private static string? Check(string method, string host, string? contentType = null, string? origin = null, string remote = "127.0.0.1", bool lan = false,
        bool socket = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Host = new HostString(host);
        if (socket)
        {
            (http.Request.Headers.Connection, http.Request.Headers.Upgrade) = ("Upgrade", "websocket");
        }

        if (contentType is not null)
        {
            http.Request.ContentType = contentType;
        }

        if (origin is not null)
        {
            http.Request.Headers.Origin = origin;
        }

        return RequestGuard.Check(http.Request, IPAddress.Parse(remote), Names, lan);
    }

    [Fact]
    public void The_status_page_on_this_pc_may_change_things()
    {
        Assert.Null(Check("GET", "localhost:8765"));
        Assert.Null(Check("POST", "localhost:8765", "application/json", "http://localhost:8765"));
        Assert.Null(Check("POST", "127.0.0.1:8765", "application/json; charset=utf-8"));
        Assert.Null(Check("DELETE", "localhost:8765"));
        Assert.Null(Check("POST", "[::1]:8765", "application/json", remote: "::1"));
    }

    [Fact]
    public void A_foreign_page_cannot_forge_a_request()
    {
        Assert.NotNull(Check("POST", "localhost:8765", "text/plain"));                                   // a no-cors fetch
        Assert.NotNull(Check("POST", "localhost:8765"));                                                  // a form without a body type
        Assert.NotNull(Check("POST", "localhost:8765", "application/json", "https://evil.example"));      // after a preflight that would fail anyway
        Assert.NotNull(Check("GET", "evil.example:8765"));                                                // DNS rebinding
        Assert.NotNull(Check("GET", "localhost:8765", origin: "https://evil.example"));                   // the viewer socket from another site
        Assert.NotNull(Check("GET", "localhost:8765", origin: "null", socket: true));                     // a sandboxed frame on any site
        Assert.NotNull(Check("POST", "localhost:8765", "application/json", "null"));
        Assert.Null(Check("GET", "localhost:8765", origin: "null"));                                      // reading: the browser keeps the answer from it
        Assert.Null(Check("GET", "localhost:8765", origin: "http://localhost:8765", socket: true));       // the page's own viewer
    }

    [Fact]
    public async Task Refusals_are_logged_once_per_source_and_kind_bounded_and_without_forged_lines()
    {
        var lines = new List<string>();
        var guard = RequestGuard.Middleware(() => false, lines.Add)(_ => Task.CompletedTask);
        async Task Send(string host, string path, string remote)
        {
            var http = new DefaultHttpContext();
            (http.Request.Method, http.Request.Host, http.Request.Path) = ("GET", new HostString(host), path);
            http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            http.Response.Body = new MemoryStream();
            await guard(http);
            Assert.Equal(403, http.Response.StatusCode);
        }

        for (var i = 0; i < 50; i++)
        {
            await Send($"evil{i}.example", $"/random/{i}", "192.168.1.20");             // one scanner, many paths and names
        }

        Assert.Single(lines);
        for (var i = 0; i < 300; i++)
        {
            await Send("evil.example", "/x", $"10.0.{i / 250}.{i % 250 + 1}");           // many sources
        }

        Assert.Equal(RequestGuard.MaxLogged + 1, lines.Count);          // capped, with one line saying so
        Assert.DoesNotContain(lines, l => l.Contains('\n') || l.Contains('\r'));
        Assert.Equal("a?b", RequestGuard.Printable("a\nb"));
    }

    [Fact]
    public void Other_devices_may_read_and_change_only_when_allowed()
    {
        Assert.Null(Check("GET", "192.168.1.10:8765", remote: "192.168.1.20"));                          // the phone reads
        Assert.Null(Check("GET", "simpc:8765", remote: "192.168.1.20"));
        Assert.NotNull(Check("POST", "192.168.1.10:8765", "application/json", "http://192.168.1.10:8765", "192.168.1.20"));
        Assert.Null(Check("POST", "192.168.1.10:8765", "application/json", "http://192.168.1.10:8765", "192.168.1.10"));   // this PC through its LAN address
        Assert.Null(Check("POST", "192.168.1.10:8765", "application/json", "http://192.168.1.10:8765", "192.168.1.20", lan: true));
    }

    [Fact]
    public void This_pc_answers_to_its_name_in_the_lan_dns_too()
    {
        var names = RequestGuard.LocalNames();
        var machine = Environment.MachineName.ToLowerInvariant();
        Assert.Contains(machine, names);
        Assert.Contains(machine + ".local", names);
        foreach (var suffix in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                     .Select(n => n.GetIPProperties().DnsSuffix).Where(s => s.Length > 0))
        {
            Assert.Contains($"{machine}.{suffix}".ToLowerInvariant(), names);                     // simpc.lan, simpc.fritz.box
        }

        Assert.DoesNotContain("evil.example", names);
    }

    [Fact]
    public void The_diagnostics_go_to_this_pc_only()
    {
        Assert.True(RequestGuard.FromThisPc(IPAddress.Loopback));
        Assert.True(RequestGuard.FromThisPc(IPAddress.IPv6Loopback));
        Assert.False(RequestGuard.FromThisPc(IPAddress.Parse("192.0.2.77")));      // a phone on the LAN: the logs stay here
        Assert.False(RequestGuard.FromThisPc(null));
    }

    [Fact]
    public void Changes_must_be_addressed_to_localhost_or_an_address_as_a_name_can_be_answered_by_another_device()
    {
        // a page served under the PC's name by someone else on the network, which then points the name at this PC
        Assert.NotNull(Check("POST", "simpc:8765", "application/json", "http://simpc:8765", "192.168.1.10"));
        Assert.Null(Check("POST", "192.168.1.10:8765", "application/json", "http://192.168.1.10:8765", "192.168.1.10"));
        Assert.Null(Check("POST", "localhost:8765", "application/json", "http://localhost:8765"));
        Assert.Null(Check("GET", "simpc:8765", remote: "192.168.1.20"));                                 // reading by name stays open
        Assert.Null(Check("POST", "simpc:8765", "application/json", "http://simpc:8765", "192.168.1.20", lan: true));   // allowed on purpose
    }

    [Fact]
    public async Task The_status_page_is_never_shown_inside_another_sites_frame()
    {
        async Task<IHeaderDictionary> Headers(string path)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = path;
            await RequestGuard.SecurityHeaders()(_ => Task.CompletedTask)(http);
            return http.Response.Headers;
        }

        var page = await Headers("/");
        Assert.Equal("DENY", page.XFrameOptions.ToString());
        Assert.Contains("frame-ancestors 'none'", page.ContentSecurityPolicy.ToString());
        Assert.Equal("nosniff", page.XContentTypeOptions.ToString());
        var viewer = await Headers("/view/pfd");                     // a viewer may be part of someone's own dashboard
        Assert.Empty(viewer.XFrameOptions.ToString());
        Assert.Equal("nosniff", viewer.XContentTypeOptions.ToString());
    }

    [Fact]
    public void A_du_set_up_only_with_tiles_stays_listed_when_unplugged()
    {
        static DuStatus Du(string display, string label, IReadOnlyList<(string, int, int)> tiles) =>
            new("aabbccdd", label, display, 100, false, "not connected", null, null, 0, null, null, null, new OtaStatus(OtaState.Idle, 0, ""), [], null, tiles, [], false, "");
        Assert.True(Api.Listed(Du("", "", [("pfd", 0, 0), ("nd", 1024, 0)])));                         // review T3
        Assert.True(Api.Listed(Du("pfd", "", [])));
        Assert.True(Api.Listed(Du("", "DU1", [])));
        Assert.False(Api.Listed(Du("", "", [])));                                                      // a stranger simply goes
    }
}

public class ViewerTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1", "iPhone")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/140.0.7339.101 Mobile/15E148 Safari/604.1", "iPhone")]   // Chrome on iOS
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/140.0 Mobile Safari/537.36", "Android phone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 Chrome/140.0 Safari/537.36", "Android tablet")]
    [InlineData("Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 Chrome/138.0 Safari/537.36", "Raspberry Pi")]
    [InlineData("Python/3.11 websockets/13.1", "Pi viewer")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0 Safari/537.36 Edg/140.0", "Windows PC")]
    [InlineData("VLC/3.0.21 LibVLC/3.0.21", "VLC")]
    [InlineData("", "a device")]
    public void A_viewer_is_named_by_its_user_agent(string userAgent, string name) =>
        Assert.Equal(name, ViewerNames.Describe(userAgent, local: false));

    [Fact]
    public void A_viewer_on_the_sim_pc_is_this_pc_and_leaves_when_done()
    {
        Assert.Equal("this PC", ViewerNames.Describe("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", local: true));
        var slot = new GlassLink.Core.Du.FrameSlot("pfd");
        var phone = slot.AddViewer("iPhone");
        using (slot.AddViewer("this PC"))
        {
            Assert.Equal(["iPhone", "this PC"], slot.Viewers.Order());
            Assert.Equal(2, slot.Clients);
        }

        phone.Dispose();
        Assert.Empty(slot.Viewers);
        Assert.Equal(0, slot.Clients);
    }
}
