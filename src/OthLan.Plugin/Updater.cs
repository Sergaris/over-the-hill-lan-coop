using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 in-game update: once per start (background, 5 s timeout) ask GitHub for the latest release of the public repo;
/// if newer, a prompt in the main menu (Install / Later / Skip this version; mouse, keyboard, gamepad). 0.1.9.1: if the
/// check fails (timeout / HTTP / parse), a one-shot Offline notice (Retry / Later) appears in the main menu too.
/// Install: download OTH-LAN-x.y.z.zip + SHA256SUMS.txt of the same release, sha256 must match (zip and the Setup.exe
/// inside it), unpack to %TEMP%, start that Setup.exe "update --wait-pid": it replaces the plugin DLL after the game has
/// exited (the loaded DLL cannot be replaced in place). Rules and parsing: UpdateLogic.
/// </summary>
internal static class Updater
{
    public enum St { Off, Idle, Checking, UpToDate, Offline, Available, Downloading, Ready, Failed }
    public static volatile St State = St.Idle;
    public static UpdateLogic.Release Latest;
    public static string Detail = "";             // English, for the log / F7 (failure reason)
    public static float Progress;                 // 0..1 while downloading
    public static bool PromptOpen => _prompt;
    public static int Checks, Fails, Started;
    static bool _prompt, _dismissed, _readyShown, _offlineShown;
    static int _focus;
    static string _testApi, _testPrefix;
    static UnityEngine.EventSystems.EventSystem _es;

    static bool TestLoopback => _testApi != null;

    public static void Init()
    {
#if SELFTEST
        if (!string.IsNullOrWhiteSpace(Plugin.UpdateTestApi.Value))
        {
            _testApi = Plugin.UpdateTestApi.Value.Trim();
            if (Uri.TryCreate(_testApi, UriKind.Absolute, out var u)) _testPrefix = $"{u.Scheme}://{u.Authority}/";
            Plugin.L.LogWarning($"[Update] TEST endpoint {_testApi} (self-test build only)");
        }
#endif
        if (!Plugin.UpdateCheck.Value) { State = St.Off; Plugin.L.LogInfo("[Update] check for updates is off (F7 / cfg Update.CheckForUpdates)"); return; }
        CheckAsync(delaySeconds: 3);
    }

    public static void CheckAsync(double delaySeconds = 0)
    {
        if (State is St.Checking or St.Downloading) return;
        State = St.Checking; Checks++;
        _offlineShown = false; // a fresh check may notify again if it still fails
        new Thread(() => CheckJob(delaySeconds).GetAwaiter().GetResult()) { IsBackground = true, Name = "othlan-update" }.Start();
    }

    /// <summary>F7 / dialog: re-run the GitHub check now.</summary>
    public static void CheckNow()
    {
        Plugin.UpdateSkipped.Value = "";
        _dismissed = false; _readyShown = false; _offlineShown = false;
        if (_prompt) Close();
        CheckAsync(0);
    }

    static async Task CheckJob(double delaySeconds)
    {
        try { if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds)); await Check(); }
        catch (Exception e) { Fail(St.Offline, e is TaskCanceledException ? "timeout" : e.GetBaseException().Message); }
    }

    static void Fail(St st, string why) { State = st; Detail = why ?? ""; if (st == St.Failed) Fails++; Plugin.L.LogInfo($"[Update] {st}: {Detail}"); }

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
        var api = _testApi ?? UpdateLogic.LatestApi;
        string etag = null, cached = null;
        try { if (_testApi == null && File.Exists(CachePath)) { var t = File.ReadAllText(CachePath); var i = t.IndexOf('\n'); if (i > 0) { etag = t[..i]; cached = t[(i + 1)..]; } } } catch { }
        using var c = Client(TimeSpan.FromSeconds(5));
        using var r = await Get(c, api, q =>
        {
            q.Headers.Accept.ParseAdd("application/vnd.github+json");
            q.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (etag != null) q.Headers.TryAddWithoutValidation("If-None-Match", etag);
        });
        string json;
        if (r.StatusCode == HttpStatusCode.NotModified && cached != null) json = cached;
        else if (!r.IsSuccessStatusCode) { Fail(St.Offline, $"HTTP {(int)r.StatusCode}"); return; }
        else
        {
            json = await r.Content.ReadAsStringAsync();
            if (json.Length > 1_000_000) { Fail(St.Offline, "answer too large"); return; }
            try { if (_testApi == null && r.Headers.ETag != null) File.WriteAllText(CachePath, r.Headers.ETag.ToString() + "\n" + json); } catch { }
        }
        var rel = UpdateLogic.ParseRelease(json, out var why, _testPrefix);
        if (rel == null) { Fail(St.UpToDate, "no usable release: " + why); return; }
        Latest = rel;
        if (!UpdateLogic.IsNewer(rel.Version, Plugin.Version)) { State = St.UpToDate; Detail = rel.Version; Plugin.L.LogInfo($"[Update] up to date ({Plugin.Version}; latest {rel.Version})"); return; }
        State = St.Available; Detail = rel.Version;
        var skipped = UpdateLogic.ShouldOffer(rel.Version, Plugin.Version, Plugin.UpdateSkipped.Value) ? "" : " (skipped by the user: no prompt)";
        Plugin.L.LogInfo($"[Update] OTH-LAN {rel.Version} available (running {Plugin.Version}){skipped}: {rel.ZipName} {rel.ZipSize} B");
    }

    /// <summary>Download, verify, unpack, start the verified Setup.exe "update --wait-pid". Background.</summary>
    public static void Install()
    {
        var rel = Latest;
        if (rel == null || State is St.Downloading or St.Ready) return;
        State = St.Downloading; Progress = 0; Detail = "";
        new Thread(() => InstallJob(rel).GetAwaiter().GetResult()) { IsBackground = true, Name = "othlan-update-dl" }.Start();
    }

    static async Task InstallJob(UpdateLogic.Release rel)
    {
        {
            string dir = null;
            try
            {
                dir = Path.Combine(Path.GetTempPath(), "OTH-LAN-update", rel.Version);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                using var c = Client(TimeSpan.FromMinutes(20));
                // 1) sums (small)
                string sumsText;
                using (var r = await Get(c, rel.SumsUrl))
                {
                    if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{UpdateLogic.SumsName}: HTTP {(int)r.StatusCode}");
                    if (r.Content.Headers.ContentLength > 65536) throw new InvalidOperationException($"{UpdateLogic.SumsName} too large");
                    sumsText = await r.Content.ReadAsStringAsync();
                }
                var sums = UpdateLogic.ParseSums(sumsText);
                if (!sums.TryGetValue(rel.ZipName, out var want)) throw new InvalidOperationException($"{rel.ZipName} not listed in {UpdateLogic.SumsName}");
                // 2) zip, hashed while it streams
                var zip = Path.Combine(dir, rel.ZipName);
                string got;
                using (var r = await Get(c, rel.ZipUrl))
                {
                    if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{rel.ZipName}: HTTP {(int)r.StatusCode}");
                    var total = r.Content.Headers.ContentLength ?? rel.ZipSize;
                    if (total > 400L * 1024 * 1024) throw new InvalidOperationException("zip too large");
                    using var src = await r.Content.ReadAsStreamAsync();
                    using var dst = File.Create(zip);
                    using var sha = SHA256.Create();
                    var buf = new byte[1 << 16]; long n = 0; int k;
                    while ((k = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, k); sha.TransformBlock(buf, 0, k, null, 0); n += k;
                        if (n > 400L * 1024 * 1024) throw new InvalidOperationException("zip too large");
                        if (total > 0) Progress = (float)Math.Min(1.0, (double)n / total);
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    got = Convert.ToHexString(sha.Hash).ToLowerInvariant();
                }
                if (!UpdateLogic.HashEquals(got, want))
                {
                    try { File.Delete(zip); } catch { }
                    throw new InvalidOperationException($"sha256 mismatch for {rel.ZipName}: got {UpdateLogic.ShortHash(got)}, release says {UpdateLogic.ShortHash(want)} (nothing installed)");
                }
                Plugin.L.LogInfo($"[Update] {rel.ZipName} sha256 OK ({got})");
                // 3) unpack (no path escapes), then the Setup.exe hash too
                var outDir = Path.Combine(dir, "x");
                var root = Path.GetFullPath(outDir) + Path.DirectorySeparatorChar;
                using (var za = ZipFile.OpenRead(zip))
                    foreach (var e in za.Entries)
                    {
                        var p = Path.GetFullPath(Path.Combine(outDir, e.FullName));
                        if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("zip entry outside the folder: " + e.FullName);
                        if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(p); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(p));
                        e.ExtractToFile(p, true);
                    }
                var setup = Path.Combine(outDir, $"OTH-LAN-{rel.Version}", "OTH-LAN-Setup.exe");
                if (!File.Exists(setup)) throw new InvalidOperationException("OTH-LAN-Setup.exe not found in the zip");
                if (sums.TryGetValue("OTH-LAN-Setup.exe", out var wantExe))
                {
                    string gotExe; using (var f = File.OpenRead(setup)) using (var s = SHA256.Create()) gotExe = Convert.ToHexString(s.ComputeHash(f)).ToLowerInvariant();
                    if (!UpdateLogic.HashEquals(gotExe, wantExe)) throw new InvalidOperationException("sha256 mismatch for OTH-LAN-Setup.exe (nothing installed)");
                }
                // 4) start it: it waits for this process to exit, then replaces the plugin DLL
                var game = BepInEx.Paths.GameRootPath;
                var pid = Process.GetCurrentProcess().Id;
                var psi = new ProcessStartInfo(setup, $"update --game \"{game.TrimEnd('\\')}\" --wait-pid {pid}")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(setup) };
                using (var p = Process.Start(psi)) { if (p == null) throw new InvalidOperationException("could not start OTH-LAN-Setup.exe"); Plugin.L.LogInfo($"[Update] updater started (pid {p.Id}): installs {rel.Version} when the game exits"); }
                Started++;
                State = St.Ready; Progress = 1;
            }
            catch (Exception e) { Fail(St.Failed, e.GetBaseException().Message); }
        }
    }

    public static void Skip() { if (Latest != null) { Plugin.UpdateSkipped.Value = Latest.Version; Plugin.L.LogInfo($"[Update] {Latest.Version} skipped by the user"); } Close(); }
    public static void Later() { Plugin.L.LogInfo("[Update] later"); Close(); }
    public static void QuitAndUpdate() { Plugin.L.LogInfo("[Update] quit and update"); Close(); Application.Quit(); }

    static void Close()
    {
        _prompt = false; _dismissed = true;
        try { if (_es != null && !_es.WasCollected) _es.enabled = true; } catch { }
        _es = null;
    }

    static string[] Buttons() => State switch
    {
        St.Ready => new[] { "upd.quitUpdate", "upd.later" },
        St.Failed => new[] { "upd.retry", "upd.later" },
        St.Offline => new[] { "upd.retry", "upd.later" },
        St.Downloading => new[] { "upd.later" },
        _ => new[] { "upd.install", "upd.later", "upd.skip" },
    };

    static void Press(string key)
    {
        switch (key)
        {
            case "upd.install": Install(); _focus = 0; break;
            case "upd.retry":
                if (State == St.Offline) CheckNow();
                else Install();
                _focus = 0;
                break;
            case "upd.later": Later(); break;
            case "upd.skip": Skip(); break;
            case "upd.quitUpdate": QuitAndUpdate(); break;
        }
    }

    /// <summary>Main thread: opens the prompt in the main menu; keyboard + gamepad focus.</summary>
    public static void Tick()
    {
        if (!_prompt)
        {
            // a finished download re-opens the prompt once (Quit and update), even after "Later" during the download
            if (State == St.Ready && !_readyShown && Latest != null && CoopMenu.MainMenuVisible) { _readyShown = true; _dismissed = false; }
            else if (State == St.Offline && !_offlineShown && !_dismissed && CoopMenu.MainMenuVisible)
            {
                _offlineShown = true; _dismissed = false;
                _prompt = true; _focus = 0;
                try { _es = UnityEngine.EventSystems.EventSystem.current; if (_es != null) _es.enabled = false; } catch { _es = null; }
                Plugin.L.LogInfo($"[Update] offline prompt shown ({Detail})");
                return;
            }
            else if (_dismissed || State != St.Available || Latest == null || !CoopMenu.MainMenuVisible) return;
            else if (!UpdateLogic.ShouldOffer(Latest.Version, Plugin.Version, Plugin.UpdateSkipped.Value)) { _dismissed = true; return; }
            _prompt = true; _focus = 0;
            // the game's menu must not move behind the prompt (bug #1 lesson: one owner of navigation at a time)
            try { _es = UnityEngine.EventSystems.EventSystem.current; if (_es != null) _es.enabled = false; } catch { _es = null; }
            Plugin.L.LogInfo($"[Update] prompt shown for {Latest.Version}");
            return;
        }
        if (!CoopMenu.MainMenuVisible && State != St.Downloading && State != St.Ready) { _prompt = false; try { if (_es != null) _es.enabled = true; } catch { } _es = null; return; }
        var b = Buttons();
        if (_focus >= b.Length) _focus = 0;
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current; var gp = UnityEngine.InputSystem.Gamepad.current;
            bool left = kb != null && (kb.leftArrowKey.wasPressedThisFrame || kb.upArrowKey.wasPressedThisFrame) || gp != null && (gp.dpad.left.wasPressedThisFrame || gp.dpad.up.wasPressedThisFrame || gp.leftStick.left.wasPressedThisFrame);
            bool right = kb != null && (kb.rightArrowKey.wasPressedThisFrame || kb.downArrowKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame) || gp != null && (gp.dpad.right.wasPressedThisFrame || gp.dpad.down.wasPressedThisFrame || gp.leftStick.right.wasPressedThisFrame);
            bool ok = kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame) || gp != null && gp.buttonSouth.wasPressedThisFrame;
            bool back = kb != null && kb.escapeKey.wasPressedThisFrame || gp != null && gp.buttonEast.wasPressedThisFrame;
            if (left) _focus = (_focus + b.Length - 1) % b.Length;
            if (right) _focus = (_focus + 1) % b.Length;
            if (ok) Press(b[_focus]);
            else if (back) Later();
        }
        catch { }
    }

    public static void OnGUI()
    {
        if (!_prompt) return;
        if (State != St.Offline && Latest == null) return;
        Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        float w = Mathf.Min(760, Screen.width - 40), h = 330, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
        GUI.Box(new Rect(x, y, w, h), ""); GUI.Box(new Rect(x, y, w, h), ""); GUI.Box(new Rect(x, y, w, h), "");
        var title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        var body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        var titleText = State == St.Offline ? Loc.T("upd.titleOffline") : Loc.F("upd.title", Latest.Version);
        GUI.Label(new Rect(x + 18, y + 14, w - 36, 32), titleText, title);
        string text = State switch
        {
            St.Offline => Loc.F("upd.bodyOffline", string.IsNullOrEmpty(Detail) ? "—" : Detail),
            St.Downloading => Loc.F("upd.downloading", Progress * 100),
            St.Ready => Loc.T("upd.ready"),
            St.Failed => Loc.F("upd.failed", Detail),
            _ => Loc.F("upd.body", Plugin.Version, Latest.Version) + "\n\n" + UpdateLogic.ShortNotes(Latest.Notes, Loc.Lang),
        };
        GUI.Label(new Rect(x + 18, y + 52, w - 36, h - 120), text, body);
        var b = Buttons();
        float bw = (w - 36 - 12 * (b.Length - 1)) / b.Length, by = y + h - 58;
        for (int i = 0; i < b.Length; i++)
        {
            var bs = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = i == _focus ? FontStyle.Bold : FontStyle.Normal };
            var label = (i == _focus ? "› " : "") + Loc.T(b[i]) + (i == _focus ? " ‹" : "");
            var r = new Rect(x + 18 + i * (bw + 12), by, bw, 42);
            if (i == _focus) { GUI.Box(r, ""); GUI.Box(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), ""); }
            if (GUI.Button(r, label, bs)) { _focus = i; Press(b[i]); }
        }
        PromptFrame = Time.frameCount;
    }
    public static int PromptFrame = -1;

    /// <summary>F7 line.</summary>
    public static Msg StatusMsg() => State switch
    {
        St.Off => new Msg("upd.st.off"),
        St.Checking => new Msg("upd.st.checking"),
        St.UpToDate => new Msg("upd.st.upToDate", Plugin.Version),
        St.Offline => new Msg("upd.st.offline"),
        St.Available => new Msg("upd.st.available", Latest?.Version ?? "?"),
        St.Downloading => new Msg("upd.downloading", Progress * 100),
        St.Ready => new Msg("upd.st.ready", Latest?.Version ?? "?"),
        St.Failed => new Msg("upd.failed", Detail),
        _ => new Msg("upd.st.idle"),
    };

    /// <summary>Self-test: press a prompt button by key.</summary>
    public static bool TestPress(string key) { if (!_prompt) return false; Press(key); return true; }
    public static string TestButtons() => string.Join(",", Buttons());
    public static int TestFocus => _focus;
}
