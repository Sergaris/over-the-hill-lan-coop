using System;
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

    public static byte[] Embedded(string name)
    {
        using var s = typeof(Payload).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    /// <summary>Embedded copy, else a file next to the exe, else download. Always sha256-verified.</summary>
    public static string Obtain(string resourceName, string fileName, string url, string sha256, string workDir)
    {
        var dest = Path.Combine(workDir, fileName);
        var bytes = Embedded(resourceName);
        if (bytes != null) { File.WriteAllBytes(dest, bytes); Log.Info($"{fileName}: using embedded copy"); }
        else if (File.Exists(Path.Combine(AppContext.BaseDirectory, fileName)))
        { File.Copy(Path.Combine(AppContext.BaseDirectory, fileName), dest, true); Log.Info($"{fileName}: using local copy next to installer"); }
        else
        {
            Log.Info($"{fileName}: downloading {url}");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            File.WriteAllBytes(dest, http.GetByteArrayAsync(url).GetAwaiter().GetResult());
        }
        var h = Sha256(dest);
        if (!h.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{fileName}: sha256 {h} != expected {sha256}");
        Log.Ok($"{fileName}: sha256 verified");
        return dest;
    }
}
