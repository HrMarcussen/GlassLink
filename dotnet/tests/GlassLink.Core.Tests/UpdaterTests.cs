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

    private const string Downloads = "https://github.com/HrMarcussen/GlassLink/releases/download/v0.7.0/";

    private static string ReleaseJson(string tag = "v0.7.0", string setupUrl = Downloads + "setup.exe") => $$"""
        {"tag_name":"{{tag}}","html_url":"https://github.com/HrMarcussen/GlassLink/releases/tag/{{tag}}","body":"### Added\n- things",
         "published_at":"2026-10-01T10:00:00Z","draft":false,"prerelease":false,
         "assets":[{"name":"GlassLink-0.7.0-setup.exe","browser_download_url":"{{setupUrl}}","size":{{Setup.Length}}},
                   {"name":"GlassLink-0.7.0-win-x64.zip","browser_download_url":"{{Downloads}}app.zip","size":"5"},
                   {"name":"SHA256SUMS.txt","browser_download_url":"{{Downloads}}sums","size":100},
                   {"name":"evil.exe","browser_download_url":"http://example.test/evil.exe","size":1}]}
        """;

    private static Updater Make(string current, string? sums = null, bool installed = true, Func<string, string, bool>? launch = null,
        Func<string?>? signer = null, Func<HttpRequestMessage, HttpResponseMessage>? answer = null) =>
        new(current, Config(), _ => { }, new FakeGitHub(answer ?? (r => Answer(r, sums ?? GoodSums()))), AppDir(installed), launch, signer ?? (() => null),
            Directory.CreateTempSubdirectory("glasslink-update-download").FullName);

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
        var r = Release.From(JsonNode.Parse(ReleaseJson()), Downloads[..^7])!;
        Assert.Equal(("0.7.0", "v0.7.0", "GlassLink-0.7.0-setup.exe", "SHA256SUMS.txt"), (r.Version, r.Tag, r.Installer!.Name, r.Sums!.Name));
        Assert.Equal("2026-10-01", r.Published!.Value.ToString("yyyy-MM-dd"));
        Assert.Null(Release.From(JsonNode.Parse("""{"message":"Not Found"}""")));
        Assert.Null(Release.From(JsonNode.Parse("""[{"tag_name":"v9.9.9"}]""")));                 // an odd answer is no release, not an exception

        // a setup program over plain http, from another site, or with a path in its name is never a candidate
        foreach (var url in new[] { "http://github.com/HrMarcussen/GlassLink/releases/download/v0.7.0/setup.exe", "https://example.test/setup.exe" })
        {
            Assert.Null(Release.From(JsonNode.Parse(ReleaseJson(setupUrl: url)), "https://github.com/HrMarcussen/GlassLink/releases/download/")!.Installer);
        }

        var pathName = JsonNode.Parse(ReleaseJson())!;
        pathName["assets"]![0]!["name"] = @"..\..\GlassLink-0.7.0-setup.exe";
        Assert.Null(Release.From(pathName)!.Installer);
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
        var launched = new List<(string File, string Args, bool Writable)>();
        using var good = Make("0.6.2", launch: (file, args) =>
        {
            bool writable;
            try
            {
                using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    writable = true;
                }
            }
            catch (IOException)
            {
                writable = false;
            }

            launched.Add((file, args, writable));
            return true;
        });
        await good.CheckAsync();
        Assert.True(good.BeginInstall());
        Until(() => launched.Count == 1);
        Assert.EndsWith("GlassLink-0.7.0-setup.exe", launched[0].File);
        Assert.Equal(Setup, File.ReadAllBytes(launched[0].File));
        Assert.Contains("/update=1", launched[0].Args);
        Assert.Contains("/SILENT", launched[0].Args);
        Assert.DoesNotContain("/withsim", launched[0].Args);        // this test run was not started by the sim
        Assert.False(launched[0].Writable);                          // checked and started without a moment to swap it

        var tampered = GoodSums().Replace(GoodSums()[..8], "deadbeef");
        var never = new List<string>();
        using var bad = Make("0.6.2", tampered, launch: (file, _) => { never.Add(file); return true; });
        await bad.CheckAsync();
        Assert.True(bad.BeginInstall());
        Until(() => bad.State == "idle" && bad.Error.Length > 0, () => $"state {bad.State} error '{bad.Error}' progress {bad.Progress}");
        Assert.Contains("does not match", bad.Error);
        Assert.Empty(never);

        // a signed GlassLink refuses an update that is not signed by the same publisher (the fake download is unsigned)
        using var signed = Make("0.6.2", launch: (file, _) => { never.Add(file); return true; }, signer: () => "CN=GlassLink test publisher");
        await signed.CheckAsync();
        Assert.True(signed.BeginInstall());
        Until(() => signed.State == "idle" && signed.Error.Length > 0, () => $"state {signed.State} error '{signed.Error}'");
        Assert.Contains("not validly signed", signed.Error);
        Assert.Empty(never);
    }

    [Fact]
    public async Task A_dmc_the_sim_started_is_started_again_with_the_sim_after_an_update()
    {
        var args = "";
        var updater = new Updater("0.6.2", Config(), _ => { }, new FakeGitHub(r => Answer(r, GoodSums())), AppDir(true),
            (_, a) => { args = a; return true; }, () => null, Directory.CreateTempSubdirectory("glasslink-update-download").FullName)
        { StartedWithSim = true };
        using var _u = updater;
        await updater.CheckAsync();
        Assert.True(updater.BeginInstall());
        Until(() => args.Length > 0);
        Assert.Contains("/withsim=1", args);                         // the setup program starts it with --with-sim (review S11e)
    }

    [Fact]
    public async Task A_setup_program_that_never_finishes_does_not_leave_the_update_stuck_at_starting()
    {
        var started = 0;
        var updater = new Updater("0.6.2", Config(), _ => { }, new FakeGitHub(r => Answer(r, GoodSums())), AppDir(true),
            (_, _) => { Interlocked.Increment(ref started); return true; }, () => null, Directory.CreateTempSubdirectory("glasslink-update-download").FullName)
        { StartTimeout = TimeSpan.FromMilliseconds(300) };
        using var _u = updater;
        await updater.CheckAsync();
        Assert.True(updater.BeginInstall());
        Until(() => started == 1);
        Until(() => updater.State == "idle", () => $"state {updater.State}");
        Assert.Contains("did not finish", updater.Error);
        Assert.True(updater.CanInstall);                             // Install can be pressed again
    }

    [Fact]
    public async Task A_check_never_throws_on_an_odd_answer_from_github()
    {
        using var odd = Make("0.6.2", answer: _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[1,2,3]""") });
        await odd.CheckAsync();
        Assert.Equal("idle", odd.State);
        Assert.Contains("not a release", odd.Error);
        Assert.False(odd.Available);
    }
}
