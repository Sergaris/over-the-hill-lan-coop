// usage: BuildCheck <GameAssembly.dll> <global-metadata.dat> <supported-builds.json> <unityVersion>
using System; using System.IO; using System.Linq; using System.Text.Json; using AssetRipper.Primitives; using LibCpp2IL; using OthLan;
var pe = File.ReadAllBytes(args[0]);
LibCpp2IlMain.Settings.AllowManualMetadataAndCodeRegInput = false;
if (!LibCpp2IlMain.LoadFromFile(args[0], args[1], UnityVersion.Parse(args[3]))) { Console.WriteLine("load failed"); return 1; }
using var doc = JsonDocument.Parse(File.ReadAllText(args[2]));
var b0 = doc.RootElement.GetProperty("builds").EnumerateArray().Last();
int I32(int o) => BitConverter.ToInt32(pe, o);
var peOff = I32(0x3C); int nSec = BitConverter.ToUInt16(pe, peOff + 6); int optSize = BitConverter.ToUInt16(pe, peOff + 20); var opt = peOff + 24;
var excRva = I32(opt + 112 + 3 * 8); var excSize = I32(opt + 112 + 3 * 8 + 4);
var secs = Enumerable.Range(0, nSec).Select(i => opt + optSize + i * 40).Select(s => (va: I32(s + 12), vsize: I32(s + 8), raw: I32(s + 20), rawSize: I32(s + 16))).ToArray();
int FileOff(int rva) { var s = secs.First(x => rva >= x.va && rva < x.va + Math.Max(x.vsize, x.rawSize)); return rva - s.va + s.raw; }
var ex = FileOff(excRva); var n = excSize / 12;
(int b, int e)? Lookup(int rva) { int lo = 0, hi = n - 1; while (lo <= hi) { var mid = (lo + hi) / 2; var b = I32(ex + mid * 12); var e = I32(ex + mid * 12 + 4); if (rva < b) hi = mid - 1; else if (rva >= e) lo = mid + 1; else return (b, e); } return null; }
string Sig(int rva) { var rf = Lookup(rva); var leaf = rf == null; var len = leaf ? CodeSig.LeafMaxBytes : rf.Value.e - rva; var bytes = pe.Skip(FileOff(rva)).Take(Math.Min(len, CodeSig.MaxBytes)).ToArray(); return CodeSig.Compute(bytes, bytes.Length, leaf); }
var methods = LibCpp2IlMain.TheMetadata.methodDefs;
int bad = 0;
foreach (var (sec, sigSec) in new[] { ("rva", "codeSig"), ("optionalRva", "optionalSig") })
{
  b0.TryGetProperty(sigSec, out var sigs);
  foreach (var it in b0.GetProperty(sec).EnumerateObject())
  {
    var key = it.Name; var cut = key.IndexOf("::"); var tname = key.Substring(0, cut); var rest = key.Substring(cut + 2);
    var p = rest.IndexOf('('); var mname = p >= 0 ? rest.Substring(0, p) : rest;
    int? pc = p >= 0 ? (rest.Substring(p + 1).TrimEnd(')').Length == 0 ? 0 : rest.Substring(p + 1).TrimEnd(')').Split(',').Length) : null;
    var tn = tname.Replace('+', '/');
    var cands = methods.Where(m => m.Name != null).Where(m => (m.Name == mname || m.Name.EndsWith("." + mname)) && m.DeclaringType != null && (m.DeclaringType.FullName == tname || m.DeclaringType.FullName == tn || m.DeclaringType.FullName?.Replace('/', '+') == tname) && (pc == null || m.parameterCount == pc)).ToArray();
    var oldSig = sigs.ValueKind == JsonValueKind.Object && sigs.TryGetProperty(key, out var s0) ? s0.GetString() : null;
    if (cands.Length == 0) { Console.WriteLine($"MISSING {sec} {key}"); if (sec == "rva") bad++; continue; }
    foreach (var m in cands)
    {
      var rva = (int)m.Rva; var oldRva = it.Value.GetString(); var sig = rva > 0 ? Sig(rva) : "-";
      var same = oldSig == null ? "nosig" : sig == oldSig ? "SAME" : "DIFF";
      if (same == "DIFF" && sec == "rva") bad++;
      if (args.Length > 4 && same == "DIFF") try { Dump(rva, Path.Combine(args[4], key.Replace(':', '_').Replace('(', '_').Replace(')', '_').Replace(',', '_').Replace(' ', '_') + ".asm")); } catch (Exception e) { Console.WriteLine("dump: " + e.Message); }
      Console.WriteLine($"{same} {sec} {key}: rva old {oldRva} new 0x{rva:X} sig old {oldSig ?? "-"} new {sig}" + (cands.Length > 1 ? $" (candidate of {cands.Length})" : ""));
    }
  }
}
return bad;
void Dump(int rva, string file)
{
  var rf = Lookup(rva); var len = rf == null ? CodeSig.LeafMaxBytes : rf.Value.e - rva;
  var bytes = pe.Skip(FileOff(rva)).Take(Math.Min(len, CodeSig.MaxBytes)).ToArray();
  var dec = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(bytes), 0x10000000UL);
  var fmt = new Iced.Intel.IntelFormatter(); var outp = new Iced.Intel.StringOutput(); using var w = new StreamWriter(file);
  while (dec.IP < 0x10000000UL + (ulong)bytes.Length)
  {
    var ins = dec.Decode();
    if (ins.IsIPRelativeMemoryOperand || ins.FlowControl is Iced.Intel.FlowControl.Call or Iced.Intel.FlowControl.UnconditionalBranch or Iced.Intel.FlowControl.ConditionalBranch)
      { w.WriteLine($"{ins.Mnemonic} <addr>"); continue; }
    fmt.Format(ins, outp); w.WriteLine(outp.ToStringAndReset());
  }
}
