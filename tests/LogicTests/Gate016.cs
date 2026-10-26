using System;
using System.Collections.Generic;
using System.Linq;
using OthLan;
using T = OthLan.GateLogic.Tier;
using O = OthLan.GateLogic.Outcome;

/// <summary>0.1.6: version gate, slot allocator, code signatures.</summary>
static class Gate016
{
    public static void Run(Action<object, object, string> eq)
    {
        void Eq<X>(X got, X want, string what) => eq(got, want, what);

        // ---- gate tiers ----
        GateLogic.Probe Good() => new()
        {
            FusionVersion = "2.1.2.0", ExpectedFusion = "2.1.2.0",
            Sigs = new() { ["a"] = "10:aa", ["b"] = "20:bb" }, ExpectedSigs = new() { ["a"] = "10:aa", ["b"] = "20:bb" },
            Offsets = new() { ["x"] = 0x78, ["y"] = 0x4 }, ExpectedOffsets = new() { ["x"] = 0x78, ["y"] = 0x4 },
        };
        var p = Good(); p.HashMatch = true; p.Sigs["a"] = "changed";
        Eq(GateLogic.Classify(p, out _), T.Supported, "hash match => supported (sigs not consulted)");
        Eq(GateLogic.Classify(Good(), out var r0), T.Unverified, "unknown files, all checks equal => unverified");
        Eq(r0.Count, 0, "clean unverified has no reasons");
        p = Good(); p.FusionVersion = "2.1.3.0"; Eq(GateLogic.Classify(p, out var r1), T.Incompatible, "other Fusion version => incompatible");
        Eq(r1.Any(x => x.Contains("Fusion 2.1.3.0")), true, "reason names the Fusion version");
        p = Good(); p.FusionVersion = null; Eq(GateLogic.Classify(p, out _), T.Incompatible, "unreadable Fusion version => incompatible");
        p = Good(); p.Sigs["b"] = "20:cc"; Eq(GateLogic.Classify(p, out var r2), T.Unverified, "codeSig diff alone => unverified (soft)");
        Eq(r2.Single().StartsWith("code changed: b"), true, "soft reason names the method");
        p = Good(); p.Sigs.Remove("a"); Eq(GateLogic.Classify(p, out var rMiss), T.Unverified, "missing probed sig => unverified (soft)");
        Eq(rMiss.Single().Contains("missing"), true, "soft reason says missing");
        p = Good(); p.Offsets["x"] = 0x80; Eq(GateLogic.Classify(p, out var r3), T.Incompatible, "moved field => incompatible");
        Eq(r3.Single().Contains("0x80 != 0x78"), true, "reason shows both offsets");
        p = Good(); p.Offsets.Remove("y"); Eq(GateLogic.Classify(p, out _), T.Incompatible, "unresolved field => incompatible");
        p = Good(); p.Error = "resolve: MissingMethodException"; Eq(GateLogic.Classify(p, out _), T.Incompatible, "resolve error => incompatible");
        p = Good(); p.ExpectedSigs.Clear(); Eq(GateLogic.Classify(p, out _), T.Incompatible, "manifest without sigs never unverified");
        p = Good(); p.ExpectedOffsets.Clear(); Eq(GateLogic.Classify(p, out _), T.Incompatible, "manifest without offsets never unverified");
        p = Good(); p.Sigs["extra"] = "1:00"; Eq(GateLogic.Classify(p, out _), T.Unverified, "extra probed sig is ignored");
        p = Good(); p.Sigs["b"] = "20:cc"; p.Offsets["x"] = 0x80;
        Eq(GateLogic.Classify(p, out var rBoth), T.Incompatible, "layout change still hard even with soft sig");
        Eq(rBoth.Any(x => x.StartsWith("layout changed:")), true, "hard reason kept");
        Eq(rBoth.Any(x => x.StartsWith("code changed:")), true, "soft reason listed after hard");

        Eq(GateLogic.Decide(T.Supported, null), O.Run, "supported runs");
        Eq(GateLogic.Decide(T.Supported, "disable"), O.Run, "supported ignores a stale choice");
        Eq(GateLogic.Decide(T.Unverified, null), O.Ask, "unverified asks");
        Eq(GateLogic.Decide(T.Unverified, "continue"), O.Run, "unverified + continue runs");
        Eq(GateLogic.Decide(T.Unverified, "disable"), O.DisabledByUser, "unverified + disable stays off");
        Eq(GateLogic.Decide(T.Incompatible, "continue"), O.Off, "incompatible ignores continue (no bypass)");

        var c = GateLogic.WithChoice("", "aaa", "continue");
        c = GateLogic.WithChoice(c, "bbb", "disable");
        c = GateLogic.WithChoice(c, "AAA", "disable");
        var pc = GateLogic.ParseChoices(c);
        Eq(pc.Count, 2, "choices: one per hash (case-insensitive)");
        Eq(pc["aaa"], "disable", "choices: latest answer wins");
        Eq(GateLogic.ParseChoices("x=maybe;=continue;junk;y=CONTINUE").Count, 1, "choices: junk ignored, value case-insensitive");
        var many = ""; for (int i = 0; i < 12; i++) many = GateLogic.WithChoice(many, "h" + i, "continue");
        Eq(GateLogic.ParseChoices(many).Count, 8, "choices: capped at 8");
        Eq(GateLogic.ParseChoices(many).ContainsKey("h11"), true, "choices: newest kept");

        Eq(GateLogic.BuildKey("26062472", "57da2280d66760a9xxxx"), "26062472", "build key: known build id");
        Eq(GateLogic.BuildKey(null, "57da2280d66760a93eab"), "ga:57da2280d66760a9", "build key: hash prefix for unknown");
        Eq(GateLogic.SameBuild("26062472", "abc", "ga:x", "ABC"), true, "same hash => same build even if keys differ");
        Eq(GateLogic.SameBuild("26062472", "abc", "26062472", "abd"), false, "different hash => blocked");
        Eq(GateLogic.SameBuild("26062472", null, "26062472", "abc"), true, "old host without hash: keys equal");
        Eq(GateLogic.SameBuild("26062472", null, "ga:1234", "abc"), false, "old host without hash: keys differ => blocked");
        Eq(GateLogic.CompatibleSession("26062472", "abc", "ga:025e", "025e…", "2.1.2.0", "2.1.2.0"), true, "Steam↔third-party: same Fusion");
        Eq(GateLogic.CompatibleSession("26062472", "abc", "ga:025e", "025e…", "2.1.2.0", "2.1.3.0"), false, "different Fusion blocked");
        Eq(GateLogic.CompatibleSession("26062472", "abc", "ga:025e", "025e…", "2.1.2.0", null), false, "missing my Fusion falls back to SameBuild (fail)");
        Eq(GateLogic.CompatibleSession("26062472", "abc", "ga:x", "ABC", null, "2.1.2.0"), true, "same hash still wins without Fusion");

        // ---- slot allocator (players 4, host index 1 => client slots 0, 2, 3) ----
        var a = new SlotAllocator(4, 1);
        Eq(a.Request("A", 2, 0), 2, "slot: hint honoured when free");
        Eq(a.Request("B", 2, 1), 0, "slot: taken hint => lowest free");
        Eq(a.Request("C", 0, 2), 3, "slot: third client gets the last one");
        Eq(a.Request("D", 0, 3), -1, "slot: full => -1");
        Eq(a.Request("A", 0, 4), 2, "slot: same id gets the same reservation again");
        Eq(a.Request("", 0, 4), -1, "slot: empty id refused");
        uint tA = SlotAllocator.Tag("A"), tB = SlotAllocator.Tag("B"), tC = SlotAllocator.Tag("C");
        Eq(tA != tB && tB != tC && tA != 0, true, "tags differ and are non-zero");
        Eq(a.Admit(2, tA, 5, out _), true, "admit: reserved slot, right tag");
        Eq(a.Admit(2, tB, 5, out var why), false, "admit: reserved slot, wrong tag refused");
        Eq(why.Contains("reserved"), true, "admit: says reserved");
        Eq(a.Admit(1, tA, 5, out _), false, "admit: host slot refused");
        Eq(a.Admit(7, tA, 5, out _), false, "admit: out of range refused");
        Eq(a.Admit(2, 0, 5, out _), false, "admit: untagged client cannot take a reservation");
        // A connects; B and C never connect
        var valid = new HashSet<int> { 2 };
        a.Sync(i => valid.Contains(i), 6);
        Eq(a.Admit(2, tB, 7, out why), false, "admit: connected slot refused to others");
        Eq(why.Contains("in use"), true, "admit: says in use");
        a.Sync(i => valid.Contains(i), 60);   // reservations of B (t=1) and C (t=2) expire after 45 s
        Eq(a.Describe(), "2:in:A", "slot: unconnected reservations expire, connected stays");
        Eq(a.Request("D", 3, 61), 3, "slot: freed by expiry => available to a new client");
        // A leaves, reconnects
        valid.Clear();
        var rel = a.Sync(i => valid.Contains(i), 62);
        Eq(string.Join(",", rel), "2", "slot: player left => released");
        Eq(a.Request("A", 0, 63), 2, "slot: reconnect gets the same slot (preference kept)");
        valid.Add(2); a.Sync(i => valid.Contains(i), 64);
        // A's slot taken by E while A is away, then A comes back
        var b = new SlotAllocator(4, 1);
        Eq(b.Request("A", 0, 0), 0, "slot2: A -> 0");
        b.Sync(i => i == 0, 1);
        b.Sync(i => false, 2);               // A left
        Eq(b.Request("E", 0, 3), 0, "slot2: E may take the free slot 0");
        Eq(b.Request("A", 0, 4), 2, "slot2: A gets another free slot, not a duplicate");
        // fallback (IP-derived) clients
        var f = new SlotAllocator(4, 1);
        Eq(f.Admit(3, tC, 0, out _), true, "fallback: free unreserved slot admitted");
        Eq(f.Admit(3, tB, 1, out _), false, "fallback: second client on the same slot refused");
        Eq(f.Request("B", 3, 2), 0, "fallback slot counts as taken for requests");
        Eq(f.Admit(3, tC, 3, out _), true, "fallback: same client retrying is admitted");
        // session reset
        f.Reset(4); Eq(f.Count, 0, "reset clears reservations");
        Eq(f.Request("B", 3, 4), 0, "reset keeps the id preference");
        f.Reset(2); Eq(f.Request("B", 0, 5), 0, "2-player session: only slot 0");
        Eq(f.Request("Z", 0, 5), -1, "2-player session: second client full");
        // 3 clients joining at once with the same IP-derived hint (the 0.1.5 collision case)
        var g = new SlotAllocator(4, 1);
        var got = new[] { g.Request("p1", 3, 0), g.Request("p2", 3, 0), g.Request("p3", 3, 0) };
        Eq(string.Join(",", got.OrderBy(x => x)), "0,2,3", "3 clients, same hint => 3 distinct slots");
        // randomized: never two ids on one slot, never the host slot
        var rnd = new Random(1);
        var h = new SlotAllocator(4, 1); var conn = new HashSet<int>(); var ok = true; double now = 0;
        for (int i = 0; i < 5000; i++)
        {
            now += rnd.NextDouble() * 5;
            var id = "c" + rnd.Next(6);
            var s = h.Request(id, rnd.Next(4), now);
            if (s == 1) ok = false;
            if (s >= 0 && rnd.Next(3) == 0 && h.Admit(s, SlotAllocator.Tag(id), now, out _)) conn.Add(s);
            if (rnd.Next(10) == 0) conn.Remove(rnd.Next(4));
            h.Sync(x => conn.Contains(x), now);
            var desc = h.Describe().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ids = desc.Select(d => d.Split(':')[2]).ToList();
            if (ids.Count != ids.Distinct().Count()) ok = false;
            if (desc.Any(d => d.StartsWith("1:"))) ok = false;
        }
        Eq(ok, true, "randomized 5000 steps: no duplicate id, no host slot");

        // ---- code signatures ----
        var x1 = new byte[] { 0xE8, 1, 2, 3, 4, 0x48, 0x8B, 0x05, 9, 9, 9, 9, 0x74, 0x10, 0x48, 0x8B, 0x41, 0x78, 0xC3 };
        var x2 = new byte[] { 0xE8, 5, 6, 7, 8, 0x48, 0x8B, 0x05, 7, 7, 7, 7, 0x74, 0x10, 0x48, 0x8B, 0x41, 0x78, 0xC3 };
        var x3 = (byte[])x2.Clone(); x3[17] = 0x7C;
        Eq(CodeSig.Compute(x1, x1.Length, false), CodeSig.Compute(x2, x2.Length, false), "sig: call target and RIP displacement masked");
        Eq(CodeSig.Compute(x2, x2.Length, false) != CodeSig.Compute(x3, x3.Length, false), true, "sig: field offset change detected");
        var leafX = new byte[] { 0x48, 0x8B, 0x41, 0x78, 0xC3, 0xCC, 0xCC, 0x90 };
        Eq(CodeSig.Compute(leafX, leafX.Length, true).StartsWith("5:"), true, "sig: leaf stops after ret");
    }
}
