using System; using System.Collections.Generic; using System.Net; using System.Net.Sockets; using System.Threading;
namespace OthLan {
 class Cfg<T>{ public T Value; }
 static class Plugin { public const string Version="0.1.5"; public static Cfg<int> Port=new(){Value=27315};
   public static class L { public static void LogInfo(string s){} public static void LogError(string s)=>Console.WriteLine(s);} }
 static class BuildLock { public static string CurrentBuildId="26062472"; }
 static class CarSync { public static void OnPosePacket(byte[] d, IPEndPoint ep){} public static volatile int HostFlagsValue = 1; public static int HostFlags => HostFlagsValue; public static bool HostEnabled => HostFlagsValue == 1;
   public static volatile int SeenFlags = -1; public static void OnHostFlags(int f, double now){ SeenFlags = f; } }
 static class P {
   static volatile bool drop; static volatile int delayMs;
   // UDP proxy 127.0.0.1:27400 <-> host 27316: simulates latency and total loss; host only ever sees the proxy's source port.
   static void Proxy(){
     var front=new UdpClient(new IPEndPoint(IPAddress.Loopback,27400)); var back=new UdpClient(0); IPEndPoint client=null;
     var host=new IPEndPoint(IPAddress.Loopback,27316);
     new Thread(()=>{ while(true){ var e=new IPEndPoint(IPAddress.Any,0); var d=back.Receive(ref e); if(drop) continue; var c=client; var dl=delayMs;
        ThreadPool.QueueUserWorkItem(_=>{ Thread.Sleep(dl); front.Send(d,d.Length,c);}); } }){IsBackground=true}.Start();
     new Thread(()=>{ while(true){ var e=new IPEndPoint(IPAddress.Any,0); var d=front.Receive(ref e); client=e; if(drop) continue; var dl=delayMs;
        ThreadPool.QueueUserWorkItem(_=>{ Thread.Sleep(dl); back.Send(d,d.Length,host);}); } }){IsBackground=true}.Start();
   }
   static void Run(double seconds){ var end=DateTime.UtcNow.AddSeconds(seconds); while(DateTime.UtcNow<end){ ClientLink.SendPing(2); HostPings.SendAll(); Thread.Sleep(200);} }
   static int Main(){
     HostInfo.Publish("R2A1_Desert",4,new Dictionary<string,string>()); Proxy();
     ClientLink.Ensure("127.0.0.1",27400);
     int fails=0; void Check(bool ok,string what){ Console.WriteLine((ok?"OK   ":"FAIL ")+what); if(!ok) fails++; }
     delayMs=20; Run(2.0);   // 20 ms each way => ~40 ms RTT
     double c; bool to; lock(ClientLink.Tracker){ c=ClientLink.Tracker.Value(NetClock.Now); to=ClientLink.Tracker.TimedOut(NetClock.Now);} 
     var h=HostPings.Snapshot();
     Check(!to && c>35 && c<80, $"client echo RTT {c:0.0} ms (40 ms simulated)");
     Check(h.ContainsKey(2) && !h[2].timedOut && h[2].ms>35 && h[2].ms<80, $"host RTT to slot 2: {(h.ContainsKey(2)?h[2].ms:double.NaN):0.0} ms via client's source port; client echoed {ClientLink.HostPingsEchoed} host pings");
     Check(CarSync.SeenFlags == 1, $"client received the host's CarSync flag ON (got {CarSync.SeenFlags})");
     CarSync.HostFlagsValue = 0; Run(0.6);
     Check(CarSync.SeenFlags == 0, $"client follows host flag OFF within 0.6 s (got {CarSync.SeenFlags})");
     CarSync.HostFlagsValue = 1; Run(0.6);
     Check(CarSync.SeenFlags == 1, "client follows host flag back ON");
     delayMs=100; Run(2.5);
     lock(ClientLink.Tracker) c=ClientLink.Tracker.Value(NetClock.Now);
     Check(c>150 && c<260, $"latency change tracked: {c:0.0} ms (200 ms simulated)");
     drop=true; Run(1.5);
     lock(ClientLink.Tracker) to=ClientLink.Tracker.TimedOut(NetClock.Now);
     Check(!to, "1.5 s of loss: not timed out yet");
     Run(1.0);
     lock(ClientLink.Tracker){ to=ClientLink.Tracker.TimedOut(NetClock.Now); c=ClientLink.Tracker.Value(NetClock.Now);} h=HostPings.Snapshot();
     Check(to && double.IsNaN(c) && PingLogic.ClassifyEcho(c,to,150,250)==PingLogic.Level.Red && PingLogic.Format(c)=="local --", "2.5 s of loss: client 'local --' red");
     Check(h[2].timedOut, $"host side timeout for slot 2 too (loss {h[2].loss:0}%)");
     drop=false; delayMs=20; Run(1.0);
     lock(ClientLink.Tracker){ to=ClientLink.Tracker.TimedOut(NetClock.Now); c=ClientLink.Tracker.Value(NetClock.Now);} 
     Check(!to && c>0, $"recovered: {c:0.0} ms, loss {ClientLink.Tracker.LossPercent:0}%");
     Console.WriteLine(fails==0?"ALL PASSED":"FAILURES: "+fails); return fails; } } }
