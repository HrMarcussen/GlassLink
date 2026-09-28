using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using GlassLink.Core.Config;
using GlassLink.Core.Du;

namespace GlassLink.Dmc;

public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>One GitHub Release as the update check sees it.</summary>
public sealed record Release(string Version, string Tag, string PageUrl, string Notes, DateTimeOffset? Published, ReleaseAsset? Installer, ReleaseAsset? Sums)
{
    /// <summary>From GitHub's release JSON; null when it is not a release (no tag).</summary>
    public static Release? From(JsonNode? json)
    {
        if (json?["tag_name"].Text() is not { Length: > 0 } tag)
        {
            return null;
        }

        var assets = (json["assets"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => new ReleaseAsset(a["name"].Text() ?? "", a["browser_download_url"].Text() ?? "", (long)(a["size"]?.AsDouble() ?? 0)))
            .Where(a => a.Name.Length > 0 && a.Url.StartsWith("https://", StringComparison.Ordinal)).ToList();
        return new Release(tag.TrimStart('v', 'V'), tag, json["html_url"].Text() ?? "", json["body"].Text() ?? "",
            DateTimeOffset.TryParse(json["published_at"].Text(), out var p) ? p : null,
            assets.FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)),
            assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Updates of the DMC from GitHub Releases (#76). A check asks GitHub for the latest published release a minute after the
/// start and every six hours (anonymously; nothing about this PC is sent), unless <c>updates.check</c> is false. It is
/// installed only on the user's click, and only by an installed copy (a checkout is updated with git): the setup program
/// is downloaded, its SHA-256 compared with the release's SHA256SUMS.txt and, if this GlassLink.exe is signed, its
/// signature checked to be valid and from the same publisher; then it runs silently. The installer stops this DMC as
/// for any upgrade, and starts the new one when it is done. New DU firmware comes with the update, and the status page
/// then offers it for the DUs as usual.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string DefaultRepository = "HrMarcussen/GlassLink";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1), Every = TimeSpan.FromHours(6);
    private readonly string _current;
    private readonly ConfigFile _config;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly string _appDir;
    private readonly Func<string, string, bool> _launch;
    private readonly Func<string?> _ownSigner;
    private System.Threading.Timer? _timer;
    private string? _etag;
    private int _busy;                                       // 1 while checking or installing

    public Updater(string currentVersion, ConfigFile config, Action<string> log, HttpMessageHandler? handler = null, string? appDir = null,
        Func<string, string, bool>? launch = null, Func<string?>? ownSigner = null)
    {
        (_current, _config, _log) = (currentVersion, config, log);
        _appDir = appDir ?? AppContext.BaseDirectory;
        _launch = launch ?? LaunchInstaller;
        _ownSigner = ownSigner ?? (() => Authenticode.SignerOf(Environment.ProcessPath ?? ""));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);            // the download of the setup program; the check is small
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GlassLink-DMC", currentVersion));
    }

    public Release? Latest { get; private set; }

    public DateTime? Checked { get; private set; }

    /// <summary>idle, checking, downloading, verifying, starting</summary>
    public string State { get; private set; } = "idle";

    public double Progress { get; private set; }

    /// <summary>The last problem, in words; empty when there is none.</summary>
    public string Error { get; private set; } = "";

    public bool Enabled => _config.Read(root => (root["updates"] as JsonObject)?["check"] is not { } c || c.GetValueKind() != System.Text.Json.JsonValueKind.False);

    public string Repository => _config.Read(root => (root["updates"] as JsonObject)?["repository"].Text()) is { Length: > 0 } r ? r : DefaultRepository;

    /// <summary>Installed by the setup program (it leaves its uninstaller next to GlassLink.exe), not run from a checkout.</summary>
    public bool Installed => File.Exists(Path.Combine(_appDir, "unins000.exe"));

    public bool Available => Latest is { } r && Firmware.IsOutdated(_current, r.Version);

    public bool CanInstall => Available && Installed && Latest?.Installer is not null && Latest.Sums is not null && State == "idle";

    public void Start() => _timer = new System.Threading.Timer(_ => { if (Enabled) { _ = CheckAsync(); } }, null, FirstCheck, Every);

    public void SetEnabled(bool on) => _config.Update(root => ConfigFile.Section(root, "updates")["check"] = on);

    /// <summary>Asks GitHub for the latest release. Never throws: problems end up in <see cref="Error"/>.</summary>
    public async Task CheckAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        State = "checking";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (_etag is not null)
            {
                request.Headers.IfNoneMatch.ParseAdd(_etag);    // unchanged: 304, which does not count against the rate limit
            }

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            Checked = DateTime.UtcNow;
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotModified:
                    Error = "";
                    break;
                case HttpStatusCode.NotFound:
                    (Latest, _etag, Error) = (null, null, "no published release to compare with (yet)");
                    break;
                case HttpStatusCode.OK:
                    var was = Latest?.Version;
                    Latest = Release.From(JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)));
                    _etag = response.Headers.ETag?.ToString();
                    Error = Latest is null ? "GitHub's answer was not a release" : "";
                    if (Available && Latest!.Version != was)
                    {
                        _log($"update: GlassLink {Latest.Version} is available ({Latest.PageUrl})");
                    }

                    break;
                default:
                    Error = $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}";
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Checked = DateTime.UtcNow;
            Error = ex is System.Text.Json.JsonException ? "GitHub's answer could not be read" : "no connection to GitHub";
        }
        finally
        {
            State = "idle";
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Starts the download and installation; false (with the reason in <see cref="Error"/>) when it cannot.</summary>
    public bool BeginInstall()
    {
        if (!CanInstall)
        {
            Error = !Available ? "no newer version" : !Installed ? "this copy runs from a source checkout: update it with git"
                : Latest?.Installer is null || Latest.Sums is null ? "the release has no setup program or no checksums" : "busy";
            return false;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Error = "busy";
            return false;
        }

        _ = Task.Run(InstallAsync);
        return true;
    }

    private async Task InstallAsync()
    {
        var release = Latest!;
        var installer = release.Installer!;
        var folder = Path.Combine(Path.GetTempPath(), "GlassLink-update");
        var file = Path.Combine(folder, installer.Name);
        try
        {
            (State, Progress, Error) = ("downloading", 0, "");
            var sums = ParseSums(await _http.GetStringAsync(release.Sums!.Url).ConfigureAwait(false));
            if (!sums.TryGetValue(installer.Name, out var expected))
            {
                throw new InvalidDataException($"{installer.Name} is not in the release's checksums");
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);                  // earlier downloads
            }

            Directory.CreateDirectory(folder);
            using (var response = await _http.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? installer.Size;
                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var target = File.Create(file);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                    done += n;
                    Progress = total > 0 ? Math.Min(1, (double)done / total) : 0;
                }
            }

            State = "verifying";
            string actual;
            await using (var downloaded = File.OpenRead(file))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded).ConfigureAwait(false));
            }

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("the download does not match the release's checksum");
            }

            if (_ownSigner() is { } ours)
            {
                // a signed GlassLink only installs updates that are validly signed by the same publisher
                if (!Authenticode.IsValid(file) || Authenticode.SignerOf(file) != ours)
                {
                    throw new InvalidDataException("the setup program is not validly signed by the publisher of this GlassLink");
                }
            }

            State = "starting";
            _log($"update: starting the setup program of GlassLink {release.Version}");
            if (!_launch(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /update=1"))
            {
                Error = "the installation was cancelled at the Windows prompt";
                State = "idle";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error = ex is HttpRequestException or TaskCanceledException ? "the download failed: no connection to GitHub" : ex.Message;
            _log($"update: {Error}");
            State = "idle";
            TryDelete(file);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>"hash  name" lines, as sha256sum writes them.</summary>
    public static Dictionary<string, string> ParseSums(string text) =>
        text.Split('\n').Select(l => l.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[0].Length == 64).GroupBy(p => p[1].TrimStart('*'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>The setup program asks for administrator rights; cancelling that prompt is not an error.</summary>
    private static bool LaunchInstaller(string file, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)          // ERROR_CANCELLED: "No" at the UAC prompt
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["repository"] = Repository,
        ["current"] = _current,
        ["latest"] = Latest?.Version,
        ["available"] = Available,
        ["installed"] = Installed,
        ["can_install"] = CanInstall,
        ["url"] = Latest?.PageUrl,
        ["notes"] = Latest?.Notes,
        ["published"] = Latest?.Published?.ToString("yyyy-MM-dd"),
        ["checked"] = Checked?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        ["state"] = State,
        ["progress"] = Math.Round(Progress, 3),
        ["error"] = Error,
    };

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}

/// <summary>Authenticode through Windows itself (WinVerifyTrust), no third-party code.</summary>
public static class Authenticode
{
    /// <summary>The subject of the certificate that signed the file; null when it is not signed.</summary>
    public static string? SignerOf(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057                            // the signer of a signed file, not a certificate file to load
            return File.Exists(file) ? X509Certificate.CreateFromSignedFile(file).Subject : null;
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>True when the file's signature is valid and chains to a trusted root.</summary>
    public static bool IsValid(string file)
    {
        var fileInfo = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = file };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2 /* none */, RevocationChecks = 0, UnionChoice = 1 /* file */,
                File = fileInfoPtr, StateAction = 0, ProviderFlags = 0,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");     // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<FileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
