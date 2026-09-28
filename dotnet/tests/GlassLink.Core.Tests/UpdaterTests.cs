using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GlassLink.Core.Config;
using GlassLink.Dmc;

namespace GlassLink.Core.Tests;

public class UpdaterTests
{
    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static readonly byte[] Setup = Encoding.ASCII.GetBytes("pretend this is GlassLink-0.7.0-setup.exe");

    private static string ReleaseJson(string tag = "v0.7.0") => $$"""
        {"tag_name":"{{tag}}","html_url":"https://github.com/HrMarcussen/GlassLink/releases/tag/{{tag}}","body":"### Added\n- things",
         "published_at":"2026-10-01T10:00:00Z","draft":false,"prerelease":false,
         "assets":[{"name":"GlassLink-0.7.0-setup.exe","browser_download_url":"https://example.test/setup.exe","size":{{Setup.Length}}},
                   {"name":"GlassLink-0.7.0-win-x64.zip","browser_download_url":"https://example.test/app.zip","size":5},
                   {"name":"SHA256SUMS.txt","browser_download_url":"https://example.test/sums","size":100},
                   {"name":"evil.exe","browser_download_url":"http://example.test/evil.exe","size":1}]}
        """;

    private static HttpResponseMessage Answer(HttpRequestMessage r, string sums)
    {
        var url = r.RequestUri!.ToString();
        return url.Contains("/releases/latest") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()), Headers = { ETag = new("\"abc\"") } }
            : url.EndsWith("/sums") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) }
            : url.EndsWith("/setup.exe") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string GoodSums() => $"{Convert.ToHexString(SHA256.HashData(Setup)).ToLowerInvariant()}  GlassLink-0.7.0-setup.exe\n0000000000000000000000000000000000000000000000000000000000000000  GlassLink-0.7.0-win-x64.zip\n";

    private static ConfigFile Config() => new((JsonObject)JsonNode.Parse("{}")!);

    private static string AppDir(bool installed)
    {
        var dir = Directory.CreateTempSubdirectory("glasslink-update-test").FullName;
        if (installed)
        {
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "");
        }

        return dir;
    }

    private static void Until(Func<bool> condition, Func<string>? what = null)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), "timed out " + what?.Invoke());
    }

    [Fact]
    public void A_release_is_read_with_its_setup_program_and_checksums_and_nothing_over_plain_http()
    {
        var r = Release.From(JsonNode.Parse(ReleaseJson()))!;
        Assert.Equal(("0.7.0", "v0.7.0", "GlassLink-0.7.0-setup.exe", "SHA256SUMS.txt"), (r.Version, r.Tag, r.Installer!.Name, r.Sums!.Name));
        Assert.Equal("2026-10-01", r.Published!.Value.ToString("yyyy-MM-dd"));
        Assert.Null(Release.From(JsonNode.Parse("""{"message":"Not Found"}""")));
        var sums = Updater.ParseSums(GoodSums() + "garbage line\n" + new string('a', 64) + " *star.bin\r\n");
        Assert.Equal(3, sums.Count);
        Assert.True(sums.ContainsKey("star.bin"));                  // sha256sum's binary-mode marker
    }

    [Fact]
    public async Task A_newer_release_is_offered_the_same_one_is_not_and_problems_are_said_in_words()
    {
        var github = new FakeGitHub(r => Answer(r, GoodSums()));
        using var newer = new Updater("0.6.2", Config(), _ => { }, github, AppDir(true));
        await newer.CheckAsync();
        Assert.True(newer.Available && newer.CanInstall);
        Assert.Equal("0.7.0", newer.ToJson()["latest"]!.GetValue<string>());
        Assert.Contains("/repos/HrMarcussen/GlassLink/releases/latest", github.Requests[0].RequestUri!.ToString());
        await newer.CheckAsync();
        Assert.Equal("\"abc\"", github.Requests[1].Headers.IfNoneMatch.Single().ToString());     // asks "changed since?"

        using var same = new Updater("0.7.0", Config(), _ => { }, new FakeGitHub(r => Answer(r, GoodSums())), AppDir(true));
        await same.CheckAsync();
        Assert.False(same.Available);

        using var none = new Updater("0.6.2", Config(), _ => { }, new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), AppDir(true));
        await none.CheckAsync();
        Assert.Contains("no published release", none.Error);

        using var offline = new Updater("0.6.2", Config(), _ => { }, new FakeGitHub(_ => throw new HttpRequestException("down")), AppDir(true));
        await offline.CheckAsync();
        Assert.Equal("no connection to GitHub", offline.Error);
        Assert.Equal("idle", offline.State);
    }

    [Fact]
    public async Task A_checkout_is_not_updated_and_the_check_can_be_switched_off()
    {
        var config = Config();
        using var checkout = new Updater("0.6.2", config, _ => { }, new FakeGitHub(r => Answer(r, GoodSums())), AppDir(false));
        await checkout.CheckAsync();
        Assert.True(checkout.Available);
        Assert.False(checkout.BeginInstall());
        Assert.Contains("source checkout", checkout.Error);
        Assert.True(checkout.Enabled);
        checkout.SetEnabled(false);
        Assert.False(checkout.Enabled);
        Assert.False(((JsonObject)config.Root["updates"]!)["check"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_setup_program_runs_only_when_it_matches_the_release_checksum()
    {
        var launched = new List<(string File, string Args)>();
        using var good = new Updater("0.6.2", Config(), _ => { }, new FakeGitHub(r => Answer(r, GoodSums())), AppDir(true),
            (file, args) => { launched.Add((file, args)); return true; }, () => null);
        await good.CheckAsync();
        Assert.True(good.BeginInstall());
        Until(() => launched.Count == 1);
        Assert.EndsWith("GlassLink-0.7.0-setup.exe", launched[0].File);
        Assert.Equal(Setup, File.ReadAllBytes(launched[0].File));
        Assert.Contains("/update=1", launched[0].Args);
        Assert.Contains("/SILENT", launched[0].Args);

        var tampered = GoodSums().Replace(GoodSums()[..8], "deadbeef");
        var never = new List<string>();
        using var bad = new Updater("0.6.2", Config(), _ => { }, new FakeGitHub(r => Answer(r, tampered)), AppDir(true),
            (file, _) => { never.Add(file); return true; }, () => null);
        await bad.CheckAsync();
        Assert.True(bad.BeginInstall());
        Until(() => bad.State == "idle" && bad.Error.Length > 0, () => $"state {bad.State} error '{bad.Error}' progress {bad.Progress}");
        Assert.Contains("does not match", bad.Error);
        Assert.Empty(never);

        // a signed GlassLink refuses an update that is not signed by the same publisher (the fake download is unsigned)
        using var signed = new Updater("0.6.2", Config(), _ => { }, new FakeGitHub(r => Answer(r, GoodSums())), AppDir(true),
            (file, _) => { never.Add(file); return true; }, () => "CN=GlassLink test publisher");
        await signed.CheckAsync();
        Assert.True(signed.BeginInstall());
        Until(() => signed.State == "idle" && signed.Error.Length > 0, () => $"state {signed.State} error '{signed.Error}'");
        Assert.Contains("not validly signed", signed.Error);
        Assert.Empty(never);
    }
}
