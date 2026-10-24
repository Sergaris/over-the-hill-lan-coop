using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OthLan;

/// <summary>
/// HTTP check, download, sha256 verify and Setup.exe launch. <see cref="Updater"/> owns state and the prompt.
/// Rules and parsing stay in <see cref="UpdateLogic"/>.
/// </summary>
internal static class UpdateJob
{
    const long MaxZipBytes = 400L * 1024 * 1024;
    const long MaxSumsBytes = 65536;
    const int MaxJsonChars = 1_000_000;

#if SELFTEST
    static string _testApi, _testPrefix;
    static bool TestLoopback => _testApi != null;
    static string TestPrefix => _testPrefix;

    /// <summary>Self-test build: release JSON URL instead of the GitHub API. Captured once at init.</summary>
    public static void SetTestEndpoint(string api)
    {
        _testApi = api;
        _testPrefix = null;
        if (Uri.TryCreate(_testApi, UriKind.Absolute, out var u)) _testPrefix = $"{u.Scheme}://{u.Authority}/";
    }

    static string Api() => _testApi ?? UpdateLogic.LatestApi;
#else
    const bool TestLoopback = false;
    const string TestPrefix = null;
    static string Api() => UpdateLogic.LatestApi;
#endif

    /// <summary>Background GitHub check. Caller has already moved <see cref="Updater.State"/> to Checking.</summary>
    public static void Check(double delaySeconds)
    {
        new Thread(() => CheckJob(delaySeconds).GetAwaiter().GetResult()) { IsBackground = true, Name = "othlan-update" }.Start();
    }

    /// <summary>Background download, verify, unpack, start Setup.exe. Caller has already moved state to Downloading.</summary>
    public static void Install(UpdateLogic.Release rel)
    {
        new Thread(() => InstallJob(rel).GetAwaiter().GetResult()) { IsBackground = true, Name = "othlan-update-dl" }.Start();
    }

    static async Task CheckJob(double delaySeconds)
    {
        try { if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds)); await Check(); }
        catch (Exception e) { Fail(Updater.St.Offline, e is TaskCanceledException ? "timeout" : e.GetBaseException().Message); }
    }

    static void Fail(Updater.St st, string why)
    {
        Updater.State = st; Updater.Detail = why ?? "";
        if (st == Updater.St.Failed) Updater.Fails++;
        Plugin.L.LogInfo($"[Update] {st}: {Updater.Detail}");
    }

    static HttpClient Client(TimeSpan timeout)
    {
        var h = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        var c = new HttpClient(h) { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"OTH-LAN/{Plugin.Version} (+https://github.com/{UpdateLogic.RepoPath})");
        return c;
    }

    /// <summary>GET with redirects followed by hand: every hop must pass the allow-list.</summary>
    static async Task<HttpResponseMessage> Get(HttpClient c, string url, Action<HttpRequestMessage> setup = null, CancellationToken ct = default)
    {
        for (int hop = 0; hop < 6; hop++)
        {
            if (!UpdateLogic.AllowedUrl(url, TestLoopback)) throw new InvalidOperationException($"URL not allowed: {UpdateLogic.Trunc(url, 120)}");
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            setup?.Invoke(req);
            var r = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)r.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && r.Headers.Location != null)
            {
                url = new Uri(new Uri(url), r.Headers.Location).ToString(); r.Dispose(); continue;
            }
            return r;
        }
        throw new InvalidOperationException("too many redirects");
    }

    static string CachePath => Path.Combine(BepInEx.Paths.CachePath, "othlan-update.json");

    static async Task Check()
    {
        var api = Api();
        string etag = null, cached = null;
        try { if (!TestLoopback && File.Exists(CachePath)) { var t = File.ReadAllText(CachePath); var i = t.IndexOf('\n'); if (i > 0) { etag = t[..i]; cached = t[(i + 1)..]; } } } catch { }
        using var c = Client(TimeSpan.FromSeconds(5));
        using var r = await Get(c, api, q =>
        {
            q.Headers.Accept.ParseAdd("application/vnd.github+json");
            q.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (etag != null) q.Headers.TryAddWithoutValidation("If-None-Match", etag);
        });
        string json;
        if (r.StatusCode == HttpStatusCode.NotModified && cached != null) json = cached;
        else if (!r.IsSuccessStatusCode) { Fail(Updater.St.Offline, $"HTTP {(int)r.StatusCode}"); return; }
        else
        {
            json = await r.Content.ReadAsStringAsync();
            if (json.Length > MaxJsonChars) { Fail(Updater.St.Offline, "answer too large"); return; }
            try { if (!TestLoopback && r.Headers.ETag != null) File.WriteAllText(CachePath, r.Headers.ETag.ToString() + "\n" + json); } catch { }
        }
        var rel = UpdateLogic.ParseRelease(json, out var why, TestPrefix);
        if (rel == null) { Fail(Updater.St.UpToDate, "no usable release: " + why); return; }
        Updater.Latest = rel;
        if (!UpdateLogic.IsNewer(rel.Version, Plugin.Version)) { Updater.State = Updater.St.UpToDate; Updater.Detail = rel.Version; Plugin.L.LogInfo($"[Update] up to date ({Plugin.Version}; latest {rel.Version})"); return; }
        Updater.State = Updater.St.Available; Updater.Detail = rel.Version;
        var skipped = UpdateLogic.ShouldOffer(rel.Version, Plugin.Version, Plugin.UpdateSkipped.Value) ? "" : " (skipped by the user: no prompt)";
        Plugin.L.LogInfo($"[Update] OTH-LAN {rel.Version} available (running {Plugin.Version}){skipped}: {rel.ZipName} {rel.ZipSize} B");
    }

    static async Task InstallJob(UpdateLogic.Release rel)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "OTH-LAN-update", rel.Version);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            using var c = Client(TimeSpan.FromMinutes(20));
            var sums = await FetchSums(c, rel);
            if (!sums.TryGetValue(rel.ZipName, out var want))
                throw new InvalidOperationException($"{rel.ZipName} not listed in {UpdateLogic.SumsName}");
            var zip = Path.Combine(dir, rel.ZipName);
            var got = await DownloadZip(c, rel, zip, want);
            Plugin.L.LogInfo($"[Update] {rel.ZipName} sha256 OK ({got})");
            var outDir = Path.Combine(dir, "x");
            Unpack(zip, outDir);
            var setup = Path.Combine(outDir, $"OTH-LAN-{rel.Version}", "OTH-LAN-Setup.exe");
            VerifySetup(setup, sums);
            StartSetup(setup, rel);
            Updater.Started++;
            Updater.State = Updater.St.Ready; Updater.Progress = 1;
        }
        catch (Exception e) { Fail(Updater.St.Failed, e.GetBaseException().Message); }
    }

    static async Task<Dictionary<string, string>> FetchSums(HttpClient c, UpdateLogic.Release rel)
    {
        using var r = await Get(c, rel.SumsUrl);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{UpdateLogic.SumsName}: HTTP {(int)r.StatusCode}");
        if (r.Content.Headers.ContentLength > MaxSumsBytes) throw new InvalidOperationException($"{UpdateLogic.SumsName} too large");
        return UpdateLogic.ParseSums(await r.Content.ReadAsStringAsync());
    }

    static async Task<string> DownloadZip(HttpClient c, UpdateLogic.Release rel, string zip, string want)
    {
        string got;
        using (var r = await Get(c, rel.ZipUrl))
        {
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{rel.ZipName}: HTTP {(int)r.StatusCode}");
            var total = r.Content.Headers.ContentLength ?? rel.ZipSize;
            if (total > MaxZipBytes) throw new InvalidOperationException("zip too large");
            using var src = await r.Content.ReadAsStreamAsync();
            using var dst = File.Create(zip);
            using var sha = SHA256.Create();
            var buf = new byte[1 << 16]; long n = 0; int k;
            while ((k = await src.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, k); sha.TransformBlock(buf, 0, k, null, 0); n += k;
                if (n > MaxZipBytes) throw new InvalidOperationException("zip too large");
                if (total > 0) Updater.Progress = (float)Math.Min(1.0, (double)n / total);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            got = Convert.ToHexString(sha.Hash).ToLowerInvariant();
        }
        if (UpdateLogic.HashEquals(got, want)) return got;
        try { File.Delete(zip); } catch { }
        throw new InvalidOperationException($"sha256 mismatch for {rel.ZipName}: got {UpdateLogic.ShortHash(got)}, release says {UpdateLogic.ShortHash(want)} (nothing installed)");
    }

    static void Unpack(string zip, string outDir)
    {
        var root = Path.GetFullPath(outDir) + Path.DirectorySeparatorChar;
        using var za = ZipFile.OpenRead(zip);
        foreach (var e in za.Entries)
        {
            var p = Path.GetFullPath(Path.Combine(outDir, e.FullName));
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("zip entry outside the folder: " + e.FullName);
            if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(p); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            e.ExtractToFile(p, true);
        }
    }

    static void VerifySetup(string setup, Dictionary<string, string> sums)
    {
        if (!File.Exists(setup)) throw new InvalidOperationException("OTH-LAN-Setup.exe not found in the zip");
        if (!sums.TryGetValue("OTH-LAN-Setup.exe", out var wantExe)) return;
        string gotExe; using (var f = File.OpenRead(setup)) using (var s = SHA256.Create()) gotExe = Convert.ToHexString(s.ComputeHash(f)).ToLowerInvariant();
        if (!UpdateLogic.HashEquals(gotExe, wantExe)) throw new InvalidOperationException("sha256 mismatch for OTH-LAN-Setup.exe (nothing installed)");
    }

    static void StartSetup(string setup, UpdateLogic.Release rel)
    {
        var game = BepInEx.Paths.GameRootPath;
        var pid = Process.GetCurrentProcess().Id;
        var psi = new ProcessStartInfo(setup, $"update --game \"{game.TrimEnd('\\')}\" --wait-pid {pid}")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(setup) };
        using (var p = Process.Start(psi))
        {
            if (p == null) throw new InvalidOperationException("could not start OTH-LAN-Setup.exe");
            Plugin.L.LogInfo($"[Update] updater started (pid {p.Id}): installs {rel.Version} when the game exits");
        }
    }
}
