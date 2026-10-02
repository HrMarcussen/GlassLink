using System.Net;
using GlassLink.Dmc;
using Microsoft.AspNetCore.Http;

namespace GlassLink.Core.Tests;

public class RequestGuardTests
{
    private static readonly IReadOnlySet<string> Names = new HashSet<string> { "localhost", "127.0.0.1", "::1", "simpc", "192.168.1.10" };

    private static string? Check(string method, string host, string? contentType = null, string? origin = null, string remote = "127.0.0.1", bool lan = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Host = new HostString(host);
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
