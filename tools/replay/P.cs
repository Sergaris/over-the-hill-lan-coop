using System; using System.IO; using System.Linq; using OthLan;
var lines = File.ReadAllLines(args.Length > 0 ? args[0] : "host-trace.csv"); var h = lines[0].Split(',');
int it=Array.IndexOf(h,"t"), ix=Array.IndexOf(h,"rX"), iy=Array.IndexOf(h,"rY"), iz=Array.IndexOf(h,"rZ"), isl=Array.IndexOf(h,"rSlot");
var s = new RemoteSmoother(); var c = new RemoteSmoother.Cfg(); var raw = new Smoothness(); var drawn = new Smoothness(); double lt=-1; string ls="";
foreach (var l in lines.Skip(1)) { var f=l.Split(','); if (f[ix]=="") { s.Reset(); raw.Break(); drawn.Break(); lt=-1; continue; }
  double t=double.Parse(f[it]), x=double.Parse(f[ix]), y=double.Parse(f[iy]), z=double.Parse(f[iz]); if (f[isl]!=ls){ s.Reset(); raw.Break(); drawn.Break(); ls=f[isl]; }
  var dt = lt<0?0:t-lt; lt=t; if (dt<=0) continue; if (dt>0.25){ raw.Break(); drawn.Break(); }
  raw.Add(x,y,z,dt); var d=s.Step(x,y,z,dt,c,false); drawn.Add(d.x,d.y,d.z,dt); }
Console.WriteLine("raw   "+raw.Describe()); Console.WriteLine("drawn "+drawn.Describe()+$" absorbed={s.Absorbed} snaps={s.Snaps}");
