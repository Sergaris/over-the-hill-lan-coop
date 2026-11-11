using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OthLan.Installer;

static class Payload
{
    public static JsonDocument Manifest() =>
        JsonDocument.Parse(typeof(Payload).Assembly.GetManifestResourceStream("supported-builds.json"));

    public static string Sha256(string file)
    {
        using var f = File.OpenRead(file);
        using var s = SHA256.Create();
        return Convert.ToHexString(s.ComputeHash(f)).ToLowerInvariant();
    }

    public static string Sha256(byte[] bytes)
    {
        using var s = SHA256.Create();
        return Convert.ToHexString(s.ComputeHash(bytes)).ToLowerInvariant();
    }

    public static byte[] Embedded(string name)
    {
        using var s = typeof(Payload).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    /// <summary>
    /// Embedded copy, else a file next to the exe, else download. Always sha256-verified.
    /// If embedded or local fails the hash check, logs a warning and falls back to the next source
    /// (eventually the manifest URL). Throws only when every available source fails verification.
    /// </summary>
    public static string Obtain(string resourceName, string fileName, string url, string sha256, string workDir)
    {
        var dest = Path.Combine(workDir, fileName);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var candidates = new List<(string label, string sha)>();

        var embedded = Embedded(resourceName);
        if (embedded != null)
        {
            var h = Sha256(embedded);
            blobs["embedded"] = embedded;
            candidates.Add(("embedded", h));
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(localPath))
        {
            var local = File.ReadAllBytes(localPath);
            var h = Sha256(local);
            blobs["local"] = local;
            candidates.Add(("local", h));
        }

        // Download last so a good embedded/local never hits the network.
        byte[] downloaded = null;
        string downloadSha = null;
        // Only download if nothing before already matches (avoids needless traffic).
        var early = PayloadLogic.PickFirstMatching(sha256, candidates, out _);
        if (early == null)
        {
            Log.Info($"{fileName}: downloading {url}");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            downloaded = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            downloadSha = Sha256(downloaded);
            blobs["download"] = downloaded;
            candidates.Add(("download", downloadSha));
        }

        var pick = PayloadLogic.PickFirstMatching(sha256, candidates, out var rejected);
        foreach (var r in rejected)
            Log.Warn(PayloadLogic.MismatchMessage(fileName, r, blobs.TryGetValue(r, out var b) ? Sha256(b) : "?", sha256) + " — falling back");

        if (pick == null)
            throw new InvalidDataException(
                $"{fileName}: sha256 mismatch for every source (expected {sha256}"
                + (rejected.Length > 0 ? $"; rejected: {string.Join(", ", rejected)}" : "") + ")");

        File.WriteAllBytes(dest, blobs[pick]);
        if (pick == "embedded") Log.Info($"{fileName}: using embedded copy");
        else if (pick == "local") Log.Info($"{fileName}: using local copy next to installer");
        else Log.Info($"{fileName}: using downloaded copy");
        Log.Ok($"{fileName}: sha256 verified");
        return dest;
    }
}
