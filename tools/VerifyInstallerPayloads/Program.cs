using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Post-build check: every installer payload EmbeddedResource must match
/// manifest/supported-builds.json sha256 (bepinex.zip, and unity-libs.zip when present).
/// Usage: VerifyInstallerPayloads &lt;OTH-LAN-Setup.dll-or-exe&gt; &lt;supported-builds.json&gt;
/// Fails (exit 1) on missing bepinex.zip resource or hash mismatch.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: VerifyInstallerPayloads <assembly.dll|Setup.exe> <supported-builds.json>");
            return 2;
        }
        var asmPath = Path.GetFullPath(args[0]);
        var manifestPath = Path.GetFullPath(args[1]);
        if (!File.Exists(asmPath)) { Console.Error.WriteLine($"missing assembly: {asmPath}"); return 1; }
        if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"missing manifest: {manifestPath}"); return 1; }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var bx = doc.RootElement.GetProperty("bepinex");
        var wantBep = bx.GetProperty("sha256").GetString()?.ToLowerInvariant();
        var wantLibs = bx.TryGetProperty("unityLibsSha256", out var ul) ? ul.GetString()?.ToLowerInvariant() : null;

        // Prefer a sibling .dll when given a single-file .exe (publish leaves the managed DLL next to it in bin/).
        var loadPath = asmPath;
        if (asmPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var sibling = Path.ChangeExtension(asmPath, ".dll");
            if (File.Exists(sibling)) loadPath = sibling;
            else
            {
                // Look under typical publish intermediate: same dir may only have the bundle.
                // Fall back to scanning the exe for the managed DLL name is unreliable; require .dll.
                Console.Error.WriteLine(
                    "VerifyInstallerPayloads: pass the managed OTH-LAN-Setup.dll (bin/.../win-x64/), not only the single-file exe.");
                return 1;
            }
        }

        var asm = Assembly.LoadFrom(loadPath);
        var names = new HashSet<string>(asm.GetManifestResourceNames() ?? Array.Empty<string>(), StringComparer.Ordinal);
        Console.WriteLine($"VerifyInstallerPayloads: {loadPath}");
        Console.WriteLine($"  resources: {string.Join(", ", names)}");

        var fails = 0;
        fails += Check(asm, names, "bepinex.zip", wantBep, required: true);
        if (names.Contains("unity-libs.zip"))
            fails += Check(asm, names, "unity-libs.zip", wantLibs, required: true);
        else
            Console.WriteLine("  unity-libs.zip: (not embedded — ok for public flavour)");

        if (!names.Contains("OthLan.dll"))
        {
            Console.Error.WriteLine("  OthLan.dll: MISSING embedded plugin");
            fails++;
        }
        else
            Console.WriteLine($"  OthLan.dll: present ({ResourceLen(asm, "OthLan.dll")} bytes)");

        if (!names.Contains("supported-builds.json"))
        {
            Console.Error.WriteLine("  supported-builds.json: MISSING");
            fails++;
        }

        if (fails != 0)
        {
            Console.Error.WriteLine($"VerifyInstallerPayloads: {fails} failure(s)");
            return 1;
        }
        Console.WriteLine("VerifyInstallerPayloads: OK");
        return 0;
    }

    static int Check(Assembly asm, HashSet<string> names, string resource, string wantSha, bool required)
    {
        if (!names.Contains(resource))
        {
            if (!required) { Console.WriteLine($"  {resource}: absent"); return 0; }
            Console.Error.WriteLine($"  {resource}: MISSING (required)");
            return 1;
        }
        var bytes = Read(asm, resource);
        var got = Sha(bytes);
        var len = bytes.Length;
        if (string.IsNullOrEmpty(wantSha))
        {
            Console.Error.WriteLine($"  {resource}: no expected sha256 in manifest");
            return 1;
        }
        if (!got.Equals(wantSha, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"  {resource}: FAIL size={len} sha256={got} expected={wantSha}");
            return 1;
        }
        Console.WriteLine($"  {resource}: OK size={len} sha256={got}");
        return 0;
    }

    static byte[] Read(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static long ResourceLen(Assembly asm, string name) => Read(asm, name).Length;

    static string Sha(byte[] bytes)
    {
        using var s = SHA256.Create();
        return Convert.ToHexString(s.ComputeHash(bytes)).ToLowerInvariant();
    }
}
