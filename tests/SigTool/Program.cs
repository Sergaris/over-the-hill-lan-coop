using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OthLan;

// usage: SigTool <GameAssembly.dll> <supported-builds.json> [buildId] [rva|optionalRva]  (prints codeSig / optionalSig)
var pe = File.ReadAllBytes(args[0]);
using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
var build = doc.RootElement.GetProperty("builds").EnumerateArray()
    .First(b => args.Length < 3 || args[2] == "-" || b.GetProperty("buildId").GetString() == args[2]);
var section = args.Length > 3 ? args[3] : "rva";

int I32(int o) => BitConverter.ToInt32(pe, o);
var peOff = I32(0x3C);
int nSec = BitConverter.ToUInt16(pe, peOff + 6);
int optSize = BitConverter.ToUInt16(pe, peOff + 20);
var opt = peOff + 24;
var excRva = I32(opt + 112 + 3 * 8); var excSize = I32(opt + 112 + 3 * 8 + 4);
var secs = Enumerable.Range(0, nSec).Select(i => opt + optSize + i * 40)
    .Select(s => (va: I32(s + 12), vsize: I32(s + 8), raw: I32(s + 20), rawSize: I32(s + 16))).ToArray();
int FileOff(int rva) { var s = secs.First(x => rva >= x.va && rva < x.va + Math.Max(x.vsize, x.rawSize)); return rva - s.va + s.raw; }

var ex = FileOff(excRva); var n = excSize / 12;
(int b, int e)? Lookup(int rva)
{
    int lo = 0, hi = n - 1;
    while (lo <= hi)
    {
        var mid = (lo + hi) / 2; var b = I32(ex + mid * 12); var e = I32(ex + mid * 12 + 4);
        if (rva < b) hi = mid - 1; else if (rva >= e) lo = mid + 1; else return (b, e);
    }
    return null;
}

Console.WriteLine(section == "rva" ? "\"codeSig\": {" : "\"optionalSig\": {");
var items = build.GetProperty(section).EnumerateObject().ToArray();
for (int k = 0; k < items.Length; k++)
{
    var rva = Convert.ToInt32(items[k].Value.GetString(), 16);
    var rf = Lookup(rva);
    var leaf = rf == null;
    var len = leaf ? CodeSig.LeafMaxBytes : rf.Value.e - rva;
    var off = FileOff(rva);
    var bytes = pe.Skip(off).Take(Math.Min(len, CodeSig.MaxBytes)).ToArray();
    var sig = CodeSig.Compute(bytes, bytes.Length, leaf);
    Console.WriteLine($"  \"{items[k].Name}\": \"{sig}\"{(k < items.Length - 1 ? "," : "")}   ");
    Console.Error.WriteLine($"{items[k].Name}: rva 0x{rva:X} {(leaf ? "leaf" : $"pdata 0x{rf.Value.b:X}-0x{rf.Value.e:X}")} -> {sig}");
}
Console.WriteLine("}");
