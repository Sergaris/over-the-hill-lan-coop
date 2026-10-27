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
        // long stall: extrapolate then Hold; after HoldMaxSeconds snap to Fusion (T1.2)
        s = new RemoteSmoother(); fx = 0;
        for (int k = 0; k < 60; k++) { fx += 10 * dt; s.Step(fx, 0, 0, dt, c, false); }
        double last = 0; int snaps0 = s.Snaps;
        for (int k = 0; k < 40; k++) last = s.Step(fx, 0, 0, dt, c, true).x;
        Eq(last - fx <= 10 * c.ExtrapolateSeconds + 0.2, true, $"smooth: stall bounded before hold timeout ({last - fx:0.00} m ahead)");
        for (int k = 0; k < 120; k++) last = s.Step(fx, 0, 0, dt, c, true).x;
        Eq(s.Snaps > snaps0, true, "smooth: hold timeout counted as snap");
        Eq(Math.Abs(last - fx) < 0.05, true, $"smooth: after hold snap at fusion ({last - fx:0.00})");
        // T1.2 fall then stop without starve: no dive underground
        s = new RemoteSmoother(); c = new RemoteSmoother.Cfg(); double y = 10, ground = 0;
        for (int k = 0; k < 30; k++) { y -= 0.15; ground = y; var d = s.Step(0, y, 0, dt, c, false); Eq(d.y >= ground - c.MaxBelowMeters - 1e-9, true, "smooth: fall floor"); }
        for (int k = 0; k < 200; k++)
        {
            var d = s.Step(0, ground, 0, dt, c, false);
            Eq(d.y >= ground - c.MaxBelowMeters - 1e-9, true, "smooth: stop floor");
            if (k == 19) Eq(s.OffsetLength < 0.01, true, $"smooth: stop offset gone ({s.OffsetLength:0.000})");
        }
        Eq(s.Last, RemoteSmoother.State.Follow, "smooth: stopped car Follow");
        // T1.2 starve after fall: Y not extrapolated down
        s = new RemoteSmoother(); y = 10;
        for (int k = 0; k < 30; k++) { y -= 0.15; s.Step(0, y, 0, dt, c, false); }
        ground = y; double minY = double.MaxValue;
        for (int k = 0; k < 60; k++) { var d = s.Step(0, ground, 0, dt, c, true); if (d.y < minY) minY = d.y; }
        Eq(minY >= ground - c.MaxBelowMeters - 1e-9, true, $"smooth: starve Y floor ({minY:0.00} vs {ground:0.00})");
        s = new RemoteSmoother();
        for (int k = 0; k < 600; k++) s.Step(1, 2, 3, dt, c, false);
        Eq(s.Last, RemoteSmoother.State.Follow, "smooth: standing Follow");
        Eq(s.OffsetLength < 1e-6, true, "smooth: standing offset 0");
        Eq(HostFollowLogic.ServoSpeed(1.0, 0.25, 6, 0.5, 0), 4.0, "servo: 1 m / 0.25 s = 4 m/s");
        Eq(HostFollowLogic.ServoSpeed(10.0, 0.25, 6, 0.5, 10), 11.0, "servo: capped at 6 + 0.5 v");
        Eq(HostFollowLogic.ServoSpeed(0, 0.25, 6, 0.5, 10), 0.0, "servo: no error, no correction");
        Eq(RemoteSmoother.TauFor(0.1, c), c.Tau, "smooth: small error slow decay");
        Eq(RemoteSmoother.TauFor(5, c), c.TauLarge, "smooth: big error fast decay");
        Eq(RemoteSmoother.RotKeep(90, 0.1, 0.2, 180) > 0.79 && RemoteSmoother.RotKeep(90, 0.1, 0.2, 180) < 0.81, true, "smooth: rotation decay capped 180 deg/s");
        // 0.2.0 T3.5: DecayOffset is the single owner of offset decay (RemoteCarView source blend).
        {
            double ox = 1, oy = 0, oz = 0;
            var bc = new RemoteSmoother.Cfg { Tau = 0.1, TauLarge = 0.1, MaxCorrection = 100, SpeedFactor = 0, SmallMeters = 0, LargeMeters = 1 };
            RemoteSmoother.DecayOffset(ref ox, ref oy, ref oz, 0.1, bc);
            Eq(ox > 0.30 && ox < 0.45 && oy == 0 && oz == 0, true, "smooth: DecayOffset ~e^-1 in one tau");
            ox = 0; RemoteSmoother.DecayOffset(ref ox, ref oy, ref oz, 0.1, bc);
            Eq(ox == 0 && oy == 0 && oz == 0, true, "smooth: DecayOffset zero stays zero");
        }
    }

    // ---------- 0.1.8 update check (UpdateLogic) ----------
    static void Updates()
    {
        Eq(UpdateLogic.TryParseVersion("0.1.8", out var v) && v == (0, 1, 8, 0), true, "ver 0.1.8");
        Eq(UpdateLogic.TryParseVersion("v0.1.10", out var v2) && v2 == (0, 1, 10, 0), true, "ver v-prefix, two digits");
        Eq(UpdateLogic.TryParseVersion("0.1.9-beta", out _), false, "prerelease suffix rejected");
        Eq(UpdateLogic.TryParseVersion("0.1", out _), false, "two parts rejected");
        Eq(UpdateLogic.TryParseVersion("0.1.x", out _), false, "non-digit rejected");
        Eq(UpdateLogic.TryParseVersion("", out _), false, "empty rejected");
        Eq(UpdateLogic.TryParseVersion("1.2.3.4", out var v4) && v4 == (1, 2, 3, 4), true, "four parts accepted (0.1.8.1)");
        Eq(UpdateLogic.TryParseVersion("v0.1.8.1", out var v5) && v5 == (0, 1, 8, 1), true, "four parts with v");
        Eq(UpdateLogic.TryParseVersion("0.1.8.1", out _), true, "running 0.1.8.1 (Plugin.Version) parses");
        Eq(UpdateLogic.TryParseVersion("1.2.3.4.5", out _), false, "five parts rejected");
        Eq(UpdateLogic.TryParseVersion("0.1.8.", out _), false, "empty 4th part rejected");
        Eq(UpdateLogic.TryParseVersion("0.1.8.1-beta", out _), false, "4-part prerelease suffix rejected");
        Eq(UpdateLogic.Compare("0.1.8", "0.1.8.1"), -1, "0.1.8 < 0.1.8.1");
        Eq(UpdateLogic.Compare("0.1.8.1", "0.1.9"), -1, "0.1.8.1 < 0.1.9");
        Eq(UpdateLogic.Compare("v0.1.8.1", "0.1.8.1"), 0, "0.1.8.1 == v0.1.8.1");
        Eq(UpdateLogic.Compare("0.1.8.0", "0.1.8"), 0, "0.1.8.0 == 0.1.8");
        Eq(UpdateLogic.Compare("0.1.8.10", "0.1.8.9"), 1, "4th part numeric");
        Eq(UpdateLogic.IsNewer("0.1.8.1", "0.1.8"), true, "0.1.8.1 newer than 0.1.8");
        Eq(UpdateLogic.IsNewer("0.1.8", "0.1.8.1"), false, "0.1.8 not newer than 0.1.8.1");
        Eq(UpdateLogic.IsNewer("0.1.9", "0.1.8.1"), true, "0.1.9 newer than 0.1.8.1");
        Eq(UpdateLogic.ShouldOffer("0.1.8.1", "0.1.8", "0.1.8.1"), false, "skipped 0.1.8.1 not offered");
        Eq(UpdateLogic.ShouldOffer("0.1.9", "0.1.8.1", "0.1.8.1"), true, "newer than skipped offered");
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
        var rel4 = UpdateLogic.ParseRelease(J("v0.1.9").Replace("0.1.9", "0.1.8.1"), out var why4);
        Eq(rel4 != null && rel4.Version == "0.1.8.1" && rel4.ZipName == "OTH-LAN-0.1.8.1.zip", true, "4-part release v0.1.8.1 parsed " + why4);
        Eq(rel4 != null && UpdateLogic.ShouldOffer(rel4.Version, "0.1.8.1", ""), false, "v0.1.8.1 not offered to 0.1.8.1");
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
        Eq(OwnCarLogic.CountOwnCorrection(false, false, false), false, "own corr: pre-phase2 skipped");
        Eq(OwnCarLogic.CountOwnCorrection(true, true, false), false, "own corr: Restore() invisible — skip");
        Eq(OwnCarLogic.CountOwnCorrection(true, false, true), false, "own corr: spawn transition sample skipped");
        Eq(OwnCarLogic.CountOwnCorrection(true, false, false), true, "own corr: accept-window / kept host pose counted");
        Eq(OwnCarLogic.SkipCheapRestore(0.01, 0.2, 0.05, 1.0), true, "own: 1 cm / 0.2 deg -> skip VPP restore");
        Eq(OwnCarLogic.SkipCheapRestore(0.10, 0.2, 0.05, 1.0), false, "own: 10 cm -> full restore");
        Eq(OwnCarLogic.SkipCheapRestore(0.01, 5.0, 0.05, 1.0), false, "own: 5 deg -> full restore");
        Eq(OwnCarLogic.AllowLocalPhysicsStep(0.016, 0.028), true, "ownlocal: 16 ms frame allows LocalStep");
        Eq(OwnCarLogic.AllowLocalPhysicsStep(0.035, 0.028), false, "ownlocal: 35 ms frame skips LocalStep");

        Eq(HitchKindLogic.Classify(0, 0), HitchKindLogic.Kind.None, "hitch: zero hitch -> none");
        Eq(HitchKindLogic.Classify(1.0, 0.6), HitchKindLogic.Kind.SimJump, "hitch: sim ≥½ hitch -> sim");
        Eq(HitchKindLogic.Classify(1.0, 0.5), HitchKindLogic.Kind.SimJump, "hitch: sim ==½ hitch -> sim");
        Eq(HitchKindLogic.Classify(1.0, 0.0), HitchKindLogic.Kind.FrameSkip, "hitch: sim 0 -> frame");
        Eq(HitchKindLogic.Classify(1.0, 0.2), HitchKindLogic.Kind.FrameSkip, "hitch: sim ≤¼ hitch -> frame");
        Eq(HitchKindLogic.Classify(1.0, 0.3), HitchKindLogic.Kind.Mixed, "hitch: between ¼ and ½ -> mix");
        Eq(HitchKindLogic.Tag(HitchKindLogic.Kind.SimJump), "sim", "hitch tag: sim");
        Eq(HitchKindLogic.Tag(HitchKindLogic.Kind.FrameSkip), "frame", "hitch tag: frame");
        Eq(HitchKindLogic.NearReset(0.04), true, "hitch: 40 ms after reset is near");
        Eq(HitchKindLogic.NearReset(0.20), false, "hitch: 200 ms after reset is far");
        Eq(HitchKindLogic.NearReset(-1), false, "hitch: unknown sinceReset not near");
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
        Eq(CarSyncLogic.Decide(M.High, 3.5, 5, 0.1, 0.05, c, softActive: true), A.SoftSnap, "high: desync soft-snaps inside soft window (0.1.9 U5)");
        Eq(CarSyncLogic.Decide(M.High, 13, 5, 0.1, 0.05, c, softActive: true), A.Snap, "high: huge desync still hard-snaps");
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
        Eq(CarSyncLogic.Decide(M.High, 3.5, 5, 0.2, 0.05, c), A.SoftSnap, "high: desync distance soft (0.1.9 U5)");
        Eq(CarSyncLogic.Decide(M.High, 0.5, 30, 0.2, 0.05, c), A.SoftSnap, "high: desync angle soft (0.1.9 U5)");
        Eq(CarSyncLogic.Decide(M.High, 13, 5, 0.2, 0.05, c), A.Snap, "high: desync above SoftDesyncHardMeters");
        Eq(CarSyncLogic.Decide(M.High, 3.5, 5, 0.2, 0.05, new CarSyncLogic.Cfg { CheckpointSoftSeconds = 0 }), A.Snap, "high: soft=0 -> hard desync");
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
            var ok = new DiscoveryLogic.Lobby { Mod = "0.1.7", Build = "26062472", GaHash = "57da", Fusion = "2.1.2.0" };
            Eq(DiscoveryLogic.Check(ok, "0.1.7", "26062472", "57da", "2.1.2.0"), DiscoveryLogic.Compat.Ok, "compatible");
            Eq(DiscoveryLogic.Check(ok, "0.1.6", "26062472", "57da", "2.1.2.0"), DiscoveryLogic.Compat.OtherMod, "other mod version");
            Eq(DiscoveryLogic.Check(ok, "0.1.7", "26062473", "ffff", null), DiscoveryLogic.Compat.OtherGame, "other game build, no Fusion fallback");
            var steam = new DiscoveryLogic.Lobby { Mod = "0.1.7", Build = "26062472", GaHash = "57daaaaa", Fusion = "2.1.2.0" };
            Eq(DiscoveryLogic.Check(steam, "0.1.7", "ga:025e1040", "025e1040bbbb", "2.1.2.0"), DiscoveryLogic.Compat.Ok, "Steam↔third-party same Fusion");
            Eq(DiscoveryLogic.Check(steam, "0.1.7", "ga:025e1040", "025e1040bbbb", "2.1.3.0"), DiscoveryLogic.Compat.OtherGame, "Steam↔third-party other Fusion");
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

        Suspend();
        I18n();
        NetcodeV2();
        Updates();
        Smooth();
        Playback();
        ModGate();
        Hang();
        SessionLogs();
        DiagRetry();
        FrameCost();
        PoseSpeed();
        WheelSpin();
        UiModal();
        WinchKind();
        WinchReleaseClear();
        ParkLogicTests();
        CarWireTests();
        StatePlaybackTests();
        RelayLogicTests();
        StreamEdge();
        SessionWire();
        ReleaseGate022();
        InteractEdge();
        SessionNotifyEdges();
        ReleaseGate023();
        WireEdges023();
        SyncProbe();
        StreamSyncInvariant();
        AcceptMetrics();
        WheelVisual();
        Console.WriteLine($"{total - fails}/{total} checks passed");
        return fails == 0 ? 0 : 1;
    }

    static void RelayLogicTests()
    {
        Eq(RelayLogic.Filter(2, true, true), RelayLogic.Decision.Accept, "relay: match accept");
        Eq(RelayLogic.Filter(2, true, false), RelayLogic.Decision.DropWrongSlot, "relay: wrong ep");
        Eq(RelayLogic.Filter(2, false, false), RelayLogic.Decision.DropUnknownPeer, "relay: unknown");
        Eq(RelayLogic.Filter(2, false, true), RelayLogic.Decision.DropUnknownPeer, "relay: unknown even if fromMatches");
        Eq(RelayLogic.Filter(CarSyncLogic.HostSlot, true, true), RelayLogic.Decision.DropWrongSlot, "relay: host slot over UDP");
    }

    // ---------- 0.2.0 T3.4 StatePlayback ----------
    static void StatePlaybackTests()
    {
        var pb = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0.05, MaxDelayS = 0.15, DelayEwma = 0.5, SendHz = 30 });
        // steady 30 Hz, 20 m/s, ± ±20 ms on arrival
        var rng = new Random(1);
        for (int i = 0; i < 90; i++)
        {
            double send = i / 30.0;
            double arrive = send + 0.08 + (rng.NextDouble() - 0.5) * 0.04;
            pb.Push(Mk(i, send, 20 * (float)send, 0, 20, 0), arrive);
        }
        double maxErr = 0;
        for (int k = 0; k < 30; k++)
        {
            double now = 2.0 + k / 60.0;
            var r = pb.SampleAt(now);
            if (!r.Ok) continue;
            var play = now - pb.OffsetS - r.DelayS;
            var expect = 20 * play;
            maxErr = Math.Max(maxErr, Math.Abs(r.Px - expect));
        }
        Eq(maxErr < 0.05, true, $"statepb: steady Hermite err {maxErr:0.000} m");

        // 30% loss
        pb = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0.05, MaxDelayS = 0.2, DelayEwma = 0.3, SendHz = 30 });
        for (int i = 0; i < 60; i++)
        {
            if (i % 10 < 3) continue;
            double send = i / 30.0;
            pb.Push(Mk(i, send, 10 * (float)send, 0, 10, 0), send + 0.05);
        }
        float prev = float.NaN; double maxJump = 0; const double dt = 1.0 / 60.0;
        for (int k = 0; k < 40; k++)
        {
            var r = pb.SampleAt(1.0 + k * dt);
            if (!r.Ok) continue;
            if (!float.IsNaN(prev))
            {
                var step = Math.Abs(r.Px - prev);
                var expect = 10 * dt; // 10 m/s
                maxJump = Math.Max(maxJump, Math.Abs(step - expect));
            }
            prev = r.Px;
        }
        Eq(maxJump < 0.10, true, $"statepb: loss jump excess {maxJump:0.000} m");

        // break stream → extrap then stale
        pb = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0.05, MaxDelayS = 0.1, DelayEwma = 1, SendHz = 30, MaxExtrapS = 0.25 });
        for (int i = 0; i < 20; i++)
        {
            double send = i / 30.0;
            pb.Push(Mk(i, send, 5 * (float)send, 1, 5, -2), send + 0.05);
        }
        var lastSend = 19 / 30.0;
        var mid = pb.SampleAt(lastSend + 0.05 + 0.05 + 0.15); // within extrap
        Eq(mid.Ok && mid.Extrapolated, true, "statepb: extrap after gap");
        Eq(Math.Abs(mid.Vx - 5) < 1e-3f, true, $"statepb: extrap keeps sample vel ({mid.Vx})");
        var held = pb.SampleAt(lastSend + 0.05 + 0.05 + 0.5);
        Eq(held.Ok && held.Stale && !held.Extrapolated, true, "statepb: stale hold after MaxExtrap");
        Eq(held.Vx == 0 && held.Vy == 0 && held.Vz == 0, true, $"statepb: stale vel 0 (got {held.Vx},{held.Vy},{held.Vz})");
        Eq(held.Wx == 0 && held.Wy == 0 && held.Wz == 0, true, "statepb: stale angVel 0");
        Eq(Math.Abs(held.Px - 5 * (float)lastSend) < 1e-3f, true, $"statepb: stale hold pose ({held.Px})");

        // fall then stop: y floor
        pb = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0.02, MaxDelayS = 0.05, DelayEwma = 1, MaxExtrapS = 0.25, MaxBelowNewestM = 0.5 });
        float y = 10;
        for (int i = 0; i < 30; i++)
        {
            y -= 0.2f;
            double send = i * 0.02;
            pb.Push(Mk(i, send, 0, y, 0, -10, airborne: true), send + 0.02);
        }
        var ground = y;
        for (int i = 30; i < 50; i++)
        {
            double send = i * 0.02;
            pb.Push(Mk(i, send, 0, ground, 0, 0, airborne: false), send + 0.02);
        }
        double minY = double.MaxValue;
        for (int k = 0; k < 40; k++)
        {
            var r = pb.SampleAt(1.0 + k / 60.0);
            if (r.Ok && r.Py < minY) minY = r.Py;
        }
        Eq(minY >= ground - 0.05 - 1e-3, true, $"statepb: y floor {minY:0.00} vs {ground:0.00}");

        // epoch snap once
        pb = new StatePlayback();
        pb.Push(Mk(1, 0.1, 0, 0, 0, 0, epoch: 1), 0.15);
        pb.Push(Mk(2, 0.2, 1, 0, 0, 0, epoch: 1), 0.25);
        pb.Push(Mk(3, 0.3, 100, 0, 0, 0, epoch: 2), 0.35);
        var snap = pb.SampleAt(0.40);
        Eq(snap.Ok && snap.Snap && Math.Abs(snap.Px - 100) < 1e-3, true, "statepb: epoch snap");
        var after = pb.SampleAt(0.41);
        Eq(after.Ok && !after.Snap, true, "statepb: snap only once");

        // dupe seq
        pb = new StatePlayback();
        pb.Push(Mk(5, 0.1, 0, 0, 0, 0), 0.12);
        pb.Push(Mk(5, 0.1, 9, 0, 0, 0), 0.13);
        Eq(pb.Dupes, 1, "statepb: dupe counted");
        Eq(pb.Count, 1, "statepb: dupe not stored");

        // delayOverride 0 for host follower
        pb = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0.1, MaxDelayS = 0.2, DelayEwma = 0 });
        pb.Push(Mk(1, 1.0, 10, 0, 0, 0), 1.05);
        pb.Push(Mk(2, 1.1, 12, 0, 0, 0), 1.15);
        var host = pb.SampleAt(1.15, 0);
        Eq(host.Ok && Math.Abs(host.Px - 12) < 0.05, true, $"statepb: delayOverride0 near newest ({host.Px})");
    }

    static StatePlayback.Sample Mk(int seq, double send, float px, float py, float vx, float vy, bool airborne = false, byte epoch = 1)
        => new()
        {
            Seq = (uint)seq, SendTime = send, Epoch = epoch,
            Px = px, Py = py, Pz = 0, Qw = 1,
            Vx = vx, Vy = vy,
            Flags = airborne ? CarWire.FlagAirborne : (byte)0,
            WheelCount = 0,
        };

    // ---------- 0.2.0 T3.1 OTC3 wire ----------
    static void CarWireTests()
    {
        Eq(CarWire.Magic, 0x3343544Fu, "carwire: magic OTC3");
        Eq(CarWire.StateBytes(4), 92, "carwire: 4-wheel state size");
        Eq(CarWire.StateBytes(6), 104, "carwire: 6-wheel state size");
        Eq(CarWire.StateBytes(7), -1, "carwire: >6 wheels invalid");

        var s = new CarWire.CarStateSample
        {
            Seq = 42, SendTime = 123.456,
            Px = 1, Py = 2, Pz = 3, Qx = 0, Qy = 0, Qz = 0, Qw = 1,
            Vx = 10, Vy = 0, Vz = -2, Wx = 0.1f, Wy = 0.2f, Wz = 0,
            Flags = (byte)(CarWire.FlagHeadlights | CarWire.FlagEngineOn),
            SteerDeg = 45f, WheelCount = 4,
        };
        s.SetWheel(0, new CarWire.WheelSample { RollDeg = 90, SpinRadS = 12.5f, SuspM = 0.05f });
        s.SetWheel(1, new CarWire.WheelSample { RollDeg = 180, SpinRadS = -12.5f, SuspM = -0.02f });
        s.SetWheel(2, new CarWire.WheelSample { RollDeg = 270, SpinRadS = 0, SuspM = 0 });
        s.SetWheel(3, new CarWire.WheelSample { RollDeg = 0, SpinRadS = 1600f, SuspM = 0.1f });
        var p = new CarWire.CarStatePacket { Slot = 2, Epoch = 7, Count = 1 };
        p.SetState(0, s);
        var buf = new byte[CarWire.MaxPacketBytes];
        var n = CarWire.Encode(p, buf);
        Eq(n > 0 && n == CarWire.HeaderSize + 92, true, $"carwire: encode size ({n})");
        Eq(CarWire.TryDecode(buf.AsSpan(0, n), out var d), true, "carwire: decode ok");
        Eq(d.Slot, (byte)2, "carwire: slot");
        Eq(d.Epoch, (byte)7, "carwire: epoch");
        Eq(d.Count, (byte)1, "carwire: count");
        var r = d.S0;
        Eq(r.Seq, 42u, "carwire: seq");
        Eq(Math.Abs(r.SendTime - 123.456) < 1e-9, true, "carwire: sendTime");
        Eq(Math.Abs(r.Px - 1) < 1e-5 && Math.Abs(r.Py - 2) < 1e-5 && Math.Abs(r.Pz - 3) < 1e-5, true, "carwire: pos");
        Eq(Math.Abs(r.SteerDeg - 45f) < 0.02f, true, $"carwire: steer ({r.SteerDeg})");
        Eq(r.Flags, (byte)(CarWire.FlagHeadlights | CarWire.FlagEngineOn), "carwire: flags");
        // T3.6 light bits survive the wire (FlagHandbrake reserved, unused by observer)
        var lightFlags = (byte)(CarWire.FlagBrake | CarWire.FlagReverse | CarWire.FlagAirborne | CarWire.FlagEngineOn);
        s.Flags = lightFlags;
        p.SetState(0, s);
        n = CarWire.Encode(p, buf);
        Eq(CarWire.TryDecode(buf.AsSpan(0, n), out d) && d.S0.Flags == lightFlags, true, "carwire: brake+reverse+airborne flags");
        Eq((d.S0.Flags & CarWire.FlagReverse) != 0, true, "carwire: FlagReverse bit");
        Eq((d.S0.Flags & CarWire.FlagBrake) != 0, true, "carwire: FlagBrake bit");
        Eq(r.WheelCount, (byte)4, "carwire: wheels");
        Eq(Math.Abs(r.W0.SpinRadS - 12.5f) < 0.06f, true, $"carwire: spin0 ({r.W0.SpinRadS})");
        Eq(Math.Abs(r.W3.SpinRadS - 1600f) < 0.1f, true, $"carwire: spin clip edge ({r.W3.SpinRadS})");

        // steer ±45, spin clip beyond ±1600, 6 wheels, 3 states
        s.SteerDeg = -45f; s.WheelCount = 6;
        s.SetWheel(3, new CarWire.WheelSample { RollDeg = 10, SpinRadS = 2000f, SuspM = 0 }); // clips to 1600
        s.SetWheel(4, new CarWire.WheelSample { RollDeg = 20, SpinRadS = -2000f, SuspM = 0 });
        s.SetWheel(5, new CarWire.WheelSample { RollDeg = 359.9f, SpinRadS = 0, SuspM = 0.2f });
        var p3 = new CarWire.CarStatePacket { Slot = 1, Epoch = 1, Count = 3 };
        p3.SetState(0, s);
        var s1 = s; s1.Seq = 41; s1.Px = 0.5f; p3.SetState(1, s1);
        var s2 = s; s2.Seq = 40; s2.Px = 0; p3.SetState(2, s2);
        n = CarWire.Encode(p3, buf);
        Eq(n == CarWire.HeaderSize + 3 * 104, true, $"carwire: 3×6-wheel size ({n})");
        Eq(n < 1200, true, "carwire: under MTU budget");
        Eq(CarWire.TryDecode(buf.AsSpan(0, n), out d) && d.Count == 3 && d.S0.Seq == 42 && d.S2.Seq == 40, true, "carwire: 3-state roundtrip");
        Eq(Math.Abs(d.S0.SteerDeg - (-45f)) < 0.02f, true, "carwire: steer -45");
        Eq(Math.Abs(d.S0.W3.SpinRadS - 1600f) < 0.1f, true, "carwire: spin +clip");
        Eq(Math.Abs(d.S0.W4.SpinRadS - (-1600f)) < 0.1f, true, "carwire: spin -clip");

        Eq(CarWire.TryDecode(buf.AsSpan(0, n / 2), out _), false, "carwire: truncated rejected");
        buf[0] ^= 1;
        Eq(CarWire.TryDecode(buf.AsSpan(0, n), out _), false, "carwire: bad magic rejected");
        buf[0] ^= 1;
        var nanP = p; nanP.S0.Px = float.NaN;
        Eq(CarWire.Encode(nanP, buf), -1, "carwire: NaN encode rejected");
        var badCount = p; badCount.Count = 0;
        Eq(CarWire.Encode(badCount, buf), -1, "carwire: count 0 rejected");
        Eq(CarWire.QuantSpin(2000f), CarWire.QuantSpin(1600f), "carwire: QuantSpin clips");
    }

    // ---------- 0.2.0 T1.4 winch world vs car ----------
    static void WinchKind()
    {
        Eq(WinchKindLogic.Classify(false, false, false), WinchKindLogic.Kind.None, "winch: none");
        Eq(WinchKindLogic.Classify(true, false, false), WinchKindLogic.Kind.World, "winch: tree/world");
        Eq(WinchKindLogic.Classify(true, true, false), WinchKindLogic.Kind.Car, "winch: own rope to car");
        Eq(WinchKindLogic.Classify(false, false, true), WinchKindLogic.Kind.Car, "winch: towed by other");
        Eq(WinchKindLogic.Classify(true, false, true), WinchKindLogic.Kind.Car, "winch: towed wins over world");
        Eq(WinchKindLogic.LogTag(WinchKindLogic.Kind.World), "world", "winch: tag world");
        Eq(WinchKindLogic.LogTag(WinchKindLogic.Kind.Car, 2), "car:2", "winch: tag car slot");
        Eq(WinchKindLogic.LogTag(WinchKindLogic.Kind.None), "none", "winch: tag none");
    }

    // ---------- 0.2.5.4 clear networked WinchPointsDisabled after detach ----------
    static void WinchReleaseClear()
    {
        Eq(WinchReleaseLogic.ShouldClearDisabled(true, false), true, "winch-release: disabled idle -> clear");
        Eq(WinchReleaseLogic.ShouldClearDisabled(true, true), false, "winch-release: disabled with rope -> keep");
        Eq(WinchReleaseLogic.ShouldClearDisabled(false, false), false, "winch-release: already enabled -> noop");
        Eq(WinchReleaseLogic.ShouldClearDisabled(false, true), false, "winch-release: attached enabled -> noop");
    }

    // ---------- 0.2.4 host parks idle driverless cars ----------
    static void ParkLogicTests()
    {
        const float wake = 60f;
        Eq(ParkLogic.IsStill(0.01f, 0.01f), true, "park: still");
        Eq(ParkLogic.IsStill(0.2f, 0.01f), false, "park: rolling is not still");
        Eq(ParkLogic.IsStill(0.01f, 0.2f), false, "park: spinning is not still");
        Eq(ParkLogic.WakeDistance(0f), ParkLogic.MinWakeM, "park: wake distance floor");
        Eq(ParkLogic.WakeDistance(50f), 50f + ParkLogic.ReachMarginM, "park: wake distance = reach + margin");
        Eq(ParkLogic.WakeDistance(float.NaN), ParkLogic.MinWakeM, "park: NaN reach -> floor");
        Eq(ParkLogic.Decide(false, ParkLogic.ParkAfterS, 500f, wake), ParkLogic.Act.Park, "park: still long enough, far -> park");
        Eq(ParkLogic.Decide(false, ParkLogic.ParkAfterS - 0.1f, 500f, wake), ParkLogic.Act.Keep, "park: not still long enough");
        Eq(ParkLogic.Decide(false, 10f, wake + 5f, wake), ParkLogic.Act.Keep, "park: inside hysteresis band stays awake");
        Eq(ParkLogic.Decide(false, 10f, float.PositiveInfinity, wake), ParkLogic.Act.Park, "park: no player cars -> park");
        Eq(ParkLogic.Decide(true, 0f, wake - 1f, wake), ParkLogic.Act.Wake, "park: player within reach wakes");
        Eq(ParkLogic.Decide(true, 0f, wake + 5f, wake), ParkLogic.Act.Keep, "park: parked in hysteresis band stays parked");
        Eq(ParkLogic.Decide(true, 0f, float.NaN, wake), ParkLogic.Act.Wake, "park: NaN distance wakes");
        Eq(ParkLogic.Decide(false, 10f, float.NaN, wake), ParkLogic.Act.Keep, "park: NaN distance never parks");
    }

    // ---------- 0.2.0 T1.3 single modal slot ----------
    static void UiModal()
    {
        string cur = null;
        Eq(UiModalLogic.TryOpen("modgate", ref cur), true, "modal: open modgate");
        Eq(cur, "modgate", "modal: current modgate");
        Eq(UiModalLogic.TryOpen("update", ref cur), false, "modal: update blocked");
        Eq(cur, "modgate", "modal: still modgate");
        Eq(UiModalLogic.TryOpen("modgate", ref cur), true, "modal: re-open same");
        UiModalLogic.Close("update", ref cur);
        Eq(cur, "modgate", "modal: close other no-op");
        UiModalLogic.Close("modgate", ref cur);
        Eq(cur, null, "modal: closed");
        Eq(UiModalLogic.TryOpen("update", ref cur), true, "modal: update after close");
        Eq(UiModalLogic.TryOpen("", ref cur), false, "modal: empty owner");
        Eq(UiModalLogic.TryOpen(null, ref cur), false, "modal: null owner");
    }

    // ---------- 0.2.0 T1.1 remote wheel spin rate ----------
    static void WheelSpin()
    {
        Eq(WheelSpinLogic.RateRadS(10, 0, 0.4, false), 25.0, "wheel: zero netAng → body/r");
        Eq(WheelSpinLogic.RateRadS(10, 24, 0.4, true), 24.0, "wheel: live netAng");
        Eq(WheelSpinLogic.RateRadS(10, 1, 0.4, true), 25.0, "wheel: weak netAng vs body → body/r");
        Eq(WheelSpinLogic.RateRadS(0, 0, 0.4, false), 0.0, "wheel: standstill");
        Eq(WheelSpinLogic.RateRadS(0, 0, 0.4, true), 0.0, "wheel: standstill alive zeros");
        Eq(WheelSpinLogic.RateRadS(-10, 0, 0.4, false), -25.0, "wheel: reverse body");
        Eq(WheelSpinLogic.RateRadS(1.5, 0.1, 0.4, true), 0.1, "wheel: slow body keeps weak netAng");
        Eq(WheelSpinLogic.AnyNetAngAlive(new float[] { 0, 0, 0 }), false, "wheel: all zero not alive");
        Eq(WheelSpinLogic.AnyNetAngAlive(new float[] { 0, 0.6f, 0 }), true, "wheel: one alive");
        Eq(WheelSpinLogic.ExtendAliveUntil(5, 0, true), 6.0, "wheel: extend +1s");
        Eq(WheelSpinLogic.IsAlive(5.5, 6.0), true, "wheel: still alive");
        Eq(WheelSpinLogic.IsAlive(6.1, 6.0), false, "wheel: expired");
        Eq(WheelSpinLogic.RateRadS(10, 0, 0, false), 10.0 / WheelSpinLogic.DefaultRadius, "wheel: bad radius → default");
    }

    // ---------- 0.2.0 T0.5 speed from rendered Δpose ----------
    static void PoseSpeed()
    {
        Eq(PoseSpeedLogic.FromDelta(10, 0, 0, 1), 10.0, "poseV: 10 m in 1 s");
        Eq(PoseSpeedLogic.FromDelta(0, 0, 0, 0.016), 0.0, "poseV: stationary");
        Eq(PoseSpeedLogic.FromDelta(0.16, 0, 0, 0.016), 10.0, "poseV: 10 m/s at 60 Hz");
        Eq(PoseSpeedLogic.FromDelta(3, 4, 0, 1), 5.0, "poseV: 3-4-5");
        Eq(PoseSpeedLogic.FromDelta(1, 0, 0, 0), 0.0, "poseV: zero dt");
        Eq(PoseSpeedLogic.FromDelta(1, 0, 0, 1e-4), 0.0, "poseV: below MinDt");
        Eq(PoseSpeedLogic.FromDelta(0, 0, 0, 1, 0, 0, 0.5), 2.0, "poseV: points form");
        Eq(PoseSpeedLogic.FromDelta(double.NaN, 0, 0, 1), 0.0, "poseV: NaN");
        Eq(PoseSpeedLogic.MinDt, 1e-3, "poseV: MinDt");
    }

    // ---------- 0.2.0 T0.4 per-stage frame cost ----------
    static void FrameCost()
    {
        Eq(FrameCostLogic.StageHotMs, 8.0, "cost: stage hot");
        Eq(FrameCostLogic.WindowCap >= 3600, true, "cost: window holds 30 s × 2 LocalStep/frame");
        Eq(FrameCostLogic.WindowCap >= 30 * 60 * 2, true, "cost: 30*60*2 formula");
        Eq(FrameCostLogic.MaxPlausibleFrameMs, 500.0, "cost: frame cap");
        Eq(FrameCostLogic.MaxPlausibleStageMs, 5000.0, "cost: stage cap");
        var buf = new double[FrameCostLogic.WindowCap];
        int n = 0, i = 0, over = 0;
        Eq(FrameCostLogic.TryPush(buf, ref n, ref i, -1, FrameCostLogic.MaxPlausibleStageMs), false, "cost: reject negative");
        Eq(n, 0, "cost: n after reject");
        Eq(FrameCostLogic.TryPush(buf, ref n, ref i, 600, FrameCostLogic.MaxPlausibleFrameMs), false, "cost: frame rejects 600");
        Eq(FrameCostLogic.TryPush(buf, ref n, ref i, 600, FrameCostLogic.MaxPlausibleStageMs), true, "cost: stage accepts 600");
        n = i = 0; Array.Clear(buf, 0, buf.Length);
        Eq(FrameCostLogic.TryPush(buf, ref n, ref i, double.NaN, FrameCostLogic.MaxPlausibleStageMs), false, "cost: reject NaN");
        for (var k = 0; k < 100; k++)
        {
            var ms = 1.0 + k * 0.01;
            if (FrameCostLogic.TryPush(buf, ref n, ref i, ms, FrameCostLogic.MaxPlausibleStageMs) && ms > FrameCostLogic.StageHotMs) over++;
        }
        Eq(n, 100, "cost: 100 samples");
        Eq(FrameCostLogic.TryStats(buf, n, out var p50, out var p99, out var max), true, "cost: stats ok");
        Eq(p50 > 1.4 && p50 < 1.6, true, $"cost: p50 mid ({p50:0.00})");
        Eq(p99 > 1.9 && p99 < 2.1, true, $"cost: p99 high ({p99:0.00})");
        Eq(max > 1.98 && max < 2.01, true, $"cost: max ({max:0.00})");
        Eq(over, 0, "cost: no hot yet");
        if (FrameCostLogic.TryPush(buf, ref n, ref i, 12.0, FrameCostLogic.MaxPlausibleStageMs)) over++;
        Eq(over, 1, "cost: >8ms counted");
        var fmt = FrameCostLogic.Format("resim", buf, n, over, FrameCostLogic.StageHotMs);
        Eq(fmt.StartsWith("resim p50="), true, "cost: format prefix");
        Eq(fmt.Contains(">8ms=1"), true, "cost: format hot");
        Eq(FrameCostLogic.Format("x", Array.Empty<double>(), 0, 0, 8).Contains("p50=-"), true, "cost: empty format");
        Eq(FrameCostLogic.Percentile(new[] { 1.0, 2.0, 3.0 }, 0.5), 2.0, "cost: median");
        Eq(FrameCostLogic.Percentile(new[] { 1.0 }, 0.99), 1.0, "cost: single");
        FrameCostLogic.Clear(buf, ref n, ref i);
        Eq(n, 0, "cost: clear n");
        Eq(i, 0, "cost: clear i");
        Eq(FrameCostLogic.TryStats(buf, n, out _, out _, out _), false, "cost: clear empty stats");
    }

    // ---------- 0.2.0 T0.3 Diagnostics RuntimeConfig retry ----------
    static void DiagRetry()
    {
        Eq(DiagLogic.IsRuntimeConfigNotReady(null), false, "diag: null");
        Eq(DiagLogic.IsRuntimeConfigNotReady(new Exception("")), false, "diag: empty msg");
        Eq(DiagLogic.IsRuntimeConfigNotReady(new Exception("something else")), false, "diag: unrelated");
        Eq(DiagLogic.IsRuntimeConfigNotReady(new InvalidOperationException("RuntimeConfig is not ready")), true, "diag: IOE RuntimeConfig");
        Eq(DiagLogic.IsRuntimeConfigNotReady(new Exception("Failed to get RuntimeConfig")), true, "diag: generic RuntimeConfig");
        Eq(DiagLogic.IsRuntimeConfigNotReady(new Exception("runtimeconfig missing")), true, "diag: case insensitive");
        Eq(DiagLogic.ShouldRetryRuntimeConfig(new Exception("RuntimeConfig"), 0), true, "diag: retry 0");
        Eq(DiagLogic.ShouldRetryRuntimeConfig(new Exception("RuntimeConfig"), 19), true, "diag: retry 19");
        Eq(DiagLogic.ShouldRetryRuntimeConfig(new Exception("RuntimeConfig"), 20), false, "diag: retry cap 20");
        Eq(DiagLogic.ShouldRetryRuntimeConfig(new Exception("other"), 0), false, "diag: no retry unrelated");
        Eq(DiagLogic.MaxRuntimeConfigRetries, 20, "diag: max retries constant");
    }

    // ---------- 0.2.0 T0.2 session log rotation ----------
    static void SessionLogs()
    {
        Eq(SessionLogsLogic.SanitizeWhy(""), "snap", "sanitize empty");
        Eq(SessionLogsLogic.SanitizeWhy("   "), "snap", "sanitize blank");
        Eq(SessionLogsLogic.SanitizeWhy("F7 button"), "F7-button", "sanitize spaces");
        Eq(SessionLogsLogic.SanitizeWhy("a/b\\c:d*e?f"), "a-b-c-d-e-f", "sanitize path chars");
        Eq(SessionLogsLogic.SanitizeWhy(new string('x', 60)).Length, 40, "sanitize capped at 40");
        Eq(SessionLogsLogic.SnapshotFileName(new DateTime(2026, 10, 24, 19, 17, 3), "session-end"),
            "LogOutput-20261024-191703-session-end.log", "snapshot name");
        Eq(SessionLogsLogic.TryParseStamp("LogOutput-20261024-191703-session-end.log", out var st)
            && st == new DateTime(2026, 10, 24, 19, 17, 3), true, "parse stamp");
        Eq(SessionLogsLogic.TryParseStamp("readme.txt", out _), false, "non-snapshot ignored");
        Eq(SessionLogsLogic.TryParseStamp("LogOutput.log", out _), false, "live log not a snapshot");
        Eq(SessionLogsLogic.TryParseStamp("LogOutput-bad.log", out _), false, "bad stamp rejected");

        var keep = new[]
        {
            "LogOutput-20261024-100000-a.log",
            "LogOutput-20261024-100100-b.log",
            "LogOutput-20261024-100200-c.log",
            "LogOutput-20261024-100300-d.log",
            "LogOutput-20261024-100400-e.log",
            "LogOutput-20261024-100500-f.log",
            "LogOutput-20261024-100600-g.log",
            "LogOutput-20261024-100700-h.log",
            "LogOutput-20261024-100800-i.log",
            "LogOutput-20261024-100900-j.log",
            "LogOutput-20261024-101000-k.log", // 11th → drop oldest
            "notes.txt",
            "OthLan.cfg",
        };
        var drop = SessionLogsLogic.PickExcess(keep, 10);
        Eq(drop.Count, 1, "11 snaps -> drop 1");
        Eq(drop[0], "LogOutput-20261024-100000-a.log", "drop oldest");
        Eq(drop.Contains("notes.txt"), false, "foreign files untouched");
        Eq(drop.Contains("OthLan.cfg"), false, "cfg untouched");
        var ten = new string[10];
        Array.Copy(keep, ten, 10);
        Eq(SessionLogsLogic.PickExcess(ten, 10).Count, 0, "exactly 10 -> drop none");
        Eq(SessionLogsLogic.PickExcess(Array.Empty<string>(), 10).Count, 0, "empty dir");
        Eq(SessionLogsLogic.PickExcess(new[] { "notes.txt", "foo.log" }, 10).Count, 0, "only foreign -> drop none");
        var eleven = new string[11];
        Array.Copy(keep, eleven, 11);
        var drop3 = SessionLogsLogic.PickExcess(eleven, 8);
        Eq(drop3.Count, 3, "11 keep 8 -> drop 3");
        Eq(drop3[0], "LogOutput-20261024-100000-a.log", "drop3 oldest first");
        Eq(drop3[2], "LogOutput-20261024-100200-c.log", "drop3 third");
        Eq(SessionLogsLogic.PickExcess(keep, 0).Count, 11, "keep 0 drops all snaps");
        Eq(SessionLogsLogic.PickExcess(new[] { @"C:\game\BepInEx\OthLan-logs\LogOutput-20261024-100000-a.log",
            "LogOutput-20261024-100100-b.log" }, 1)[0], "LogOutput-20261024-100000-a.log", "full path stripped");
        Eq(SessionLogsLogic.IsSessionCfgSection("Netcode"), true, "session cfg Netcode");
        Eq(SessionLogsLogic.IsSessionCfgSection("CarSync"), true, "session cfg CarSync");
        Eq(SessionLogsLogic.IsSessionCfgSection("Outage"), true, "session cfg Outage");
        Eq(SessionLogsLogic.IsSessionCfgSection("OwnCar"), true, "session cfg OwnCar");
        Eq(SessionLogsLogic.IsSessionCfgSection("Network"), false, "session cfg not Network");
        Eq(SessionLogsLogic.SessionCfgSections.Length, 4, "session cfg 4 sections");
    }

    // ---------- 0.1.9 pre-join Modio livery gate (wire + diff) ----------
    static void ModGate()
    {
        var a = new ModLiveryGateLogic.ModRef(10, 0);
        var b = new ModLiveryGateLogic.ModRef(20, 2);
        Eq(ModLiveryGateLogic.Format(new[] { a, b, a }), "10,20:2", "modgate: format unique");
        var p = ModLiveryGateLogic.Parse("10,20:2,bad,0,30");
        Eq(p.Count == 3 && p[0].Id == 10 && p[1].Id == 20 && p[1].SubId == 2 && p[2].Id == 30, true, "modgate: parse");
        var miss = ModLiveryGateLogic.Missing(p, m => m.Id == 10 || m.Id == 30);
        Eq(miss.Count == 1 && miss[0].Id == 20, true, "modgate: missing only 20");
        Eq(ModLiveryGateLogic.Parse("").Count, 0, "modgate: empty");
        Eq(ModLiveryGateLogic.ClassifyItem(true, true, true, false), ModLiveryGateLogic.ItemKind.Installed, "modgate: installed wins");
        Eq(ModLiveryGateLogic.ClassifyItem(false, false, false, false), ModLiveryGateLogic.ItemKind.Pending, "modgate: pending");
        Eq(ModLiveryGateLogic.ClassifyItem(false, true, false, false), ModLiveryGateLogic.ItemKind.Queued, "modgate: queued");
        Eq(ModLiveryGateLogic.ClassifyItem(false, true, true, false), ModLiveryGateLogic.ItemKind.Downloading, "modgate: downloading");
        Eq(ModLiveryGateLogic.ClassifyItem(false, false, true, true), ModLiveryGateLogic.ItemKind.Error, "modgate: error");
        Eq(ModLiveryGateLogic.ItemTitle("Cool Paint", "id", "idsub", 0), "Cool Paint", "modgate: title prefers name");
        Eq(ModLiveryGateLogic.ItemTitle("  ", "id10", "id10:2", 0), "id10", "modgate: title blank→id");
        Eq(ModLiveryGateLogic.ItemTitle(null, "id10", "id10:2", 2), "id10:2", "modgate: title null→sub");
        Eq(ModLiveryGateLogic.ItemStatusKey(ModLiveryGateLogic.ItemKind.Downloading, -1f), "modGate.stateDownloading", "modgate: status no pct");
        Eq(ModLiveryGateLogic.ItemStatusKey(ModLiveryGateLogic.ItemKind.Downloading, 0.42f), "modGate.stateDownloadingPct", "modgate: status pct");
        Eq(ModLiveryGateLogic.ItemStatusPercent(ModLiveryGateLogic.ItemKind.Downloading, 0.42f), 42, "modgate: pct round");
        Eq(ModLiveryGateLogic.ItemStatusPercent(ModLiveryGateLogic.ItemKind.Queued, 0.5f), -1, "modgate: pct only when downloading");
        Eq(UpdateLogic.TryFindAsset("{\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"6000.0.75.zip\",\"browser_download_url\":\"https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.1.9/6000.0.75.zip\"}]}",
            UpdateLogic.UnityLibsAssetName, out var u, out var why) && u.Contains("6000.0.75.zip"), true, $"modgate: unity-libs asset ({why})");
    }

    // ---------- 0.1.9 Launch hang detector ----------
    static void Hang()
    {
        Eq(OthLan.Launch.HangLogic.Classify("[Message:InteropManager] Downloading unity base libraries from https://unity.bepinex.dev/libraries/6000.0.75.zip"),
            OthLan.Launch.HangLogic.LineKind.UnityLibsDownload, "hang: download line");
        Eq(OthLan.Launch.HangLogic.Classify("[Info:OTH-LAN] 0.2.3 loaded. Press F7"),
            OthLan.Launch.HangLogic.LineKind.BootReady, "hang: boot ready");
        Eq(OthLan.Launch.HangLogic.IsHung(true, false, 10.0), true, "hang: 10s hung");
        Eq(OthLan.Launch.HangLogic.IsHung(true, false, 9.9), false, "hang: under 10s");
        Eq(OthLan.Launch.HangLogic.IsHung(true, true, 60), false, "hang: already ready");
        Eq(OthLan.Launch.HangLogic.IsHung(false, false, 60), false, "hang: never saw download");
    }

    // ---------- 0.1.9 host PosePlayback (jitter buffer + Hermite) ----------
    static void Playback()
    {
        var pb = new PosePlayback(new PosePlayback.Cfg { MinDelayS = 0.033, MaxDelayS = 0.10, DelayEwma = 1, JitterEwma = 1 });
        for (int i = 0; i < 25; i++)
        {
            double t = i * 0.02;
            pb.Push(new PosePlayback.Sample { T = t, Px = 10 * t, Vx = 10, Rw = 1, Tick = i });
        }
        Eq(pb.Count >= 16, true, $"playback: ring filled ({pb.Count})");
        var mid = pb.SampleAt(0.40);
        Eq(mid.Ok, true, "playback: sample ok");
        var playT = 0.40 - pb.DelayS;
        Eq(Math.Abs(mid.Px - 10 * playT) < 0.12, true, $"playback: Hermite steady ({mid.Px:0.000} vs {10 * playT:0.000})");
        Eq(pb.DelayS <= 0.10 + 1e-9, true, $"playback: delay capped ({pb.DelayS:0.000}s)");
        // past newest: hold (CarSync tick-age extrapolates — no double dead-reckon)
        var held = pb.SampleAt(0.40 + 1.0);
        Eq(held.Ok && !held.Extrapolated, true, "playback: past newest holds");
        Eq(Math.Abs(held.Px - 10 * 0.48) < 0.05, true, $"playback: hold = newest ({held.Px:0.000})");
        Eq(new PosePlayback().SampleAt(1).Ok, false, "playback: empty not ok");
        pb = new PosePlayback(new PosePlayback.Cfg { MinDelayS = 0.0, MaxDelayS = 0.01, DelayEwma = 1, JitterEwma = 1 });
        pb.Push(new PosePlayback.Sample { T = 0.0, Px = 0, Vx = 0, Rw = 1, Tick = 1 });
        pb.Push(new PosePlayback.Sample { T = 0.02, Px = 0.2, Vx = 10, Rw = 1, Tick = 2 });
        pb.Push(new PosePlayback.Sample { T = 0.04, Px = 0.4, Vx = 10, Rw = 1, Tick = 3 });
        var h = pb.SampleAt(0.04);
        Eq(h.Ok, true, "playback: short delay sample");
        Eq(Math.Abs(h.Px - 0.4) < 0.15, true, $"playback: near newest ({h.Px:0.000})");
        Eq(h.Tick, 3, "playback: tick from bracketing sample");
    }

    // ---------- 0.1.9 R3 contact/winch suspend ----------
    static void Suspend()
    {
        var c = new SuspendLogic.Cfg();
        Eq(SuspendLogic.ActiveNow(true, false, false, false, c), true, "suspend: local contact");
        Eq(SuspendLogic.ActiveNow(false, false, true, false, c), true, "suspend: host contact flag");
        Eq(SuspendLogic.ActiveNow(false, true, false, false, c), true, "suspend: local winch");
        Eq(SuspendLogic.ActiveNow(false, false, false, false, c), false, "suspend: idle");
        c.SuspendOnContact = false;
        Eq(SuspendLogic.ActiveNow(true, false, false, false, c), false, "suspend: contact disabled");
        c.SuspendOnContact = true; c.SuspendOnWinch = false;
        Eq(SuspendLogic.ActiveNow(false, true, false, false, c), false, "suspend: winch disabled");
        Eq(SuspendLogic.Why(true, true, false, false, new SuspendLogic.Cfg()), SuspendLogic.Reason.Both, "suspend: both");
        Eq(SuspendLogic.Why(true, false, false, false, new SuspendLogic.Cfg()), SuspendLogic.Reason.Contact, "suspend: contact reason");
        double last = 0;
        Eq(SuspendLogic.Hold(true, 1.0, ref last, 1.0), true, "suspend: enter");
        Eq(last, 1.0, "suspend: lastActive set");
        Eq(SuspendLogic.Hold(false, 1.5, ref last, 1.0), true, "suspend: hold within resume");
        Eq(SuspendLogic.Hold(false, 2.1, ref last, 1.0), false, "suspend: leave after resume");
        Eq(SuspendLogic.ResumeDelay(new SuspendLogic.Cfg { ResumeSeconds = 1 }, 200, 0.1) >= 1.2, true, "suspend: resume stretched by RTT");
        Eq(SuspendLogic.PredictiveMargin(0.2, 10, 0.2, 3), 2.2, "suspend: predictive margin");
        Eq(SuspendLogic.PredictiveMargin(0.2, 100, 1, 3), 3.0, "suspend: predictive margin capped");

        // 0.2.0 T4.1 InteractionLogic (D-1 defaults) + T4.0 servo formula owner
        var ic = new InteractionLogic.Cfg { ContactAuthority = InteractionLogic.Authority.Owner, ResumeSeconds = 1.5, MinDwellSeconds = 2 };
        Eq(InteractionLogic.WantShared(true, false, WinchKindLogic.Kind.None, WinchKindLogic.Kind.None, ic), false, "interact: Owner contact does not share");
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.Car, WinchKindLogic.Kind.None, ic), true, "interact: car winch shares");
        double ent = 0, wantAt = 0;
        var m = InteractionLogic.Mode.Owner;
        m = InteractionLogic.Step(m, 0, ref ent, ref wantAt, false, ic);
        Eq(m, InteractionLogic.Mode.Owner, "interact: idle Owner");
        m = InteractionLogic.Step(m, 1, ref ent, ref wantAt, true, ic);
        Eq(m, InteractionLogic.Mode.HostShared, "interact: enter HostShared");
        m = InteractionLogic.Step(m, 6, ref ent, ref wantAt, true, ic);
        m = InteractionLogic.Step(m, 6.1, ref ent, ref wantAt, false, ic);
        Eq(m, InteractionLogic.Mode.HostShared, "interact: still in dwell/resume");
        m = InteractionLogic.Step(m, 8.0, ref ent, ref wantAt, false, ic);
        Eq(m, InteractionLogic.Mode.Owner, "interact: leave after resume+dwell");
        m = InteractionLogic.Mode.Owner; ent = wantAt = 0;
        int enters = 0;
        for (var t = 0.0; t < 3.0; t += 0.3)
        {
            var prev = m;
            m = InteractionLogic.Step(m, t, ref ent, ref wantAt, true, ic);
            if (prev == InteractionLogic.Mode.Owner && m == InteractionLogic.Mode.HostShared) enters++;
        }
        Eq(enters, 1, "interact: chatter one enter");
        ic.WorldWinchAuthority = InteractionLogic.Authority.Host;
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.World, WinchKindLogic.Kind.None, ic), true, "interact: world winch Host authority");
        Eq(HostFollowLogic.ServoSpeed(0, 0.1, 10, 0, 5), 0, "servo: on target");
        Eq(HostFollowLogic.ServoSpeed(1, 0.1, 10, 0, 0), 10, "servo: 1m/0.1s = 10");
        Eq(HostFollowLogic.ServoSpeed(5, 0.1, 10, 0, 0), 10, "servo: cap 10");
        Eq(HostFollowLogic.ServoSpeed(1, 0, 10, 0, 5), 0, "servo: tau 0 → 0");
        Eq(HostFollowLogic.ServoSpeed(1, -0.1, 10, 0, 5), 0, "servo: tau neg → 0");
        Eq(HostFollowLogic.ServoSpeed(1, 0.1, 10, 0.5, -20), 10, "servo: neg clientSpeed ignored in cap");
        Eq(HostFollowLogic.ServoAngSpeed(0.2, 0.1, 3), 2.0, "servo-ang: 0.2rad/0.1s = 2");
        Eq(HostFollowLogic.ServoAngSpeed(1.0, 0.1, 3), 3.0, "servo-ang: capped at 3");
        Eq(HostFollowLogic.ServoAngSpeed(0, 0.1, 3), 0, "servo-ang: on target");
        Eq(HostFollowLogic.ServoAngSpeed(0.5, 0, 3), 0, "servo-ang: tau 0");
        Eq(HostFollowLogic.ServoAngSpeed(0.5, 0.1, 0), 0, "servo-ang: max 0");
        ic.ContactAuthority = InteractionLogic.Authority.Host;
        Eq(InteractionLogic.WantShared(true, false, WinchKindLogic.Kind.None, WinchKindLogic.Kind.None, ic), true, "interact: Host contact shares");
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.World, WinchKindLogic.Kind.None,
            new InteractionLogic.Cfg { WorldWinchAuthority = InteractionLogic.Authority.Owner }), false, "interact: world winch Owner ignores");
    }

    /// <summary>0.2.2 extra: epoch snap once; Stale after MaxExtrap; LastArrivalLocal updated.</summary>
    static void StreamEdge()
    {
        var pb = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0, MaxDelayS = 0, DelayEwma = 1, MaxExtrapS = 0.25 });
        pb.Push(Mk(1, 1.0, 0, 0, 10, 0, epoch: 1), 1.05);
        Eq(pb.LastArrivalLocal, 1.05, "stream: LastArrivalLocal");
        pb.Push(Mk(2, 2.0, 10, 0, 10, 0, epoch: 2), 2.05);
        Eq(pb.EpochDrops >= 1, true, "stream: epoch drop counted");
        var phys = pb.SampleAt(2.05, delayOverride: 0);
        Eq(phys.Ok && phys.Snap, true, "stream: physics SampleAt sees Snap");
        var snap = pb.SampleAt(2.05);
        Eq(snap.Ok && snap.Snap, true, "stream: render SampleAt still sees Snap (physics did not consume)");
        var again = pb.SampleAt(2.06);
        Eq(again.Snap, false, "stream: Snap consumed once by render path");
        var stale = pb.SampleAt(2.05 + 0.30);
        Eq(stale.Ok && stale.Stale, true, "stream: Stale after MaxExtrap");
        Eq(stale.Vx == 0 && stale.Vy == 0 && stale.Vz == 0, true, "stream: Stale freezes linear vel");
        // 30% loss uniform stream: no huge jumps when sampling mid-gap
        pb = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0.05, MaxDelayS = 0.1, DelayEwma = 0.5, SendHz = 30, MaxExtrapS = 0.25 });
        double t = 0; float x = 0; var rng = new Random(42);
        for (var i = 0; i < 90; i++)
        {
            t += 1 / 30.0; x += 20f / 30f;
            if (rng.NextDouble() < 0.30) continue;
            pb.Push(Mk(i + 1, t, x, 0, 20, 0), t + 0.02);
        }
        var mid = pb.SampleAt(t + 0.02);
        Eq(mid.Ok, true, "stream: lossy stream still Ok");
        Eq(Math.Abs(mid.Px - x) < 2.0, true, $"stream: lossy error bounded ({mid.Px - x:0.00} m)");
    }

    static void SessionWire()
    {
        Loc.SetLanguage("en");
        Eq(CoopSessionLogic.StatusOf(true, true), CoopSessionLogic.StatusNoLink, "session: nolink wins");
        Eq(CoopSessionLogic.StatusOf(false, true), CoopSessionLogic.StatusShared, "session: shared");
        Eq(CoopSessionLogic.StatusOf(false, false), CoopSessionLogic.StatusOk, "session: ok");
        Eq(CoopSessionLogic.SharedFromFlags(CarSyncLogic.FlagSuspendWinch), true, "session: winch flag is shared");
        Eq(CoopSessionLogic.SharedFromFlags(CarSyncLogic.FlagSuspendContact), true, "session: contact flag is shared");
        Eq(CoopSessionLogic.SharedFromFlags(CarSyncLogic.FlagCarSync), false, "session: carsync flag alone is not shared");
        Eq(CoopSessionLogic.ClipName("  a\nb  "), "ab", "session: clip controls");
        Eq(System.Text.Encoding.UTF8.GetByteCount(CoopSessionLogic.ClipName(new string('я', 20))) <= CoopSessionLogic.MaxNameUtf8, true, "session: clip utf8 cap");
        var rows = new System.Collections.Generic.List<CoopSessionLogic.Row>();
        for (var i = 0; i < 9; i++) rows.Add(new CoopSessionLogic.Row { Slot = (byte)i, Name = "n" + i, PingMs = -1 });
        var pkt = CoopSessionLogic.EncodeList(rows);
        var back = new System.Collections.Generic.List<CoopSessionLogic.Row>();
        Eq(CoopSessionLogic.TryDecodeList(pkt, back), true, "session: decode clamped list");
        Eq(back.Count, 8, "session: max 8 rows");
        Eq(back[7].PingMs, -1, "session: missing ping");
        rows.Clear();
        rows.Add(new CoopSessionLogic.Row { Slot = 1, Name = "Ёж", PingMs = 12, LossPct = 3, Status = CoopSessionLogic.StatusShared });
        pkt = CoopSessionLogic.EncodeList(rows);
        Eq(CoopSessionLogic.TryDecodeList(pkt, back) && back.Count == 1 && back[0].Name == "Ёж" && back[0].PingMs == 12 && back[0].Status == CoopSessionLogic.StatusShared && back[0].LossPct == 3, true, "session: unicode row");
        var shortb = new byte[pkt.Length - 1];
        Array.Copy(pkt, shortb, shortb.Length);
        Eq(CoopSessionLogic.TryDecodeList(shortb, back), false, "session: short list rejected");
        var extra = new byte[pkt.Length + 1];
        Array.Copy(pkt, extra, pkt.Length);
        Eq(CoopSessionLogic.TryDecodeList(extra, back), false, "session: trailing byte rejected");
        Eq(CoopSessionLogic.TryDecodeResync(CoopSessionLogic.EncodeResync(3), out var slot) && slot == 3, true, "session: resync slot");
        Eq(CoopSessionLogic.IsDiag(CoopSessionLogic.EncodeDiag()), true, "session: diag magic");
        Eq(CoopSessionLogic.IsDiag(CoopSessionLogic.EncodeResync(1)), false, "session: resync is not diag");
        var ok = new CoopSessionLogic.Row { Slot = 2, Name = "Ann", PingMs = 40, LossPct = 3, Status = CoopSessionLogic.StatusOk };
        Eq(CoopSessionLogic.FormatRow(ok), "Ann   40 ms   3%   ok", "session: row ok");
        var nl = new CoopSessionLogic.Row { Name = "Bo", PingMs = -1, LossPct = 10, Status = CoopSessionLogic.StatusNoLink, NoLinkS = 4 };
        Eq(CoopSessionLogic.FormatRow(nl), "Bo   —   10%   no link 4 s", "session: row nolink");
        Eq(CoopSessionLogic.FormatRow(new CoopSessionLogic.Row { Name = "Cy", PingMs = 8, Status = CoopSessionLogic.StatusShared }), "Cy   8 ms   0%   shared", "session: row shared");
    }

    /// <summary>0.2.2: updater must parse the next three-part tag and refuse draft/wrong-repo assets.</summary>
    static void ReleaseGate022()
    {
        Eq(UpdateLogic.Compare("0.2.2", "0.2.1"), 1, "022: 0.2.2 > 0.2.1");
        Eq(UpdateLogic.IsNewer("0.2.2", "0.2.1"), true, "022: offer newer");
        Eq(UpdateLogic.ShouldOffer("0.2.2", "0.2.1", "0.2.2"), false, "022: skipped not offered");
        Eq(UpdateLogic.ShouldOffer("0.2.2", "0.2.1", "0.2.1"), true, "022: skip of older still offers");
        string J(string tag, bool draft = false, bool pre = false) =>
            $"{{\"tag_name\":\"{tag}\",\"name\":\"n\",\"body\":\"## What's new in 0.2.2\\n\\n- A\\n\\n## Что нового в 0.2.2\\n\\n- А\\n\",\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(pre ? "true" : "false")},\"assets\":[" +
            $"{{\"name\":\"OTH-LAN-0.2.2.zip\",\"size\":12,\"browser_download_url\":\"https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.2.2/OTH-LAN-0.2.2.zip\"}}," +
            $"{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.2.2/SHA256SUMS.txt\"}}," +
            $"{{\"name\":\"6000.0.75.zip\",\"size\":9,\"browser_download_url\":\"https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.2.2/6000.0.75.zip\"}}]}}";
        var rel = UpdateLogic.ParseRelease(J("v0.2.2"), out var why);
        Eq(rel != null && rel.Version == "0.2.2" && why == null, true, $"022: parse v0.2.2 ({why})");
        Eq(rel != null && rel.ZipName == "OTH-LAN-0.2.2.zip" && rel.ZipUrl.EndsWith("/OTH-LAN-0.2.2.zip"), true, "022: zip asset");
        Eq(rel != null && rel.SumsUrl.EndsWith("/SHA256SUMS.txt"), true, "022: sums asset");
        Eq(rel != null && rel.UnityLibsUrl.EndsWith("/6000.0.75.zip") && rel.UnityLibsSize == 9, true, "022: unity-libs asset");
        Eq(UpdateLogic.ShortNotes(rel.Notes, "en"), "• A", "022: short notes en");
        Eq(UpdateLogic.ShortNotes(rel.Notes, "ru"), "• А", "022: short notes ru");
        Eq(UpdateLogic.ParseRelease(J("v0.2.2", draft: true), out _) == null, true, "022: draft refused");
        Eq(UpdateLogic.ParseRelease(J("v0.2.2", pre: true), out _) == null, true, "022: prerelease refused");
        Eq(UpdateLogic.AllowedAssetUrl("https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.2.2/OTH-LAN-0.2.2.zip"), true, "022: asset allow");
        Eq(UpdateLogic.AllowedAssetUrl("https://github.com/evil/over-the-hill-lan-coop/releases/download/v0.2.2/OTH-LAN-0.2.2.zip"), false, "022: other owner refused");
        var sums = UpdateLogic.ParseSums("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899  OTH-LAN-0.2.2.zip\n");
        Eq(sums.ContainsKey("OTH-LAN-0.2.2.zip"), true, "022: sums key");
    }

    /// <summary>0.2.2: InteractionLogic hysteresis edges (dwell vs resume, margins).</summary>
    static void InteractEdge()
    {
        var ic = new InteractionLogic.Cfg { ContactAuthority = InteractionLogic.Authority.Host, ResumeSeconds = 1.0, MinDwellSeconds = 3.0 };
        double ent = 0, wantAt = 0;
        var m = InteractionLogic.Mode.Owner;
        m = InteractionLogic.Step(m, 1.0, ref ent, ref wantAt, true, ic);
        Eq(m, InteractionLogic.Mode.HostShared, "interact-edge: enter");
        // clear want immediately: MinDwell keeps shared
        m = InteractionLogic.Step(m, 1.5, ref ent, ref wantAt, false, ic);
        Eq(m, InteractionLogic.Mode.HostShared, "interact-edge: dwell holds");
        m = InteractionLogic.Step(m, 3.9, ref ent, ref wantAt, false, ic);
        Eq(m, InteractionLogic.Mode.HostShared, "interact-edge: still in dwell");
        m = InteractionLogic.Step(m, 4.1, ref ent, ref wantAt, false, ic);
        Eq(m, InteractionLogic.Mode.Owner, "interact-edge: leave after dwell (resume already expired)");
        // resume alone: leave dwell satisfied but resume window open
        m = InteractionLogic.Mode.Owner; ent = wantAt = 0;
        m = InteractionLogic.Step(m, 10, ref ent, ref wantAt, true, ic);
        m = InteractionLogic.Step(m, 14, ref ent, ref wantAt, false, ic); // dwell done at 13, wantAt=10 → resume expired at 11
        Eq(m, InteractionLogic.Mode.Owner, "interact-edge: leave when both dwell+resume clear");
        m = InteractionLogic.Mode.Owner; ent = wantAt = 0;
        m = InteractionLogic.Step(m, 20, ref ent, ref wantAt, true, ic);
        m = InteractionLogic.Step(m, 22.5, ref ent, ref wantAt, true, ic); // refresh want
        m = InteractionLogic.Step(m, 23.5, ref ent, ref wantAt, false, ic); // dwell ok (3s), resume still open
        Eq(m, InteractionLogic.Mode.HostShared, "interact-edge: resume holds after dwell");
        m = InteractionLogic.Step(m, 24.0, ref ent, ref wantAt, false, ic);
        Eq(m, InteractionLogic.Mode.Owner, "interact-edge: leave after resume");
        Eq(InteractionLogic.ResumeDelay(ic, 100, 0.2) >= 1.3, true, "interact-edge: ResumeDelay stretches");
        Eq(InteractionLogic.PredictiveMargin(0.5, 10, 0.2, 3), 2.5, "interact-edge: margin speed*look");
        Eq(InteractionLogic.PredictiveMargin(0.5, 100, 1, 3), 3.0, "interact-edge: margin capped");
        Eq(InteractionLogic.WantShared(false, true, WinchKindLogic.Kind.None, WinchKindLogic.Kind.None, ic), true, "interact-edge: host contact alone");
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.None, WinchKindLogic.Kind.Car, ic), true, "interact-edge: host car winch");
        var worldHost = new InteractionLogic.Cfg { WorldWinchAuthority = InteractionLogic.Authority.Host };
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.None, WinchKindLogic.Kind.World, worldHost), true,
            "interact-edge: host World winch alone shares");
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.None, WinchKindLogic.Kind.World,
            new InteractionLogic.Cfg { WorldWinchAuthority = InteractionLogic.Authority.Owner }), false,
            "interact-edge: host World ignored when Owner");
    }

    static void SessionNotifyEdges()
    {
        Eq(SessionNotifyLogic.BecameNoLink(false, true), true, "notify: no-link rising");
        Eq(SessionNotifyLogic.BecameNoLink(true, true), false, "notify: stay no-link");
        Eq(SessionNotifyLogic.BecameLinked(true, false), true, "notify: link restored");
        Eq(SessionNotifyLogic.HostSessionEnded(true, true, false), true, "notify: host session end");
        Eq(SessionNotifyLogic.HostSessionEnded(true, false, false), false, "notify: never had runner");
        Eq(SessionNotifyLogic.HostSessionEnded(false, true, false), false, "notify: not client");
        Eq(SessionNotifyLogic.BecamePresent(false, true), true, "notify: player join");
        Eq(SessionNotifyLogic.BecameAbsent(true, false), true, "notify: player leave edge");
        Eq(SessionNotifyLogic.BecamePresent(true, true), false, "notify: already present");
        Eq(SessionNotifyLogic.JoinLines(new[] { "a", "b" }), "a · b", "notify: join two lines");
        Eq(SessionNotifyLogic.JoinLines(new[] { "only" }), "only", "notify: join one line");
        Eq(SessionNotifyLogic.JoinLines(new string[0]), "", "notify: join empty");
        Eq(SessionNotifyLogic.JoinLines(new[] { "a", "", null, "b" }), "a · b", "notify: join skips blanks");
        Eq(SessionNotifyLogic.BecameLinked(false, false), false, "notify: linked false when never timed out");
        Eq(SessionNotifyLogic.BecameLinked(false, true), false, "notify: linked false on rising no-link");
        Eq(SessionNotifyLogic.HostSessionEnded(true, true, true), false, "notify: host end false while runner lives");
    }

    /// <summary>0.2.3: updater version format, garbage Compare, release-assets host, no-sums gate, skip.</summary>
    static void ReleaseGate023()
    {
        Eq(UpdateLogic.TryParseVersion("v0.2.3", out var v23) && UpdateLogic.Format(v23) == "0.2.3", true, "023: format v0.2.3");
        Eq(UpdateLogic.Format((0, 1, 8, 1)), "0.1.8.1", "023: format four-part");
        Eq(UpdateLogic.Format((0, 1, 8, 0)), "0.1.8", "023: format drops zero build");
        Eq(UpdateLogic.Compare("garbage", "0.2.2"), -1, "023: garbage < version");
        Eq(UpdateLogic.Compare("0.2.2", "garbage"), 1, "023: version > garbage");
        Eq(UpdateLogic.Compare("x", "y"), 0, "023: both garbage equal");
        Eq(UpdateLogic.AllowedUrl("https://release-assets.githubusercontent.com/a"), true, "023: release-assets host");
        Eq(UpdateLogic.ShouldOffer("0.2.3", "0.2.2", "0.2.3"), false, "023: skipped 0.2.3 not offered");
        Eq(UpdateLogic.ShouldOffer("0.2.3", "0.2.2", "0.2.2"), true, "023: skip of older still offers 0.2.3");
        string JnoSums = "{\"tag_name\":\"v0.2.3\",\"name\":\"n\",\"body\":\"x\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
            "{\"name\":\"OTH-LAN-0.2.3.zip\",\"size\":12,\"browser_download_url\":\"https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.2.3/OTH-LAN-0.2.3.zip\"}]}";
        var nos = UpdateLogic.ParseRelease(JnoSums, out var whyNos);
        Eq(nos == null && whyNos != null && whyNos.Contains("SHA256SUMS"), true, $"023: no sums refused ({whyNos})");
        string Jok = "{\"tag_name\":\"v0.2.3\",\"name\":\"n\",\"body\":\"## What's new in 0.2.3\\n\\n- B\\n\\n## Что нового в 0.2.3\\n\\n- Б\\n\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
            "{\"name\":\"OTH-LAN-0.2.3.zip\",\"size\":12,\"browser_download_url\":\"https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.2.3/OTH-LAN-0.2.3.zip\"}," +
            "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/Sergaris/over-the-hill-lan-coop/releases/download/v0.2.3/SHA256SUMS.txt\"}]}";
        var rel = UpdateLogic.ParseRelease(Jok, out var whyOk);
        Eq(rel != null && rel.Version == "0.2.3" && whyOk == null, true, $"023: parse v0.2.3 ({whyOk})");
        Eq(UpdateLogic.ShortNotes(rel.Notes, "en"), "• B", "023: short notes en");
        Eq(UpdateLogic.ShortNotes(rel.Notes, "ru"), "• Б", "023: short notes ru");
    }

    /// <summary>0.2.3: OTPL sanitize, OTC3 version/quat/count, StatePlayback old-epoch, Interaction NaN RTT.</summary>
    static void WireEdges023()
    {
        var rows = new System.Collections.Generic.List<CoopSessionLogic.Row>
        {
            new() { Slot = 1, Name = "X", PingMs = 200_000, LossPct = 0, Status = 99 },
        };
        var pkt = CoopSessionLogic.EncodeList(rows);
        var back = new System.Collections.Generic.List<CoopSessionLogic.Row>();
        Eq(CoopSessionLogic.TryDecodeList(pkt, back) && back.Count == 1 && back[0].Status == CoopSessionLogic.StatusOk, true, "023: status 99→Ok on wire");
        Eq(back[0].PingMs == 65534, true, $"023: ping clamp ({back[0].PingMs})");
        pkt[8] = 101; // LossPct after Slot(1)+Ping(2) at offset 5+1+2=8
        Eq(CoopSessionLogic.TryDecodeList(pkt, back), false, "023: loss 101 rejected");
        pkt = CoopSessionLogic.EncodeList(rows);
        pkt[9] = 3; // Status byte
        Eq(CoopSessionLogic.TryDecodeList(pkt, back), false, "023: status 3 rejected");
        var a = new CoopSessionLogic.Row { Slot = 1, Name = "A", PingMs = 10, Status = CoopSessionLogic.StatusOk };
        var b = new CoopSessionLogic.Row { Slot = 2, Name = "B", PingMs = 20, Status = CoopSessionLogic.StatusOk };
        var block = CoopSessionLogic.FormatBlock(new[] { a, b });
        Eq(block == CoopSessionLogic.FormatRow(a) + "\n" + CoopSessionLogic.FormatRow(b), true, "023: FormatBlock joins with NL");
        Eq(CoopSessionLogic.FormatBlock(null), "", "023: FormatBlock null");
        Eq(CoopSessionLogic.FormatBlock(new CoopSessionLogic.Row[0]), "", "023: FormatBlock empty");

        var s = new CarWire.CarStateSample
        {
            Seq = 1, SendTime = 1.0, Px = 0, Py = 0, Pz = 0, Qx = 0, Qy = 0, Qz = 0, Qw = 0,
            Vx = 0, Vy = 0, Vz = 0, Flags = 0, SteerDeg = 0, WheelCount = 0,
        };
        var p = new CarWire.CarStatePacket { Slot = 1, Epoch = 1, Count = 1 };
        p.SetState(0, s);
        var buf = new byte[CarWire.MaxPacketBytes];
        var n = CarWire.Encode(p, buf);
        Eq(n > 0 && CarWire.TryDecode(buf.AsSpan(0, n), out var d) && Math.Abs(d.S0.Qw - 1f) < 1e-5, true, "023: zero quat → Qw=1");
        buf[4] = 99;
        Eq(CarWire.TryDecode(buf.AsSpan(0, n), out _), false, "023: bad OTC3 version rejected");
        buf[4] = CarWire.Version;
        var bad = p; bad.Count = 4;
        Eq(CarWire.Encode(bad, buf), -1, "023: count 4 rejected");

        var pb = new StatePlayback();
        pb.Push(Mk(10, 1.0, 0, 0, 0, 0, epoch: 2), 1.05);
        Eq(pb.Count, 1, "023: epoch2 stored");
        pb.Push(Mk(9, 0.5, 9, 0, 0, 0, epoch: 1), 1.06); // older epoch, older send
        Eq(pb.Count == 1 && pb.EpochDrops == 0, true, "023: old epoch ignored");
        pb.Push(Mk(8, 0.9, 1, 0, 0, 0, epoch: 2), 1.07); // reorder seq within epoch
        Eq(pb.Reorders >= 1, true, "023: reorder counted");

        var ic = new InteractionLogic.Cfg { ResumeSeconds = 1.0 };
        Eq(InteractionLogic.ResumeDelay(ic, double.NaN, 0) >= 1.2, true, "023: NaN RTT → default 200ms net");
        Eq(ModLiveryGateLogic.Format(null), "", "023: mod format null");
        Eq(ModLiveryGateLogic.Format(new[] { new ModLiveryGateLogic.ModRef(0, 0), new ModLiveryGateLogic.ModRef(5, 1) }), "5:1", "023: mod format skips id0");
    }

    /// <summary>0.2.0 T4.4: SyncProbe line format / parse / distance.</summary>
    static void SyncProbe()
    {
        var line = SyncProbeLogic.Format(2, 42, 10.5f, -1.25f, 3f);
        Eq(line.StartsWith(SyncProbeLogic.Tag), true, "sync: tag prefix");
        Eq(SyncProbeLogic.TryParse(line, out var sl, out var seq, out var x, out var y, out var z)
            && sl == 2 && seq == 42 && Math.Abs(x - 10.5f) < 1e-3 && Math.Abs(y - (-1.25f)) < 1e-3 && Math.Abs(z - 3f) < 1e-3,
            true, $"sync: roundtrip ({line})");
        Eq(SyncProbeLogic.TryParse("prefix " + line + " suffix", out sl, out seq, out _, out _, out _) && sl == 2 && seq == 42, true, "sync: embedded in log line");
        Eq(SyncProbeLogic.TryParse("no sync here", out _, out _, out _, out _, out _), false, "sync: reject noise");
        Eq(Math.Abs(SyncProbeLogic.DiffMeters(0, 0, 0, 3, 4, 0) - 5.0) < 1e-9, true, "sync: 3-4-5 distance");
        Eq(SyncProbeLogic.DiffMeters(1, 2, 3, 1, 2, 3), 0.0, "sync: zero distance");
        var ring = new SyncProbeLogic.Ring(4);
        ring.Note(1, 1, 0, 0, 0);
        ring.Note(1, 2, 1, 0, 0);
        ring.Note(2, 5, 0, 1, 0);
        Eq(ring.Count, 3, "sync: ring count");
        var snap = ring.Snapshot();
        Eq(snap.Count, 3, "sync: snap size");
        Eq(snap[0].Seq, 1u, "sync: oldest seq");
        Eq(snap[2].Seq, 5u, "sync: newest seq");
        ring.Note(1, 3, 2, 0, 0);
        ring.Note(1, 4, 3, 0, 0); // capacity 4 — drops seq 1; order 2,5,3,4
        snap = ring.Snapshot();
        Eq(snap.Count, 4, "sync: full snap");
        Eq(snap[0].Seq, 2u, "sync: wrap oldest");
        Eq(snap[1].Seq, 5u, "sync: wrap mid");
        Eq(snap[3].Seq, 4u, "sync: wrap newest");
        var drained = ring.Drain();
        Eq(drained.Count, 4, "sync: drain returns all");
        Eq(ring.Count, 0, "sync: drain empties");
        Eq(ring.Drain().Count, 0, "sync: second drain empty");
        Eq(SyncProbeLogic.Probed(0), false, "sync: seq 0 never probed");
        Eq(SyncProbeLogic.Probed(SyncProbeLogic.Stride), true, "sync: stride seq probed");
        Eq(SyncProbeLogic.Probed(SyncProbeLogic.Stride + 1), false, "sync: other seq skipped");
        var probed = 0; for (uint s = 1; s <= 150; s++) if (SyncProbeLogic.Probed(s)) probed++;
        Eq(probed, (int)(150 / SyncProbeLogic.Stride), "sync: 5 s at 30 Hz -> ~2 lines/s");
        Eq(SyncProbeLogic.DefaultCapacity * SyncProbeLogic.Stride >= 300 * 4, true, "sync: capacity covers 5s x 4 cars@30Hz");
    }

    /// <summary>
    /// Synthetic T4.4 / §5.1–5.2: OTC3 encode → two observers' StatePlayback → poses agree;
    /// SyncProbe rings pair by (slot,seq) within plan thresholds; delayOverride:0 vs render delay;
    /// epoch snap; redundant 3-state packet feeds newest first.
    /// </summary>
    static void StreamSyncInvariant()
    {
        // --- OTC3 wire → identical bytes → two StatePlayback observers (host relay model) ---
        var cfg = new StatePlayback.Cfg { MinDelayS = 0.05, MaxDelayS = 0.15, DelayEwma = 1, SendHz = 30, MaxExtrapS = 0.25 };
        var obsA = new StatePlayback(cfg);
        var obsB = new StatePlayback(cfg);
        var buf = new byte[CarWire.MaxPacketBytes];
        const int slot = 2;
        const byte epoch = 3;
        for (var i = 0; i < 60; i++)
        {
            var send = i / 30.0;
            var px = (float)(10 * send); // 10 m/s along X
            var s = new CarWire.CarStateSample
            {
                Seq = (uint)(i + 1), SendTime = send,
                Px = px, Py = 1, Pz = 0, Qx = 0, Qy = 0, Qz = 0, Qw = 1,
                Vx = 10, Vy = 0, Vz = 0, Flags = CarWire.FlagEngineOn, SteerDeg = 0, WheelCount = 4,
            };
            s.SetWheel(0, new CarWire.WheelSample { RollDeg = (i * 36f) % 360f, SpinRadS = 20f, SuspM = 0.02f });
            var pkt = new CarWire.CarStatePacket { Slot = slot, Epoch = epoch, Count = 1 };
            pkt.SetState(0, s);
            var n = CarWire.Encode(pkt, buf);
            Eq(n > 0, true, $"stream: encode i={i}");
            CarWire.CarStatePacket dec = default;
            Eq(CarWire.TryDecode(buf.AsSpan(0, n), out dec), true, $"stream: decode i={i}");
            // same arrival skew on both PCs (ideal LAN); relay copies bytes unchanged
            var arrival = send + 0.02;
            obsA.Push(ToPb(dec.S0, epoch), arrival);
            obsB.Push(ToPb(dec.S0, epoch), arrival);
        }
        var now = 59 / 30.0 + 0.02 + 0.05; // after last + delay
        var ra = obsA.SampleAt(now);
        var rb = obsB.SampleAt(now);
        Eq(ra.Ok && rb.Ok, true, "stream: both observers ok");
        var dObs = SyncProbeLogic.DiffMeters(ra.Px, ra.Py, ra.Pz, rb.Px, rb.Py, rb.Pz);
        Eq(dObs < 1e-4, true, $"stream: observers identical ({dObs:0.######} m)");

        // physics head (delay 0) vs render head: same buffer, bounded gap at 30 Hz + 50 ms delay
        var phys = obsA.SampleAt(now, delayOverride: 0);
        var render = obsA.SampleAt(now);
        Eq(phys.Ok && render.Ok, true, "stream: phys+render ok");
        var lead = SyncProbeLogic.DiffMeters(phys.Px, phys.Py, phys.Pz, render.Px, render.Py, render.Pz);
        // ~delay*speed = 0.05*10 = 0.5 m; allow Hermite/jitter slack
        Eq(lead <= 0.65, true, $"stream: phys-render lead {lead:0.000} m ≤ 0.65");
        Eq(phys.Px >= render.Px - 1e-3f, true, "stream: phys not behind render on +Vx");

        // --- SyncProbe cross-PC pairing (tools/sync_compare.py algorithm) ---
        var ringHost = new SyncProbeLogic.Ring(64);
        var ringClient = new SyncProbeLogic.Ring(64);
        for (uint seq = 1; seq <= 40; seq++)
        {
            var x = seq * 0.1f;
            ringHost.Note(slot, seq, x, 1, 0);
            // client sees same raw OTC3 sample (quantization already applied at encode)
            ringClient.Note(slot, seq, x, 1, 0);
        }
        // inject one divergent pair (simulates post-ram drift) still under §5.2 max 1 m
        ringHost.Note(slot, 41, 4.1f, 1, 0);
        ringClient.Note(slot, 41, 4.1f + 0.25f, 1, 0);
        var diffs = new System.Collections.Generic.List<double>();
        var mapC = new System.Collections.Generic.Dictionary<(int, uint), SyncProbeLogic.Sample>();
        foreach (var s in ringClient.Snapshot()) mapC[(s.Slot, s.Seq)] = s;
        foreach (var h in ringHost.Snapshot())
        {
            if (!mapC.TryGetValue((h.Slot, h.Seq), out var c)) continue;
            diffs.Add(SyncProbeLogic.DiffMeters(h.X, h.Y, h.Z, c.X, c.Y, c.Z));
        }
        Eq(diffs.Count >= 40, true, $"stream: paired samples {diffs.Count}");
        diffs.Sort();
        var p99 = diffs[(int)((diffs.Count - 1) * 0.99)];
        var max = diffs[diffs.Count - 1];
        Eq(p99 <= 0.3, true, $"stream: sync p99 {p99:0.000} ≤ 0.3 m (T4.4)");
        Eq(max <= 1.0, true, $"stream: sync max {max:0.000} ≤ 1 m (T4.4)");
        Eq(max >= 0.24 && max <= 0.26, true, $"stream: injected 0.25 m drift seen ({max:0.000})");

        // Format must stay parseable (parity with sync_compare SYNC_RE)
        var line = SyncProbeLogic.Format(slot, 99, -12.3456f, 0.001f, 100f);
        Eq(SyncProbeLogic.TryParse("[Info:OTH-LAN] " + line, out var ps, out var pq, out var px2, out _, out _)
            && ps == slot && pq == 99 && Math.Abs(px2 - (-12.346f)) < 0.002f, // Format uses 0.###
            true, $"stream: Format/TryParse log parity ({line})");

        // --- epoch bump snaps both observers to new pose (teleport) ---
        var tele = new CarWire.CarStateSample
        {
            Seq = 100, SendTime = 3.0, Px = 50, Py = 2, Pz = 0, Qw = 1, Vx = 0, WheelCount = 0,
        };
        var tp = new CarWire.CarStatePacket { Slot = slot, Epoch = (byte)(epoch + 1), Count = 1 };
        tp.SetState(0, tele);
        var tn = CarWire.Encode(tp, buf);
        Eq(tn > 0, true, "stream: teleport encode");
        CarWire.CarStatePacket td = default;
        Eq(CarWire.TryDecode(buf.AsSpan(0, tn), out td), true, "stream: teleport decode");
        obsA.Push(ToPb(td.S0, td.Epoch), 3.02);
        obsB.Push(ToPb(td.S0, td.Epoch), 3.02);
        var sa = obsA.SampleAt(3.02);
        var sb = obsB.SampleAt(3.02);
        Eq(sa.Ok && sa.Snap && Math.Abs(sa.Px - 50) < 1e-3, true, $"stream: epoch snap A ({sa.Px})");
        Eq(sb.Ok && sb.Snap && Math.Abs(sb.Px - 50) < 1e-3, true, $"stream: epoch snap B ({sb.Px})");
        Eq(SyncProbeLogic.DiffMeters(sa.Px, sa.Py, sa.Pz, sb.Px, sb.Py, sb.Pz) < 1e-4, true, "stream: snap agrees");

        // --- redundant Count=3: newest first; decode preserves S0 ---
        CarWire.CarStateSample MkS(uint seq, double t, float x) => new()
        {
            Seq = seq, SendTime = t, Px = x, Py = 0, Pz = 0, Qw = 1, Vx = 0, WheelCount = 0,
        };
        var red = new CarWire.CarStatePacket { Slot = 1, Epoch = 1, Count = 3 };
        red.SetState(0, MkS(30, 1.00, 30));
        red.SetState(1, MkS(29, 0.967, 29));
        red.SetState(2, MkS(28, 0.933, 28));
        var rn = CarWire.Encode(red, buf);
        Eq(rn > 0 && CarWire.TryDecode(buf.AsSpan(0, rn), out var rd) && rd.Count == 3 && rd.S0.Seq == 30 && rd.S2.Seq == 28,
            true, "stream: 3-state newest-first");

        // --- Interaction: Owner contact never HostShared (D-1); car-winch still shares once ---
        var ic = new InteractionLogic.Cfg
        {
            ContactAuthority = InteractionLogic.Authority.Owner,
            WorldWinchAuthority = InteractionLogic.Authority.Owner,
            ResumeSeconds = 0.3, MinDwellSeconds = 0.5,
        };
        Eq(InteractionLogic.WantShared(true, false, WinchKindLogic.Kind.None, WinchKindLogic.Kind.None, ic), false,
            "stream: D-1 contact stays Owner");
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.Car, WinchKindLogic.Kind.None, ic), true,
            "stream: car-winch HostShared");
        // T4.2 host arm: world winch + Owner → no FlagSuspendWinch / no follow pause
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.World, WinchKindLogic.Kind.None, ic), false,
            "stream: T4.2 world+Owner → host keeps following");
        Eq(InteractionLogic.WantShared(false, false, WinchKindLogic.Kind.World, WinchKindLogic.Kind.None,
            new InteractionLogic.Cfg { WorldWinchAuthority = InteractionLogic.Authority.Host }), true,
            "stream: T4.2 world+Host → host suspends");
        // Step only sees `want` bool — tow enter/leave once (dwell 0.5 + resume 0.3)
        double ent = 0, wantAt = 0;
        var mode = InteractionLogic.Mode.Owner;
        mode = InteractionLogic.Step(mode, 1.0, ref ent, ref wantAt, true, ic);
        Eq(mode, InteractionLogic.Mode.HostShared, "stream: enter shared once");
        mode = InteractionLogic.Step(mode, 1.1, ref ent, ref wantAt, true, ic);
        Eq(mode, InteractionLogic.Mode.HostShared, "stream: stay shared while want");
        mode = InteractionLogic.Step(mode, 1.2, ref ent, ref wantAt, false, ic);
        Eq(mode, InteractionLogic.Mode.HostShared, "stream: dwell holds after clear");
        mode = InteractionLogic.Step(mode, 1.35, ref ent, ref wantAt, false, ic);
        Eq(mode, InteractionLogic.Mode.HostShared, "stream: resume window holds");
        mode = InteractionLogic.Step(mode, 1.6, ref ent, ref wantAt, false, ic);
        Eq(mode, InteractionLogic.Mode.Owner, "stream: single leave to Owner");

        // --- Y floor: airborne extrap must not dive below MaxBelowNewestM (D2) ---
        var fall = new StatePlayback(new StatePlayback.Cfg { MinDelayS = 0, MaxDelayS = 0, DelayEwma = 1, MaxExtrapS = 0.5, MaxBelowNewestM = 0.5 });
        fall.Push(new StatePlayback.Sample
        {
            Seq = 1, SendTime = 0, Epoch = 1, Px = 0, Py = 10, Pz = 0, Qw = 1,
            Vx = 0, Vy = -20, Vz = 0, Flags = CarWire.FlagAirborne,
        }, 0);
        var plunged = fall.SampleAt(0.4, delayOverride: 0); // would be y=10-8=2 without floor; floor=10-0.5=9.5
        Eq(plunged.Ok && plunged.Extrapolated && plunged.Py >= 9.5f - 1e-3f, true,
            $"stream: airborne Y floor ({plunged.Py:0.00})");
    }

    static StatePlayback.Sample ToPb(in CarWire.CarStateSample s, byte epoch) => new()
    {
        Seq = s.Seq, SendTime = s.SendTime, Epoch = epoch,
        Px = s.Px, Py = s.Py, Pz = s.Pz,
        Qx = s.Qx, Qy = s.Qy, Qz = s.Qz, Qw = s.Qw,
        Vx = s.Vx, Vy = s.Vy, Vz = s.Vz,
        Wx = s.Wx, Wy = s.Wy, Wz = s.Wz,
        Flags = s.Flags, SteerDeg = s.SteerDeg, WheelCount = s.WheelCount,
        W0 = s.W0, W1 = s.W1, W2 = s.W2, W3 = s.W3, W4 = s.W4, W5 = s.W5,
    };

    /// <summary>0.2.3: §5.1–5.2 acceptance metric formulas.</summary>
    static void AcceptMetrics()
    {
        Eq(double.IsNaN(AcceptMetricsLogic.WheelErrFrac(10, 1.0)), true, "accept: slow body no wheelErr");
        // ω = v/r → 0 error (r=0.38 → ω = 10/0.38 ≈ 26.315)
        var perfect = 10.0 / WheelSpinLogic.DefaultRadius;
        Eq(AcceptMetricsLogic.WheelErrFrac(perfect, 10.0) < 1e-9, true, "accept: perfect spin");
        Eq(AcceptMetricsLogic.WheelErrOverLimit(AcceptMetricsLogic.WheelErrFrac(perfect * 1.2, 10.0)), true, "accept: +20% over");
        Eq(AcceptMetricsLogic.WheelErrOverLimit(AcceptMetricsLogic.WheelErrFrac(perfect * 1.05, 10.0)), false, "accept: +5% under limit");
        Eq(AcceptMetricsLogic.WheelErrOverLimit(0.10), false, "accept: frac==limit not over (>)");
        Eq(AcceptMetricsLogic.WheelErrOverLimit(0.10 + 1e-12), true, "accept: frac just over limit");
        Eq(double.IsNaN(AcceptMetricsLogic.WheelErrFrac(double.NaN, 10.0)) == false
            && AcceptMetricsLogic.WheelErrFrac(double.NaN, 10.0) >= 0.99, true, "accept: NaN spin → treat as 0 vs body");
        Eq(AcceptMetricsLogic.WheelErrFrac(perfect, 10.0, 1e-6) < 1e-6, true, "accept: bad radius falls back to default");
        // reverse: signed bodyV matches signed spin (unsigned |v| would look like ~200% error)
        var revSpin = -10.0 / WheelSpinLogic.DefaultRadius;
        Eq(AcceptMetricsLogic.WheelErrFrac(revSpin, -10.0) < 1e-9, true, "accept: reverse perfect");
        Eq(AcceptMetricsLogic.BodyForwardMs(0, 0, 10, 0, 0, 0, 1), 10.0, "accept: bodyFwd identity → vz");
        Eq(Math.Abs(AcceptMetricsLogic.BodyForwardMs(10, 0, 0, 0, 0.70710678, 0, 0.70710678) - 10.0) < 1e-4, true,
            "accept: bodyFwd yaw90 → vx");
        Eq(AcceptMetricsLogic.ExtrapErrM(0, 0, 0, 3, 4, 0), 5.0, "accept: extrapErr 3-4-5");
        double since = -1;
        Eq(double.IsNaN(AcceptMetricsLogic.ProxyPullStep(ref since, 0.05, 1.0)), true, "accept: pull idle");
        Eq(double.IsNaN(AcceptMetricsLogic.ProxyPullStep(ref since, 0.2, 1.0)), true, "accept: pull open");
        Eq(since >= 0, true, "accept: pull armed");
        Eq(double.IsNaN(AcceptMetricsLogic.ProxyPullStep(ref since, 0.1, 1.1)), true, "accept: pull still open");
        var done = AcceptMetricsLogic.ProxyPullStep(ref since, 0.04, 1.25);
        Eq(Math.Abs(done - 0.25) < 1e-9, true, $"accept: pull done {done:0.000}s");
        Eq(since < 0, true, "accept: pull cleared");
        since = 2.0;
        var ab = AcceptMetricsLogic.ProxyPullAbort(ref since, 2.4);
        Eq(Math.Abs(ab - 0.4) < 1e-9 && since < 0, true, $"accept: abort age {ab:0.000}");
        Eq(double.IsNaN(AcceptMetricsLogic.ProxyPullAbort(ref since, 3.0)), true, "accept: abort idle");
        Eq(AcceptMetricsLogic.ProxyPullOpenAge(1.0, 1.5), 0.5, "accept: open age");
        Eq(double.IsNaN(AcceptMetricsLogic.ProxyPullOpenAge(-1, 1.5)), true, "accept: open age idle");
    }

    /// <summary>0.2.3 T3.2: roll wrap / integrate / soft-pull / susp (§8.2).</summary>
    static void WheelVisual()
    {
        Eq(WheelVisualLogic.WrapDeg360(370), 10f, "wheel: wrap 370");
        Eq(WheelVisualLogic.WrapDeg360(-10), 350f, "wheel: wrap -10");
        Eq(WheelVisualLogic.WrapDeg360(float.NaN), 0f, "wheel: wrap NaN");
        Eq(Math.Abs(WheelVisualLogic.IntegrateRollDeg(0, (float)Math.PI, 0.1f) - 18f) < 1e-3f, true, "wheel: integrate π·0.1s");
        Eq(WheelVisualLogic.IntegrateRollDeg(350, (float)Math.PI, 0.1f), 8f, "wheel: integrate wrap");
        Eq(WheelVisualLogic.IntegrateRollDeg(10, - (float)Math.PI, 0.1f), 352f, "wheel: integrate reverse");
        Eq(WheelVisualLogic.IntegrateRollDeg(10, 5, 0), 10f, "wheel: integrate dt0");
        Eq(WheelVisualLogic.IntegrateRollDeg(10, 5, 1f), 10f, "wheel: integrate dt too large");
        Eq(Math.Abs(WheelVisualLogic.SuspDeltaM(0.5f, 0.42f) - (-0.08f)) < 1e-5f, true, "wheel: susp compress");
        Eq(WheelVisualLogic.CanCaptureRestPos(0.1f), true, "wheel: rest pos ok slow");
        Eq(WheelVisualLogic.CanCaptureRestPos(0.5f), true, "wheel: rest pos at maxSpeed");
        Eq(WheelVisualLogic.CanCaptureRestPos(2f), false, "wheel: rest pos blocked by speed");
        Eq(WheelVisualLogic.CanCaptureRestPos(-0.2f), true, "wheel: rest pos ok reverse slow");
        Eq(Math.Abs(WheelVisualLogic.DeltaDeg(10, 350) - (-20f)) < 1e-3f, true, "wheel: delta wrap");
        // soft-pull: from 0 toward 180 with spin 0, dt 0.1 → max 9 deg
        Eq(Math.Abs(WheelVisualLogic.SoftPullRoll(0, 180, 0, 0.1f) - 9f) < 1e-3f, true, "wheel: soft-pull cap");
        Eq(WheelVisualLogic.SoftPullRoll(10, 12, 0, 0.1f), 12f, "wheel: soft-pull small");
        // BlendWheels: integrate π·0.1s = 18°, then soft-pull toward 40° (cap +9) → 27°
        var spun = WheelVisualLogic.SoftPullRoll(0, 40, (float)Math.PI, 0.1f);
        Eq(Math.Abs(spun - 27f) < 1e-2f, true, $"wheel: soft-pull with spin ({spun})");
        Eq(NetInfoLogic.MaskIp("peer 26.1.2.3 joined", "26.1.2.3", "26.10.20.30"), "peer 26.10.20.30 joined", "netinfo: mask real→demo");
        Eq(NetInfoLogic.MaskIp("peer 26.1.2.3 joined", "", "26.10.20.30"), "peer 26.1.2.3 joined", "netinfo: no real → no mask");
        Eq(NetInfoLogic.MaskIp("x", "26.1.2.3", null), "x", "netinfo: no demo → no mask");
        Eq(NetInfoLogic.MaskIp(null, "26.1.2.3", "d"), null, "netinfo: null s");
        // T3.6 FlagReverse thresholds (same for sender + Fusion lights)
        Eq(VehicleLightLogic.WantReverse(-1.0f, 0f), true, "lights: reverse rolling back");
        Eq(VehicleLightLogic.WantReverse(-0.8f, 0f), false, "lights: reverse strict < -0.8");
        Eq(VehicleLightLogic.WantReverse(-2f, 0.1f), false, "lights: reverse blocked by throttle");
        Eq(VehicleLightLogic.WantReverse(1f, 0f), false, "lights: forward no reverse");
        Eq(VehicleLightLogic.WantReverse(-0.79f, 0f), false, "lights: just above reverse speed");
        Eq(VehicleLightLogic.WantBrake(0.11f), true, "lights: brake above min");
        Eq(VehicleLightLogic.WantBrake(0.1f), false, "lights: brake at min not over");
        Eq(VehicleLightLogic.WantBrake(0.05f), false, "lights: brake was Fusion-only 0.05 — now unified 0.1");
    }
}
