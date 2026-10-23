using System;
using OthLan;
using L = OthLan.PingLogic.Level;
using M = OthLan.CarSyncLogic.Mode;
using A = OthLan.CarSyncLogic.Action;

static class Program
{
    static int fails, total;
    static void Eq<T>(T got, T want, string what)
    {
        total++;
        if (!Equals(got, want)) { fails++; Console.WriteLine($"FAIL {what}: got {got}, want {want}"); }
    }
    // ---------- 0.1.8 v3 remote-car smoothing (RemoteSmoother) ----------
    static void Smooth()
    {
        var c = new RemoteSmoother.Cfg(); double dt = 1 / 60.0;
        // constant speed 10 m/s: passes through untouched (no latency)
        var s = new RemoteSmoother(); double maxDev = 0;
        for (int i = 0; i < 120; i++) { var (x, _, _) = s.Step(i * dt * 10, 0, 0, dt, c, false); maxDev = Math.Max(maxDev, Math.Abs(x - i * dt * 10)); }
        Eq(maxDev < 1e-9, true, "smooth: steady motion untouched");
        // stall: Fusion freezes for 0.4 s, then resumes from the frozen point (Fusion clamps to the newest snapshot)
        s = new RemoteSmoother(); double fx = 0, prevDrawn = 0, maxStep = 0; int i2 = 0;
        for (; i2 < 60; i2++) { fx += 10 * dt; prevDrawn = s.Step(fx, 0, 0, dt, c, false).x; }
        for (int k = 0; k < 24; k++) { var d = s.Step(fx, 0, 0, dt, c, true).x; maxStep = Math.Max(maxStep, Math.Abs(d - prevDrawn)); prevDrawn = d; }
        Eq(Math.Abs(prevDrawn - (fx + 0.4 * 10)) < 0.05, true, $"smooth: extrapolated through a 0.4 s stall ({prevDrawn - fx:0.00} m ahead)");
        Eq(s.Last, RemoteSmoother.State.Extrapolate, "smooth: state extrapolate");
        // snapshots resume with a 2 m catch-up jump forward (time re-sync): drawn pose stays continuous
        fx += 4 + 2; var dj = s.Step(fx, 0, 0, dt, c, false).x; Eq(Math.Abs(dj - prevDrawn) < 0.5, true, $"smooth: resume jump absorbed (step {dj - prevDrawn:0.00} m)");
        prevDrawn = dj; maxStep = 0;
        for (int k = 0; k < 120; k++) { fx += 10 * dt; var d = s.Step(fx, 0, 0, dt, c, false).x; maxStep = Math.Max(maxStep, Math.Abs(d - prevDrawn - 10 * dt)); prevDrawn = d; }
        Eq(maxStep <= (c.MaxCorrection + c.SpeedFactor * 10) * dt + 1e-6, true, $"smooth: correction speed bounded ({maxStep / dt:0.0} m/s extra)");
        Eq(s.OffsetLength < 0.01, true, $"smooth: offset gone after 2 s ({s.OffsetLength:0.000} m)");
        // car stops against a wall (no stall): never extrapolated
        s = new RemoteSmoother(); fx = 0;
        for (int k = 0; k < 60; k++) { fx += 10 * dt; s.Step(fx, 0, 0, dt, c, false); }
        var w = s.Step(fx, 0, 0, dt, c, false).x; Eq(Math.Abs(w - fx) < 0.2, true, $"smooth: real stop not extrapolated ({w - fx:0.00} m)");
        // teleport: shown at once
        var tp = s.Step(fx + 2400, 0, 0, dt, c, false).x; Eq(Math.Abs(tp - (fx + 2400)) < 1e-9, true, "smooth: teleport not smoothed");
        Eq(s.Snaps, 1, "smooth: snap counted");
        // long stall beyond ExtrapolateSeconds: holds, no runaway
        s = new RemoteSmoother(); fx = 0;
        for (int k = 0; k < 60; k++) { fx += 10 * dt; s.Step(fx, 0, 0, dt, c, false); }
        double last = 0; for (int k = 0; k < 180; k++) last = s.Step(fx, 0, 0, dt, c, true).x;
        Eq(last - fx <= 10 * c.ExtrapolateSeconds + 0.2, true, $"smooth: 3 s stall bounded ({last - fx:0.00} m ahead)");
        Eq(s.Last, RemoteSmoother.State.Hold, "smooth: hold after the extrapolation window");
        Eq(HostFollowLogic.ServoSpeed(1.0, 0.25, 6, 0.5, 0), 4.0, "servo: 1 m / 0.25 s = 4 m/s");
        Eq(HostFollowLogic.ServoSpeed(10.0, 0.25, 6, 0.5, 10), 11.0, "servo: capped at 6 + 0.5 v");
        Eq(HostFollowLogic.ServoSpeed(0, 0.25, 6, 0.5, 10), 0.0, "servo: no error, no correction");
        Eq(RemoteSmoother.TauFor(0.1, c), c.Tau, "smooth: small error slow decay");
        Eq(RemoteSmoother.TauFor(5, c), c.TauLarge, "smooth: big error fast decay");
        Eq(RemoteSmoother.RotKeep(90, 0.1, 0.2, 180) > 0.79 && RemoteSmoother.RotKeep(90, 0.1, 0.2, 180) < 0.81, true, "smooth: rotation decay capped 180 deg/s");
    }

    // ---------- 0.1.8 update check (UpdateLogic) ----------
    static void Updates()
    {
        Eq(UpdateLogic.TryParseVersion("0.1.8", out var v) && v == (0, 1, 8), true, "ver 0.1.8");
        Eq(UpdateLogic.TryParseVersion("v0.1.10", out var v2) && v2 == (0, 1, 10), true, "ver v-prefix, two digits");
        Eq(UpdateLogic.TryParseVersion("0.1.9-beta", out _), false, "prerelease suffix rejected");
        Eq(UpdateLogic.TryParseVersion("0.1", out _), false, "two parts rejected");
        Eq(UpdateLogic.TryParseVersion("0.1.x", out _), false, "non-digit rejected");
        Eq(UpdateLogic.TryParseVersion("", out _), false, "empty rejected");
        Eq(UpdateLogic.TryParseVersion("1.2.3.4", out _), false, "four parts rejected");
        Eq(UpdateLogic.Compare("0.1.10", "0.1.9"), 1, "0.1.10 > 0.1.9 (numeric, not text)");
        Eq(UpdateLogic.Compare("0.2.0", "0.1.99"), 1, "minor beats patch");
        Eq(UpdateLogic.Compare("v0.1.8", "0.1.8"), 0, "equal with v");
        Eq(UpdateLogic.IsNewer("0.1.9", "0.1.8"), true, "newer");
        Eq(UpdateLogic.IsNewer("0.1.8", "0.1.8"), false, "same is not newer");
        Eq(UpdateLogic.IsNewer("0.1.7", "0.1.8"), false, "older is not newer");
        Eq(UpdateLogic.IsNewer("garbage", "0.1.8"), false, "garbage is not newer");
        Eq(UpdateLogic.ShouldOffer("0.1.9", "0.1.8", ""), true, "offer");
        Eq(UpdateLogic.ShouldOffer("0.1.9", "0.1.8", "0.1.9"), false, "skipped version not offered");
        Eq(UpdateLogic.ShouldOffer("0.1.10", "0.1.8", "0.1.9"), true, "newer than skipped offered");
        var h1 = new string('a', 64); var h2 = "0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef";
        var sums = UpdateLogic.ParseSums($"{h1}  OTH-LAN-0.1.9.zip\r\n{h2} *OTH-LAN-Setup.exe\nnot a line\n{new string('z', 64)}  bad.zip\n{h1}  ../evil.zip\n");
        Eq(sums.Count, 2, "sums: 2 valid lines");
        Eq(sums.TryGetValue("OTH-LAN-0.1.9.zip", out var z) && z == h1, true, "sums: zip");
        Eq(sums.TryGetValue("OTH-LAN-Setup.exe", out var e) && e == h2.ToLowerInvariant(), true, "sums: binary-mode star, lower-cased");
        Eq(sums.ContainsKey("../evil.zip"), false, "sums: no paths");
        Eq(UpdateLogic.HashEquals(h2, h2.ToLowerInvariant()), true, "hash compare ignores case");
        Eq(UpdateLogic.HashEquals(h1, h2), false, "hash mismatch");
        Eq(UpdateLogic.HashEquals("", ""), false, "empty hash never matches");
        Eq(UpdateLogic.AllowedUrl("https://api.github.com/repos/x"), true, "api allowed");
        Eq(UpdateLogic.AllowedUrl("https://objects.githubusercontent.com/a"), true, "objects allowed");
        Eq(UpdateLogic.AllowedUrl("http://github.com/a"), false, "http refused");
        Eq(UpdateLogic.AllowedUrl("https://github.com.evil.com/a"), false, "look-alike host refused");
        Eq(UpdateLogic.AllowedUrl("https://user@github.com/a"), false, "user info refused");
        Eq(UpdateLogic.AllowedUrl("https://github.com:8443/a"), false, "other port refused");
        Eq(UpdateLogic.AllowedUrl("http://127.0.0.1:8765/x"), false, "loopback refused without test flag");
        Eq(UpdateLogic.AllowedUrl("http://127.0.0.1:8765/x", true), true, "loopback allowed in self-test");
        Eq(UpdateLogic.AllowedAssetUrl("https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.1.9/OTH-LAN-0.1.9.zip"), true, "asset of this repo");
        Eq(UpdateLogic.AllowedAssetUrl("https://github.com/Other/over-the-hill-lan-coop/releases/download/v0.1.9/OTH-LAN-0.1.9.zip"), false, "asset of another repo refused");
        Eq(UpdateLogic.ShortHash("b8b08fa1b43d18367afa8f9f1b8ad928e04ad900c3925df57ff94e0500000000"), "b8b08fa1…00000000", "short hash shows both ends");
        Eq(UpdateLogic.ShortHash("abc"), "abc", "short hash of a short string");
        string J(string tag, bool draft = false, bool pre = false, string repo = UpdateLogic.RepoPath) =>
            "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") +
            ",\"name\":\"OTH-LAN " + tag + "\",\"body\":\"## About\\nx\\n\\n## What's in 0.1.9\\n- **Faster** sync\\n  more text\\n- Fix `F7`\\n\\n## Что в 0.1.9\\n- Быстрее\\n\",\"assets\":[" +
            "{\"name\":\"OTH-LAN-0.1.9.zip\",\"size\":123,\"browser_download_url\":\"https://github.com/" + repo + "/releases/download/" + tag + "/OTH-LAN-0.1.9.zip\"}," +
            "{\"name\":\"SHA256SUMS.txt\",\"size\":9,\"browser_download_url\":\"https://github.com/" + repo + "/releases/download/" + tag + "/SHA256SUMS.txt\"}]}";
        var rel = UpdateLogic.ParseRelease(J("v0.1.9"), out var why);
        Eq(rel != null && rel.Version == "0.1.9" && rel.ZipSize == 123 && rel.SumsUrl.EndsWith("SHA256SUMS.txt"), true, "release parsed " + why);
        Eq(UpdateLogic.ParseRelease(J("v0.1.9", draft: true), out var w1) == null && w1 == "draft", true, "draft skipped");
        Eq(UpdateLogic.ParseRelease(J("v0.1.9", pre: true), out var w2) == null && w2 == "prerelease", true, "prerelease skipped");
        Eq(UpdateLogic.ParseRelease(J("v0.1.9", repo: "Evil/over-the-hill-lan-coop"), out var w3) == null && w3.StartsWith("no OTH-LAN-0.1.9.zip"), true, "assets from another repo ignored");
        Eq(UpdateLogic.ParseRelease("{not json", out var w4) == null && w4.StartsWith("bad JSON"), true, "bad JSON");
        Eq(UpdateLogic.ParseRelease("{\"tag_name\":\"nightly\"}", out _) == null, true, "non-version tag");
        Eq(UpdateLogic.ShortNotes(rel.Notes, "en"), "• Faster sync more text\n• Fix F7", "notes en");
        Eq(UpdateLogic.ShortNotes(rel.Notes, "ru"), "• Быстрее", "notes ru");
        var tp = "http://127.0.0.1:8765/";
        var tj = "{\"tag_name\":\"v0.1.9\",\"assets\":[{\"name\":\"OTH-LAN-0.1.9.zip\",\"browser_download_url\":\"http://127.0.0.1:8765/d/OTH-LAN-0.1.9.zip\"},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"http://127.0.0.1:8765/d/SHA256SUMS.txt\"}]}";
        Eq(UpdateLogic.ParseRelease(tj, out _) == null, true, "loopback assets refused without test prefix");
        Eq(UpdateLogic.ParseRelease(tj, out _, tp) != null, true, "loopback assets with test prefix");
    }

    // ---------- netcode v2 (0.1.8, docs/NETCODE-v2.md) ----------
    static void NetcodeV2()
    {
        // reconnect while the old connection is still in the session (0.1.8): drop the old one, keep the slot
        var rs = new SlotAllocator(4, 1);
        Eq(rs.Request("A", 2, 0), 2, "rejoin: A -> 2");
        rs.Sync(i => i == 2, 1);
        Eq(rs.KickPending, false, "rejoin: nothing to drop on a normal join");
        Eq(rs.Request("A", 2, 5), 2, "rejoin: A asks again while still connected -> same slot");
        Eq(string.Join(",", rs.TakeKicks()), "2", "rejoin: old connection of slot 2 is dropped");
        Eq(rs.TakeKicks().Count, 0, "rejoin: kick is taken once");
        Eq(rs.Request("A", 2, 5.5), 2, "rejoin: a second ask while stale -> no second kick");
        Eq(rs.KickPending, false, "rejoin: no duplicate kick");
        Eq(rs.Describe(), "2:stale:A", "rejoin: slot shown as stale");
        Eq(rs.Admit(2, SlotAllocator.Tag("A"), 6, out _), true, "rejoin: the reconnecting client is admitted");
        Eq(rs.Admit(2, SlotAllocator.Tag("B"), 6, out _), false, "rejoin: others still refused");
        Eq(rs.Sync(i => false, 6.5).Count, 0, "rejoin: old one gone -> slot kept (not released)");
        Eq(rs.Describe(), "2:res:A", "rejoin: reserved for A");
        rs.Sync(i => i == 2, 7); Eq(rs.Describe(), "2:in:A", "rejoin: new connection in");
        var rt = new SlotAllocator(4, 1);
        rt.Request("A", 2, 0); rt.Sync(i => i == 2, 1); rt.Request("A", 2, 2); rt.TakeKicks();
        rt.Sync(i => i == 2, 3); Eq(rt.Describe(), "2:stale:A", "rejoin: index stays valid (new conn) -> wait");
        rt.Sync(i => i == 2, 5.5); Eq(rt.Describe(), "2:in:A", "rejoin: after 3 s the slot is simply in use again");
        Eq(rt.Request("A", 2, 9), 2, "rejoin: a later reconnect can drop the old connection again");
        Eq(rt.KickPending, true, "rejoin: second kick queued");
        // own car: take the host's pose only when very far away and not right after a link gap
        Eq(OwnCarLogic.TakeHostPose(30, 10, 25, 4), true, "own: 30 m, no recent gap -> teleport, take host pose");
        Eq(OwnCarLogic.TakeHostPose(30, 1, 25, 4), false, "own: 30 m right after a gap -> keep ours (host converges)");
        Eq(OwnCarLogic.TakeHostPose(10, 100, 25, 4), false, "own: 10 m -> keep ours (0.1.7 took it after 3 s)");
        Eq(OwnCarLogic.TakeHostPose(5, 100, 25, 4), false, "own: 5 m -> keep ours");
        // input buffer: 8 s at 50 Hz = 400, never below the game's
        Eq(OwnCarLogic.InputCapacity(8, 50, 50), 400, "input buffer 8 s @50 Hz");
        Eq(OwnCarLogic.InputCapacity(0, 50, 50), 50, "input buffer 0 = game default");
        Eq(OwnCarLogic.InputCapacity(0.5, 50, 50), 50, "input buffer never shrinks");
        Eq(OwnCarLogic.InputCapacity(100, 60, 60), 1200, "input buffer capped at 20 s");
        // host: OTAC only when the game moved the car away from the client's pose
        Eq(HostFollowLogic.GameMovedCar(6, 0, 10, 0.016, 0.5, 6.4, 2, 2), true, "host: jump away from client pose = game teleport");
        Eq(HostFollowLogic.GameMovedCar(4, 0, 10, 0.016, 4.2, 0.3, 2, 2), false, "host: jump toward client pose = correction, no OTAC");
        Eq(HostFollowLogic.GameMovedCar(0.1, 0, 10, 0.016, 0.5, 0.5, 2, 2), false, "host: normal motion");
        Eq(HostFollowLogic.GameMovedCar(0.5, 90, 1, 0.016, 0.5, 0.6, 5, 92), true, "host: reset upright (turned away) = game");
        Eq(HostFollowLogic.GameMovedCar(0.5, 45, 1, 0.016, 0.5, 0.6, 50, 5), false, "host: turned toward client heading = correction");
        var oc = new OutageLogic.Cfg();
        Eq(HostFollowLogic.Recover(80, 5, false, 10, oc, out var cs), OutageLogic.Recovery.Snap, "host: link back 80 m (client drove on) -> snap to client, not hands off");
        Eq(HostFollowLogic.Recover(6, 5, false, 10, oc, out cs) == OutageLogic.Recovery.Converge && cs > 0.3 && cs < 1.5, true, "host: link back 6 m -> converge");
        // smoothness metric
        var sm = new Smoothness();
        for (int i = 0; i < 120; i++) sm.Add(i * 20.0 / 60, 0, 0, 1.0 / 60);      // 20 m/s steady
        Eq(sm.Over10cm, 0, "smooth: steady 20 m/s has no hitches");
        Eq(sm.Percentile(99) < 1e-6, true, "smooth: p99 ~0 for steady motion");
        var sm2 = new Smoothness();
        for (int i = 0; i < 120; i++) sm2.Add(i * 20.0 / 60 + (i == 60 ? 0.8 : 0), 0, 0, 1.0 / 60);   // one 0.8 m pop
        Eq(sm2.Over50cm >= 1 && sm2.Max > 0.7, true, $"smooth: a 0.8 m pop is caught ({sm2.Describe()})");
        var sm3 = new Smoothness();
        for (int i = 0; i < 60; i++) sm3.Add(i < 30 ? i * 0.3 : 1000 + i * 0.3, 0, 0, 1.0 / 60);
        Eq(sm3.Over2m, 0, "smooth: a teleport (> 50 m) is not counted as a hitch");
        var gs = new GapStats(); gs.Add(0.02); gs.Add(0.3); gs.Add(0.8); gs.Add(4);
        Eq((gs.Over250ms, gs.Over500ms, gs.Over1s, gs.Over3s), (3, 2, 1, 1), "gap histogram");
        // room flags
        Eq(RoomPropsLogic.AllowMods(new System.Collections.Generic.Dictionary<string, string> { ["RoomProperties"] = "i:0" }), (bool?)false, "room props i:0 -> mods off");
        Eq(RoomPropsLogic.AllowMods(new System.Collections.Generic.Dictionary<string, string> { ["RoomProperties"] = "i:3" }), (bool?)true, "room props i:3 -> mods on");
        Eq(RoomPropsLogic.AllowMods(new System.Collections.Generic.Dictionary<string, string>()), (bool?)null, "room props missing -> unknown");
        // main-menu navigation: hidden entries are skipped, our button gets linked both ways
        var nav = MenuNavLogic.Vertical(new[] { true, true, true, false, true }, false);   // single, multi, [coop], hidden, settings
        Eq(nav[1].down, 2, "menu: Multiplayer down -> Local Co-op");
        Eq(nav[2].up, 1, "menu: Local Co-op up -> Multiplayer");
        Eq(nav[2].down, 4, "menu: Local Co-op down skips the hidden entry");
        Eq(nav[0].up, -1, "menu: no wrap");
        var navw = MenuNavLogic.Vertical(new[] { true, true, true }, true);
        Eq((navw[0].up, navw[2].down), (2, 0), "menu: wrap kept when the game wraps");
    }

    // ---------- i18n (0.1.8): string tables, language switching, no UI text outside the tables ----------
    static void I18n()
    {
        var en = Loc.Table("en"); var ru = Loc.Table("ru");
        Eq(en != null && ru != null, true, "i18n: en and ru tables embedded");
        if (en == null || ru == null) return;
        Eq(en.Count > 100, true, $"i18n: en has the mod's texts ({en.Count})");
        foreach (var kv in en)
        {
            if (!ru.TryGetValue(kv.Key, out var r)) { Eq(kv.Key, "(present in ru)", "i18n: key missing in ru"); continue; }
            Eq(string.Join(",", Loc.Placeholders(kv.Value)), string.Join(",", Loc.Placeholders(r)), $"i18n: placeholders of '{kv.Key}' match");
            if (kv.Value.Trim().Length == 0 || r.Trim().Length == 0) Eq(kv.Key, "(non-empty)", "i18n: empty text");
        }
        foreach (var k in ru.Keys) if (!en.ContainsKey(k)) Eq(k, "(present in en)", "i18n: ru key missing in en");
        foreach (var lang in Loc.Languages)
            foreach (var kv in Loc.Table(lang))
                try { string.Format(System.Globalization.CultureInfo.InvariantCulture, kv.Value, 1, 2, 3, 4, 5, 6); }
                catch (FormatException) { Eq(kv.Key, "(valid format)", $"i18n: {lang} format string"); }
        Eq(Loc.Normalize("ru-RU"), "ru", "i18n: normalize ru-RU"); Eq(Loc.Normalize("en"), "en", "i18n: normalize en");
        Eq(Loc.Normalize("Russian (ru)"), "ru", "i18n: normalize display name"); Eq(Loc.Normalize("zh-Hans"), "zh-hans", "i18n: normalize zh-Hans");
        Eq(Loc.Normalize("pt_BR"), "pt", "i18n: normalize pt_BR"); Eq(Loc.Normalize(""), null, "i18n: normalize empty");
        Eq(Loc.Resolve("de"), "en", "i18n: unknown language -> en"); Eq(Loc.Resolve(null), "en", "i18n: null -> en"); Eq(Loc.Resolve("ru-RU"), "ru", "i18n: ru-RU -> ru");
        Loc.SetLanguage("en");
        var g = Loc.Generation;
        var m = new Msg("status.connected", "26.1.2.3:27015");
        var nested = new Msg("recover.status", new Msg("recover.hostTimeout", 20.0));
        Eq(m.ToString(), "connected to 26.1.2.3:27015", "i18n: Msg in en");
        Eq(Loc.SetLanguage("ru"), true, "i18n: switch to ru"); Eq(Loc.Generation, g + 1, "i18n: generation bumps");
        Eq(m.ToString(), "подключено к 26.1.2.3:27015", "i18n: the same Msg follows the language");
        Eq(nested.ToString(), "LAN не запустился: сервер не запустился за 20 с. Возврат в главное меню.", "i18n: nested Msg in ru");
        Eq(nested.En(), "LAN did not start: the server did not start within 20 s. Back to the main menu.", "i18n: log text stays English");
        Eq(Loc.SetLanguage("ru-RU"), false, "i18n: same table -> no change"); Eq(Loc.Generation, g + 1, "i18n: no bump without change");
        Eq(Loc.SetLanguage("de-DE"), true, "i18n: German -> English table"); Eq(Loc.Lang, "en", "i18n: German falls back to en");
        Eq(Loc.T("no.such.key"), "no.such.key", "i18n: unknown key shows the key");
        Eq(Msg.List(new Msg("f7.playerPing", 2, "40"), "x").ToString(), "player 2: 40 ms  x", "i18n: Msg.List");
        Eq(Loc.Placeholders("{0} {1:0.0} {{x}} {0}").Count, 2, "i18n: placeholder parser");

        // Source scan: user-visible text must come from the tables. (1) No Cyrillic in string literals of the plugin
        // (comments are fine); (2) every key the code asks for exists in en.json.
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "src", "OthLan.Plugin"))) dir = System.IO.Path.GetDirectoryName(dir);
        Eq(dir != null, true, "i18n: plugin sources found for the scan");
        if (dir == null) return;
        var cyr = new System.Text.RegularExpressions.Regex("\"[^\"\\n]*[\u0400-\u04FF][^\"\\n]*\"");
        var keyUse = new System.Text.RegularExpressions.Regex("(?:Loc\\.(?:T|F|En)|new Msg|LanState\\.Status)\\(\"([a-z0-9]+\\.[A-Za-z0-9.]+)\"");
        var uiCall = new System.Text.RegularExpressions.Regex("GUI\\.(Label|Button|Box|TextField)\\(|SetLabel\\(|\\.text = ");
        var keyArg = new System.Text.RegularExpressions.Regex("(?:Loc\\.(?:T|F|En|TIn)|new Msg|Two|GetComponent\\w*|Find\\w*|SetNextControlName|FocusControl)\\(\"[^\"]*\"(?:, \"[^\"]*\")?|UserChoice\\((?:true|false), \"[^\"]*\"|UserToggle\\(\"[^\"]*\"");
        var lit = new System.Text.RegularExpressions.Regex("\"[^\"\\n]*\"");
        var tag = new System.Text.RegularExpressions.Regex("<[^>]*>|\\{[^}]*\\}|OTH-LAN|Radmin|F7|F8|F9|F10|UDP|IP");
        var word = new System.Text.RegularExpressions.Regex("[A-Za-z]{3,}");
        var allow = new[] { "\"заняты\"" };   // Autopilot (self-test only) accepts the Russian "full" text from an older host
        int scanned = 0, used = 0;
        foreach (var f in System.IO.Directory.GetFiles(System.IO.Path.Combine(dir, "src", "OthLan.Plugin"), "*.cs"))
        {
            var lines = System.IO.File.ReadAllLines(f); scanned++;
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i]; var c = code.IndexOf("//"); if (c >= 0 && !code.Substring(0, c).Contains('"')) code = code.Substring(0, c);
                if (code.TrimStart().StartsWith("///")) continue;
                foreach (System.Text.RegularExpressions.Match x in cyr.Matches(code))
                    if (Array.IndexOf(allow, x.Value) < 0) Eq($"{System.IO.Path.GetFileName(f)}:{i + 1} {x.Value}", "(no UI text outside Lang/*.json)", "i18n: hardcoded text");
                if (uiCall.IsMatch(code))
                {
                    var rest = keyArg.Replace(code, "");
                    foreach (System.Text.RegularExpressions.Match x in lit.Matches(rest))
                        if (word.IsMatch(tag.Replace(x.Value, ""))) Eq($"{System.IO.Path.GetFileName(f)}:{i + 1} {x.Value}", "(UI text from Lang/*.json)", "i18n: hardcoded UI text");
                }
                foreach (System.Text.RegularExpressions.Match x in keyUse.Matches(code)) { used++; if (!en.ContainsKey(x.Groups[1].Value)) Eq(x.Groups[1].Value, "(in en.json)", $"i18n: key used in {System.IO.Path.GetFileName(f)}:{i + 1}"); }
            }
        }
        Eq(scanned > 20 && used > 80, true, $"i18n: scanned {scanned} files, {used} key uses");
        Loc.SetLanguage("en");
    }

    static int Main()
    {
        // Ping colours 0.1.7: green <= 200, yellow 200..300 (strictly above 200, 300 inclusive), red > 300
        foreach (var (ms, want) in new[] { (0.0, L.Green), (42, L.Green), (150, L.Green), (199, L.Green), (200, L.Green), (200.4, L.Yellow),
                                           (201, L.Yellow), (250, L.Yellow), (300, L.Yellow), (300.1, L.Red), (301, L.Red), (900, L.Red), (double.NaN, L.Green) })
            Eq(PingLogic.Classify(ms, 200, 300), want, $"Classify({ms})");
        Eq(PingLogic.Classify(110, 100, 200), L.Yellow, "Classify custom thresholds");

        // cfg migration v3
        Eq(PingLogic.MigrateV3(150, 250), (200.0, 300.0), "v3: old defaults -> 200/300");
        Eq(PingLogic.MigrateV3(null, 250), (200.0, 300.0), "v3: no old key -> 200/300");
        Eq(PingLogic.MigrateV3(120, 250), (120.0, 300.0), "v3: hand-edited orange kept");
        Eq(PingLogic.MigrateV3(150, 400), (200.0, 400.0), "v3: hand-edited red kept");
        Eq(PingLogic.MigrateV3(350, 250), (350.0, 350.0), "v3: red never below yellow");

        // Smoothing
        Eq(PingLogic.Smooth(double.NaN, 40, 0.3), 40.0, "first sample passes");
        Eq(Math.Round(PingLogic.Smooth(40, 50, 0.3), 3), 43.0, "light EMA");
        Eq(PingLogic.Smooth(40, double.NaN, 0.3), 40.0, "NaN sample ignored");
        Eq(Math.Round(PingLogic.Smooth(40, 400, 0.3), 1), 256.0, "spike converges fast (alpha 0.6)");
        double s = 40; for (int i = 0; i < 25; i++) s = PingLogic.Smooth(s, 60, 0.3);
        Eq(Math.Abs(s - 60) < 0.1, true, "EMA converges in ~5 s at 5 Hz");
        Eq(PingLogic.Format(41.6), "local 42 ms", "Format");
        Eq(PingLogic.Format(double.NaN), "local --", "Format NaN");
        Eq(PingLogic.LabelText(80, 15, 10), "local 80 ms  loss 15%", "label: loss shown at >= 10%");
        Eq(PingLogic.LabelText(80, 5, 10), "local 80 ms", "label: 5% loss hidden (no noise)");
        Eq(PingLogic.LabelText(double.NaN, 60, 10), "local --", "label: no value, no loss suffix");
        Eq(PingLogic.LabelText(80, 50, 0), "local 80 ms", "label: loss display disabled");
        Loc.SetLanguage("ru");
        Eq(PingLogic.LabelText(80, 50, 10, 0.61), "нет связи 1 с", "label: no link 0.61 s");
        Eq(PingLogic.LabelText(80, 50, 10, 3.2), "нет связи 4 с", "label: no link 3.2 s -> 4");
        Eq(PingLogic.LabelText(80, 50, 10, 3.0), "нет связи 3 с", "label: no link exactly 3 s");
        Loc.SetLanguage("en");
        Eq(PingLogic.LabelText(80, 50, 10, 3.0), "no link 3 s", "label EN: no link 3 s");
        Eq(PingLogic.ClassifyEcho(40, true, 200, 300), L.Red, "no link => red");
        Eq(PingLogic.ClassifyEcho(double.NaN, false, 200, 300), L.Waiting, "no value yet => waiting");
        Eq(PingLogic.ClassifyEcho(250, false, 200, 300), L.Yellow, "echo 250 => yellow");

        // EchoTracker (time injected): no link after 0.6 s, recovery after 3 replies in a row
        var t = new EchoTracker { Alpha = 0.3 };
        Eq(t.TimedOut(0), false, "not started: no timeout");
        var s1 = t.NextSeq(0.0);
        Eq(double.IsNaN(t.Value(0.01)), true, "waiting for first reply");
        Eq(Math.Round(t.OnReply(s1, 0.040), 3), 40.0, "rtt sample 40 ms");
        Eq(Math.Round(t.Value(0.05), 3), 40.0, "first value passes through");
        Eq(double.IsNaN(t.OnReply(s1, 0.05)), true, "duplicate reply ignored");
        var s2 = t.NextSeq(0.2); t.OnReply(s2, 0.25);   // 50 ms
        Eq(Math.Round(t.Value(0.26), 3), 43.0, "smoothed 40->50 = 43");
        for (int i = 0; i < 3; i++) t.NextSeq(0.4 + i * 0.2);            // host gone: unanswered pings
        Eq(t.TimedOut(0.80), false, "0.55 s since last reply: link still ok");
        Eq(t.NoLinkSeconds(0.80), -1.0, "no 'no link' seconds yet");
        Eq(t.TimedOut(0.86), true, "> 0.6 s without reply: no link");
        for (int i = 0; i < 7; i++) t.NextSeq(1.0 + i * 0.2);
        Eq(Math.Round(t.NoLinkSeconds(2.25), 2), 2.0, "no link counts from the last reply (0.25 -> 2.25)");
        Eq(double.IsNaN(t.Value(2.3)), true, "no link => no value");
        t.NextSeq(4.0);                                                    // expires the old ones
        Eq(t.LossPercent > 70, true, $"loss counted ({t.LossPercent:0}%)");
        var r1 = t.NextSeq(4.2); t.OnReply(r1, 4.26);
        Eq(t.TimedOut(4.3), true, "1 reply: still recovering (red)");
        Eq(t.NoLinkSeconds(4.3), -1.0, "recovering: value shown again");
        Eq(double.IsNaN(t.Value(4.3)), false, "recovering: ms available");
        var r2 = t.NextSeq(4.4); t.OnReply(r2, 4.45);
        Eq(t.TimedOut(4.5), true, "2 replies: still recovering");
        var r3 = t.NextSeq(4.6); t.OnReply(r3, 4.65);
        Eq(t.TimedOut(4.7), false, "3 replies in a row: recovered");
        Eq(t.LinkLosses, 1, "one link loss counted");
        // interrupted recovery
        var t3 = new EchoTracker(); var a1 = t3.NextSeq(0); t3.OnReply(a1, 0.02);
        Eq(t3.TimedOut(1.0), true, "t3 lost");
        var a2 = t3.NextSeq(1.0); t3.OnReply(a2, 1.02);
        Eq(t3.TimedOut(1.7), true, "gap again during recovery: lost");
        Eq(Math.Round(t3.NoLinkSeconds(1.7), 2), 0.68, "counted from the last reply");
        var a3 = t3.NextSeq(1.8); t3.OnReply(a3, 1.82); var a4 = t3.NextSeq(2.0); t3.OnReply(a4, 2.02);
        Eq(t3.TimedOut(2.1), true, "streak restarted: 2 of 3");
        var a5 = t3.NextSeq(2.2); t3.OnReply(a5, 2.22);
        Eq(t3.TimedOut(2.3), false, "3 in a row: recovered");
        var late = t.NextSeq(5.0); Eq(double.IsNaN(t.OnReply(late, 7.5)), true, "reply later than timeout ignored");
        var t2 = new EchoTracker(); t2.NextSeq(0); Eq(t2.TimedOut(0.5), false, "never answered: 0.5 s ok"); Eq(t2.TimedOut(0.7), false, "never answered: waiting (grace) at 0.7 s"); Eq(t2.TimedOut(10.1), true, "never answered => no link after the 10 s grace");
        var t3b = new EchoTracker(); t3b.NextSeq(0); t3b.OnReply(1, 0.01); Eq(t3b.TimedOut(0.7), true, "answered once, then silent => no link after 0.6 s");

        // ping packet codec
        var pk = CarSyncLogic.EncodePing(0, 2, 99, 1.5);
        Eq(CarSyncLogic.TryDecodePing(pk, out var kd, out var sl, out var sq) && kd == 0 && sl == 2 && sq == 99, true, "ping codec");
        pk[4] = 7; Eq(CarSyncLogic.TryDecodePing(pk, out _, out _, out _), false, "bad kind rejected");
        Eq(CarSyncLogic.TryDecodePing(new byte[12], out _, out _, out _), false, "old 12-byte ping rejected");

        // Mode hysteresis (150 +- 15)
        var c = new CarSyncLogic.Cfg();
        Eq(CarSyncLogic.NextMode(M.Low, 160, c), M.Low, "160 stays low");
        Eq(CarSyncLogic.NextMode(M.Low, 166, c), M.High, "166 -> high");
        Eq(CarSyncLogic.NextMode(M.High, 140, c), M.High, "140 stays high");
        Eq(CarSyncLogic.NextMode(M.High, 134, c), M.Low, "134 -> low");
        Eq(CarSyncLogic.NextMode(M.High, double.NaN, c), M.High, "NaN keeps mode");

        // Decisions
        Eq(CarSyncLogic.Decide(M.Low, 0.5, 2, 0.1, 0.05, c), A.Blend, "low: blend");
        Eq(CarSyncLogic.Decide(M.Low, 9, 2, 0.1, 0.05, c), A.Snap, "low: huge error snaps");
        Eq(CarSyncLogic.Decide(M.High, 1, 5, 0.5, 0.05, c), A.None, "high: free between checkpoints");
        Eq(CarSyncLogic.Decide(M.Low, 1284.6, 1, 0.1, 0.05, c), A.None, "low: teleport-sized gap (spawn) is left to the game");
        Eq(CarSyncLogic.Decide(M.High, 60, 1, 5, 0.05, c), A.None, "high: teleport-sized gap is not a desync snap");
        Eq(CarSyncLogic.NextMode(M.High, double.NaN, c), M.High, "echo timeout holds High");
        Eq(CarSyncLogic.Decide(M.High, 1, 5, 1.5, 0.05, c), A.SoftSnap, "high: checkpoint with real error -> soft snap");
        Eq(CarSyncLogic.Decide(M.High, 0.2, 2, 1.5, 0.05, c), A.CheckpointOk, "high: checkpoint skipped below 0.25 m and 3 deg");
        Eq(CarSyncLogic.Decide(M.High, 0.2, 4, 1.5, 0.05, c), A.SoftSnap, "high: checkpoint not skipped when angle >= 3 deg");
        Eq(CarSyncLogic.Decide(M.High, 0.3, 1, 1.5, 0.05, c), A.SoftSnap, "high: checkpoint not skipped when error >= 0.25 m");
        Eq(CarSyncLogic.Decide(M.High, 1, 5, 1.5, 0.05, new CarSyncLogic.Cfg { CheckpointSoftSeconds = 0 }), A.Snap, "high: soft=0 -> hard checkpoint snap");
        Eq(CarSyncLogic.Decide(M.High, 1, 5, 0.1, 0.05, c, softActive: true), A.SoftBlend, "high: inside soft window -> soft blend");
        Eq(CarSyncLogic.Decide(M.High, 3.5, 5, 0.1, 0.05, c, softActive: true), A.Snap, "high: desync still hard-snaps inside soft window");
        Eq(CarSyncLogic.Decide(M.Low, 0.5, 2, 0.1, 0.05, c, contactPaused: true), A.None, "low: contact pause leaves host physics alone");
        Eq(CarSyncLogic.Decide(M.High, 3.5, 40, 5, 0.05, c, contactPaused: true), A.None, "high: contact pause also suppresses desync snap");
        Eq(CarSyncLogic.InContact(2.0, 0, 0.5, 1.1, 2.4, 1.2, 0.3), true, "contact: side by side 2 m apart");
        Eq(CarSyncLogic.InContact(0.3, 0, 4.9, 1.1, 2.4, 1.2, 0.3), true, "contact: nose to tail (pushing)");
        Eq(CarSyncLogic.InContact(0.3, 0, 7.0, 1.1, 2.4, 1.2, 0.3), false, "no contact: convoy 7 m behind");
        Eq(CarSyncLogic.InContact(3.0, 0, 0, 1.1, 2.4, 1.2, 0.3), false, "no contact: next lane 3 m");
        Eq(CarSyncLogic.InContact(0, 6, 0, 1.1, 2.4, 1.2, 0.3), false, "no contact: car on a bridge above");
        Eq(CarSyncLogic.ContactPaused(10.4, 10.0, 0.5), true, "contact hold 0.4 s after");
        Eq(CarSyncLogic.ContactPaused(10.6, 10.0, 0.5), false, "contact hold over after 0.6 s");
        Eq(CarSyncLogic.ContactPaused(10.6, 0, 0.5), false, "never in contact");
        Eq(CarSyncLogic.HostJumped(0.5, 1, 25, 0.016), false, "jump: normal driving at 90 km/h is not a jump");
        Eq(CarSyncLogic.HostJumped(6.2, 2, 3, 0.016), true, "jump: portable-jack style 6 m move");
        Eq(CarSyncLogic.HostJumped(0.1, 90, 0, 0.016), true, "jump: flipped upright in place");
        Eq(CarSyncLogic.HostJumped(14, 1, 30, 0.25), false, "jump: 0.25 s frame hitch at 30 m/s is not a jump");
        Eq(Math.Round(CarSyncLogic.JumpHoldSeconds(200), 2), 0.6, "jump hold at 200 ms = 0.6 s");
        Eq(CarSyncLogic.JumpHoldSeconds(20), 0.5, "jump hold minimum 0.5 s");
        Eq(CarSyncLogic.Decide(M.High, 3.5, 5, 0.2, 0.05, c), A.Snap, "high: desync distance");
        Eq(CarSyncLogic.Decide(M.High, 0.5, 30, 0.2, 0.05, c), A.Snap, "high: desync angle");
        Eq(CarSyncLogic.Decide(M.High, 10, 90, 5, 1.5, c), A.None, "stale pose ignored");
        Eq(Math.Round(CarSyncLogic.BlendFactor(0.1, 0.1), 4), Math.Round(1 - Math.Exp(-1), 4), "blend factor");
        Eq(CarSyncLogic.Extrapolate(10, 20, 0.05), 11.0, "extrapolate 50 ms at 20 m/s");
        Eq(CarSyncLogic.Extrapolate(10, 20, 3), 20.0, "extrapolation capped at 0.5 s");

        // Wire format round trip + rejects
        var p = new CarSyncLogic.Pose { Slot = 2, Seq = 77, Tick = 123456, Px = 1, Py = 2, Pz = 3, Rx = 0, Ry = 0.7071f, Rz = 0, Rw = 0.7071f, Vx = 4, Vy = 5, Vz = 6, Ax = 7, Ay = 8, Az = 9 };
        var b = CarSyncLogic.Encode(p);
        Eq(b.Length, CarSyncLogic.PoseSize, "packet size");
        Eq(CarSyncLogic.TryDecode(b, out var q), true, "decode ok");
        Eq((q.Slot, q.Seq, q.Tick, q.Px, q.Ry, q.Vz, q.Az), (p.Slot, p.Seq, p.Tick, p.Px, p.Ry, p.Vz, p.Az), "round trip");
        b[0] ^= 1; Eq(CarSyncLogic.TryDecode(b, out _), false, "bad magic rejected");
        Eq(CarSyncLogic.TryDecode(new byte[10], out _), false, "short packet rejected");
        var nan = p; nan.Px = float.NaN; Eq(CarSyncLogic.TryDecode(CarSyncLogic.Encode(nan), out _), false, "NaN rejected");

        // 0.1.5 final: host flags on pings, tick-based pose age, seq restart
        var pf = CarSyncLogic.EncodePing(1, 2, 9, 1.0, CarSyncLogic.FlagCarSync);
        Eq(CarSyncLogic.TryDecodePing(pf, out var fk, out var fs, out var fq, out var ff) && fk == 1 && fs == 2 && fq == 9, true, "ping with flags decodes");
        Eq(ff, 1, "flags byte carries CarSync on");
        Eq(CarSyncLogic.TryDecodePing(CarSyncLogic.EncodePing(1, 2, 9, 1.0), out _, out _, out _, out var fn) ? fn : -99, -1, "no flags byte => -1 (unknown)");
        Eq(CarSyncLogic.TryDecodePing(new byte[25], out _, out _, out _), false, "oversized ping rejected");
        Eq(Math.Round(CarSyncLogic.PoseAgeFromTicks(1010, 1000, 0.02), 3), 0.2, "pose 10 ticks behind = 0.2 s");
        Eq(Math.Round(CarSyncLogic.PoseAgeFromTicks(1000, 1003, 0.02), 3), -0.06, "client predicted ahead => negative age");
        Eq(CarSyncLogic.PoseAgeFromTicks(5000, 10, 0.02), 0.5, "age clamped");
        Eq(CarSyncLogic.Extrapolate(10, 20, -0.05), 9.0, "negative age extrapolates backwards");
        Eq(CarSyncLogic.AcceptSeq(5, 4, 0.1), true, "newer seq accepted");
        Eq(CarSyncLogic.AcceptSeq(4, 5, 0.1), false, "older seq rejected");
        Eq(CarSyncLogic.AcceptSeq(1, 90000, 0.5), false, "restart not accepted while old stream is live");
        Eq(CarSyncLogic.AcceptSeq(1, 90000, 2.5), true, "seq restart accepted after 2 s silence");

        Gate016.Run((g, w, what) => Eq(g, w, what));

        Eq(CarSyncLogic.SnapStreak(0.05, 4), 5, "snap streak +1 within 0.25 s");
        Eq(CarSyncLogic.SnapStreak(0.3, 4), 1, "snap streak restarts after 0.25 s");
        // ---- 0.1.7 frozen Fusion tick -> rejoin (FreezeLogic) ----
        {
            var fc = new FreezeLogic.Cfg();
            Eq(FreezeLogic.Healthy(false, 0, fc), true, "freeze: healthy 0 %");
            Eq(FreezeLogic.Healthy(false, 30, fc), false, "freeze: 30 % loss not healthy");
            Eq(FreezeLogic.Healthy(true, 0, fc), false, "freeze: timed out not healthy");
            Eq(FreezeLogic.ShouldRejoin(12, 5, 0, 1e9, fc), true, "freeze: 12 s frozen + 5 s healthy -> rejoin");
            Eq(FreezeLogic.ShouldRejoin(11.9, 30, 0, 1e9, fc), false, "freeze: 11.9 s frozen -> wait");
            Eq(FreezeLogic.ShouldRejoin(30, 4.9, 0, 1e9, fc), false, "freeze: outage (side channel not healthy) -> no rejoin");
            Eq(FreezeLogic.ShouldRejoin(30, 30, 3, 1e9, fc), false, "freeze: max 3 attempts");
            Eq(FreezeLogic.ShouldRejoin(30, 30, 1, 59, fc), false, "freeze: one per minute");
        }

        // ---- 0.1.7 outage phases / recovery (OutageLogic) ----
        {
            var oc = new OutageLogic.Cfg();
            Eq(OutageLogic.PhaseOf(0.05, oc), OutageLogic.Phase.Normal, "g 0.05 normal");
            Eq(OutageLogic.PhaseOf(0.149, oc), OutageLogic.Phase.Normal, "g 0.149 normal");
            Eq(OutageLogic.PhaseOf(0.15, oc), OutageLogic.Phase.Extrapolate, "g 0.15 extrapolate");
            Eq(OutageLogic.PhaseOf(1.49, oc), OutageLogic.Phase.Extrapolate, "g 1.49 extrapolate");
            Eq(OutageLogic.PhaseOf(1.5, oc), OutageLogic.Phase.Brake, "g 1.5 brake (default BrakeFrom 1.5)");
            Eq(OutageLogic.PhaseOf(5.0, oc), OutageLogic.Phase.Brake, "g 5 brake");
            var oc5 = new OutageLogic.Cfg { PhysicsS = 5.0 };
            Eq(OutageLogic.PhaseOf(1.5, oc5), OutageLogic.Phase.PhysicsOnly, "BrakeFrom 5: g 1.5 physics only");
            Eq(OutageLogic.PhaseOf(4.99, oc5), OutageLogic.Phase.PhysicsOnly, "BrakeFrom 5: g 4.99 physics only");
            Eq(OutageLogic.PhaseOf(5.0, oc5), OutageLogic.Phase.Brake, "BrakeFrom 5: g 5 brake");
            Eq(OutageLogic.PhaseOf(30, oc), OutageLogic.Phase.Brake, "g 30 brake");
            Eq(OutageLogic.Confidence(0, oc), 1.0, "confidence 1 at 0");
            Eq(Math.Round(OutageLogic.Confidence(0.75, oc), 3), 0.5, "confidence 0.5 at 0.75 s");
            Eq(OutageLogic.Confidence(1.5, oc), 0.0, "confidence 0 at 1.5 s");
            Eq(OutageLogic.Confidence(3, oc), 0.0, "confidence clamped");
            Eq(Math.Round(OutageLogic.ExtrapolateTau(0.75, oc), 3), 0.2, "tau = 0.1/0.5");
            Eq(double.IsInfinity(OutageLogic.ExtrapolateTau(1.5, oc)), true, "tau infinite at 0 confidence");
            double v = 20; for (int i = 0; i < 60; i++) v *= OutageLogic.BrakeFactor(1 / 60.0, oc);
            Eq(v < 0.5, true, $"braking: 20 m/s -> {v:0.00} after 1 s");
            double v2 = 20; for (int i = 0; i < 15; i++) v2 *= OutageLogic.BrakeFactor(1 / 60.0, oc);
            Eq(v2 > 5, true, $"braking is gentle: {v2:0.0} m/s after 0.25 s");
            Eq(Math.Round(OutageLogic.ConvergeSeconds(0.5), 3), 0.34, "T(0.5 m)");
            Eq(OutageLogic.ConvergeSeconds(0), 0.3, "T min 0.3");
            Eq(Math.Round(OutageLogic.ConvergeSeconds(10), 3), 1.1, "T(10 m) = 1.1");
            Eq(OutageLogic.ConvergeSeconds(15), 1.5, "T(15 m) = 1.5");
            Eq(OutageLogic.ConvergeSeconds(40), 1.5, "T max 1.5");
            OutageLogic.Recovery R(double e, double d, bool contact = false, double waited = 0, double speed = 10) => OutageLogic.Decide(e, d, contact, waited, speed, oc, out _);
            Eq(R(0.4, 4), OutageLogic.Recovery.None, "0.4 m / 4 deg: nothing");
            Eq(R(0.49, 4.9), OutageLogic.Recovery.None, "just below both");
            Eq(R(0.4, 6), OutageLogic.Recovery.Converge, "0.4 m but 6 deg: converge");
            Eq(R(0.5, 1), OutageLogic.Recovery.Converge, "0.5 m: converge");
            Eq(R(15, 1), OutageLogic.Recovery.Converge, "15 m: converge");
            Eq(R(15.1, 1), OutageLogic.Recovery.Snap, "15.1 m: snap");
            Eq(R(30, 1, contact: true), OutageLogic.Recovery.WaitContact, "30 m in contact: wait");
            Eq(R(30, 1, contact: true, waited: 1.9), OutageLogic.Recovery.WaitContact, "still waiting at 1.9 s");
            Eq(R(30, 1, contact: true, waited: 2.0), OutageLogic.Recovery.Converge, "contact did not end in 2 s: converge, never snap");
            Eq(R(50, 1), OutageLogic.Recovery.Snap, "50 m: snap");
            Eq(R(50.1, 1), OutageLogic.Recovery.Teleport, "50.1 m: teleport, leave it");
            Eq(R(2, 120, speed: 10), OutageLogic.Recovery.Converge, "flipped while moving: converge");
            OutageLogic.Decide(2, 120, false, 0, 10, oc, out var tf); Eq(tf, 0.5, "flipped: 0.5 s");
            Eq(R(2, 120, speed: 1), OutageLogic.Recovery.Snap, "flipped and slow: snap");
            Eq(R(2, 120, contact: true, speed: 1), OutageLogic.Recovery.Converge, "flipped, slow, contact: converge");
            OutageLogic.Decide(10, 1, false, 0, 10, oc, out var t10); Eq(Math.Round(t10, 3), 1.1, "converge time passed out");
            Eq(OutageLogic.KeepUpright(0.5, 0.9, -0.8), true, "host upright, client upside down, 0.5 m: keep");
            Eq(OutageLogic.KeepUpright(2, 0.9, -0.8), false, "2 m apart: not kept");
            Eq(OutageLogic.KeepUpright(0.5, -0.9, -0.8), false, "both upside down: not kept");
            // convergence factors: velocity first, position later, ~97 % at T
            var (vr0, p0) = OutageLogic.ConvergeFactors(0, 1.0, 1 / 60.0);
            Eq(vr0 > 0 && p0 == 0, true, "start: velocity/rotation only");
            double err = 10; double tt = 0; while (tt < 1.0) { var (_, kp) = OutageLogic.ConvergeFactors(tt, 1.0, 1 / 60.0); err *= 1 - kp; tt += 1 / 60.0; }
            Eq(err < 0.6 && err > 0.05, true, $"10 m -> {err:0.00} m after T (soft, no jump)");
            double maxStep = 0; err = 10; tt = 0; while (tt < 1.0) { var (_, kp) = OutageLogic.ConvergeFactors(tt, 1.0, 1 / 60.0); maxStep = Math.Max(maxStep, err * kp); err *= 1 - kp; tt += 1 / 60.0; }
            Eq(maxStep < 0.6, true, $"largest per-frame position step {maxStep:0.00} m (60 fps)");
            Eq(OutageLogic.ShowCarLabel(1.49, oc), false, "car label not before 1.5 s");
            Eq(OutageLogic.ShowCarLabel(1.5, oc), true, "car label from 1.5 s");
            // status packet
            var items = new System.Collections.Generic.List<(byte, double)> { (2, 1.5), (3, 47.25) };
            var sb = OutageLogic.EncodeStatus(items);
            var back = new System.Collections.Generic.List<(byte slot, double noLinkS)>();
            Eq(OutageLogic.TryDecodeStatus(sb, back) && back.Count == 2 && back[0].slot == 2 && back[0].noLinkS == 1.5 && back[1].slot == 3 && Math.Abs(back[1].noLinkS - 47.25) < 0.06, true, "status codec");
            Eq(OutageLogic.TryDecodeStatus(OutageLogic.EncodeStatus(new System.Collections.Generic.List<(byte, double)>()), back) && back.Count == 0, true, "empty status");
            sb[4] = 5; Eq(OutageLogic.TryDecodeStatus(sb, back), false, "bad count rejected");
            Eq(OutageLogic.TryDecodeStatus(new byte[] { 1, 2, 3 }, back), false, "short rejected");
            Eq(CarSyncLogic.TryDecodePing(OutageLogic.EncodeStatus(items), out _, out _, out _), false, "status is not a ping");
        }

        // ---- 0.1.7 host -> client "take my pose" ----
        {
            var acc = OutageLogic.EncodeAccept(3, 65535);
            Eq(OutageLogic.TryDecodeAccept(acc, out var asl, out var act) && asl == 3 && act == 65535, true, "accept round trip");
            Eq(OutageLogic.TryDecodeAccept(OutageLogic.EncodeStatus(new (byte, double)[] { (2, 1.5) }), out _, out _), false, "status is not accept");
            Eq(OutageLogic.TryDecodeAccept(new byte[7], out _, out _), false, "zeros rejected");
            Eq(OutageLogic.TryDecodeStatus(acc, new System.Collections.Generic.List<(byte, double)>()), false, "accept is not status");
        }

        // ---- 0.1.7 LAN lobby discovery ----
        {
            var rq = DiscoveryLogic.Request(4242, "0.1.7");
            Eq(rq, "OTHLAN?DISC1 nonce=4242 mod=0.1.7", "disc request");
            Eq(DiscoveryLogic.TryParseRequest(rq, out var dn, out var dm) && dn == 4242 && dm == "0.1.7", true, "disc request parse");
            Eq(DiscoveryLogic.TryParseRequest("OTHLAN?1", out _, out _), false, "info request is not discovery");
            Eq(DiscoveryLogic.TryParseRequest(new string('x', 300), out _, out _), false, "oversized request rejected");
            var lob = new DiscoveryLogic.Lobby { Id = "abc", Name = "Серёжа\nPC=1", Cur = 2, Max = 4, Mod = "0.1.7", Build = "26062472", GaHash = "57da", Area = "R2A1_Desert", CarSync = 1, Port = 27015, Nonce = 4242 };
            var rep = DiscoveryLogic.Reply(lob);
            Eq(rep.Contains("name=Серёжа\\nPC=1\n") || rep.Contains("name=Серёжа"), true, "reply has the name");
            Eq(DiscoveryLogic.TryParseReply(rep, out var pl) && pl.Id == "abc" && pl.Cur == 2 && pl.Max == 4 && pl.Mod == "0.1.7" && pl.Area == "R2A1_Desert" && pl.CarSync == 1 && pl.Nonce == 4242 && pl.Port == 27015, true, "reply round trip");
            var nl = new DiscoveryLogic.Lobby { Name = "evil\nmax=99", Max = 4 };
            Eq(DiscoveryLogic.TryParseReply(DiscoveryLogic.Reply(nl), out var pn) && pn.Max == 4 && pn.Name == "evilmax=99", true, "newline in name cannot inject a field");
            Eq(DiscoveryLogic.Clean(new string('a', 100), 32).Length, 32, "name capped at 32");
            Eq(DiscoveryLogic.TryParseReply("OTHLAN 1\nmod=0.1.6\n", out _), false, "old info reply is not a lobby");
            Eq(DiscoveryLogic.TryParseReply("OTHLAN DISC1\nmax=1000\n", out _), false, "absurd max rejected");
            Eq(DiscoveryLogic.TryParseReply("OTHLAN DISC1\nport=99999\n", out var pp) && pp.Port == 27015, true, "bad port -> default");
            Eq(DiscoveryLogic.TryParseReply("OTHLAN DISC1\n" + new string('x', 2000), out _), false, "oversized reply rejected");
            Eq(new DiscoveryLogic.Lobby { Cur = 4, Max = 4 }.Full, true, "4/4 full");
            Eq(new DiscoveryLogic.Lobby { Cur = 3, Max = 4 }.Full, false, "3/4 not full");
            Eq(new DiscoveryLogic.Lobby { Cur = 2, Max = 2 }.Full, true, "2/2 full");
            // compatibility
            var ok = new DiscoveryLogic.Lobby { Mod = "0.1.7", Build = "26062472", GaHash = "57da" };
            Eq(DiscoveryLogic.Check(ok, "0.1.7", "26062472", "57da"), DiscoveryLogic.Compat.Ok, "compatible");
            Eq(DiscoveryLogic.Check(ok, "0.1.6", "26062472", "57da"), DiscoveryLogic.Compat.OtherMod, "other mod version");
            Eq(DiscoveryLogic.Check(ok, "0.1.7", "26062473", "ffff"), DiscoveryLogic.Compat.OtherGame, "other game build");
            // address preference and dedup by host id
            Eq(DiscoveryLogic.AddressRank("26.1.2.3") > DiscoveryLogic.AddressRank("192.168.1.5"), true, "radmin preferred over LAN");
            Eq(DiscoveryLogic.AddressRank("192.168.1.5") > DiscoveryLogic.AddressRank("127.0.0.1"), true, "LAN preferred over loopback");
            var list = new DiscoveryLogic.LobbyList();
            list.Upsert(new DiscoveryLogic.Lobby { Id = "h1", Name = "B" }, "127.0.0.1", 1, 0, "loopback");
            list.Upsert(new DiscoveryLogic.Lobby { Id = "h1", Name = "B" }, "26.1.2.3", 3, 0.1, "radmin-bcast");
            list.Upsert(new DiscoveryLogic.Lobby { Id = "h1", Name = "B" }, "127.0.0.1", 1, 0.2, "loopback");
            Eq(list.Count, 1, "one host, three addresses -> one row");
            Eq(list.Sorted()[0].Ip, "26.1.2.3", "best address kept");
            list.Upsert(new DiscoveryLogic.Lobby { Id = "h2", Name = "A" }, "26.9.9.9", 50, 1.0, "radmin-bcast");
            Eq(list.Sorted()[0].Name, "A", "sorted by name");
            list.Upsert(new DiscoveryLogic.Lobby { Id = "h2", Name = "A" }, "26.9.9.9", 100, 2.0, "radmin-bcast");
            Eq(list.Sorted()[0].PingMs > 50 && list.Sorted()[0].PingMs < 100, true, "ping smoothed");
            list.Expire(6.25);
            Eq(list.Count, 1, "row without answer for > 6 s disappears");
            list.Expire(8.5);
            Eq(list.Count, 0, "all gone");
            // misc
            Eq(string.Join(".", DiscoveryLogic.DirectedBroadcast(new byte[] { 26, 10, 20, 30 }, new byte[] { 255, 0, 0, 0 })), "26.255.255.255", "radmin directed broadcast");
            Eq(string.Join(".", DiscoveryLogic.DirectedBroadcast(new byte[] { 192, 168, 1, 7 }, new byte[] { 255, 255, 255, 0 })), "192.168.1.255", "LAN directed broadcast");
            Eq(DiscoveryLogic.TryParseManual("26.1.2.3:27015", out var mi, out var mp) && mi == "26.1.2.3" && mp == 27015, true, "manual ip:port");
            Eq(DiscoveryLogic.TryParseManual("26.1.2.3", out _, out var mp2) && mp2 == 27015, true, "manual ip -> default port");
            Eq(DiscoveryLogic.TryParseManual("26.1.2", out _, out _), false, "bad ip");
            Eq(DiscoveryLogic.TryParseManual("26.1.2.3:abc", out _, out _), false, "bad port");
            Eq(DiscoveryLogic.TryParseManual("host.example:27015", out _, out _), false, "names not accepted");
        }

        I18n();
        NetcodeV2();
        Updates();
        Smooth();
        Console.WriteLine($"{total - fails}/{total} checks passed");
        return fails == 0 ? 0 : 1;
    }
}
