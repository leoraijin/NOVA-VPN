using System;using System.IO;using System.Net;using System.Net.Sockets;using System.Text;using System.Linq;using System.Collections;using System.Collections.Generic;using System.Collections.Concurrent;using System.Diagnostics;using System.Reflection;using System.Threading;using System.Threading.Tasks;using System.Web.Script.Serialization;using NovaVpn;
class MultiExitNativeChannelTests {
 static int checks;static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};
 static void Check(bool v,string m){if(!v)throw new Exception(m);checks++;Console.WriteLine("check "+checks+": "+m);}
 static Dictionary<string,object> Obj(params object[] pairs){var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d;}
 sealed class Listener:IDisposable {
  readonly TcpListener listener;readonly Task accept;readonly ConcurrentBag<TcpClient> sockets=new ConcurrentBag<TcpClient>();readonly ConcurrentBag<Task> workers=new ConcurrentBag<Task>();volatile bool stopped;public int Connections;public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
  public Listener(Action<TcpClient> action){listener=new TcpListener(IPAddress.Loopback,0);listener.Start();accept=Task.Factory.StartNew(()=>{while(!stopped){TcpClient c;try{c=listener.AcceptTcpClient();}catch(ObjectDisposedException){if(stopped)break;throw;}catch(SocketException){if(stopped)break;throw;}if(stopped){c.Close();break;}sockets.Add(c);Interlocked.Increment(ref Connections);workers.Add(Task.Run(()=>{try{action(c);}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}finally{c.Close();}}));}},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}
  public void Dispose(){if(stopped)return;int port=Port;stopped=true;
   // Wake the dedicated blocking accept before closing its listening socket.
   // The wake connection is discarded and never counted or forwarded.
   try{using(var wake=new TcpClient())wake.Connect(IPAddress.Loopback,port);if(!accept.Wait(5000))throw new Exception("Fixture accept did not terminate at "+port);}finally{listener.Stop();foreach(var c in sockets)c.Close();}
   if(!Task.WaitAll(workers.ToArray(),5000))throw new Exception("Fixture workers did not terminate at "+port);
  }
 }
 static void Echo(TcpClient c){using(c){var s=c.GetStream();s.ReadTimeout=4000;s.WriteTimeout=4000;byte[] b=new byte[1024];for(int n;(n=s.Read(b,0,b.Length))>0;)s.Write(b,0,n);}}
 static void Relay(TcpClient c,int backend){using(c)using(var remote=new TcpClient()){remote.Connect(IPAddress.Loopback,backend);var a=c.GetStream();var b=remote.GetStream();var forward=a.CopyToAsync(b);var reverse=b.CopyToAsync(a);try{Task.WaitAny(forward,reverse);}finally{c.Close();remote.Close();try{Task.WaitAll(new[]{forward,reverse},3000);}catch(AggregateException){}}}}
 static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();try{return((IPEndPoint)l.LocalEndpoint).Port;}finally{l.Stop();}}
 static Process Start(string core,string config,string log){var p=new Process{StartInfo=new ProcessStartInfo(core,"run -c \""+config+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};var writer=new StreamWriter(log,false,Encoding.UTF8){AutoFlush=true};p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(writer)writer.WriteLine(e.Data);};p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(writer)writer.WriteLine(e.Data);};p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();Logs.Add(p.Id,writer);Thread.Sleep(400);if(p.HasExited)throw new Exception("Native proxy-only fixture exited: "+log);return p;}
 static readonly Dictionary<int,StreamWriter> Logs=new Dictionary<int,StreamWriter>();
 static void Stop(Process p){if(p==null)return;int id=p.Id;try{if(!p.HasExited){p.Kill();if(!p.WaitForExit(5000))throw new Exception("Native fixture did not stop");}p.WaitForExit();}finally{p.Dispose();if(Logs.TryGetValue(id,out var log)){log.Dispose();Logs.Remove(id);}}}
 static void NativeCheck(string core,string config){using(var p=Process.Start(new ProcessStartInfo(core,"check -c \""+config+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){var a=p.StandardOutput.ReadToEndAsync();var b=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(10000)){p.Kill();p.WaitForExit();throw new Exception("Native check timeout");}Check(p.ExitCode==0,"Native configuration check: "+b.GetAwaiter().GetResult());a.GetAwaiter().GetResult();}}
 static NetworkStream OpenTunnel(TcpClient client, LocalProxyEndpoint channel, int target, string host, bool bad = false)
 {
     client.Connect(IPAddress.Loopback, channel.Port);
     var stream = client.GetStream();
     stream.ReadTimeout = 3000;
     stream.WriteTimeout = 3000;
     string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(channel.Username + ":" + (bad ? "wrong-password" : channel.Password)));
     byte[] header = Encoding.ASCII.GetBytes($"CONNECT {host}:{target} HTTP/1.1\r\nHost: {host}:{target}\r\nProxy-Authorization: Basic {auth}\r\n\r\n");
     stream.Write(header, 0, header.Length);
     var response = new StringBuilder();
     while (response.Length < 8192 && !response.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
     {
         int value = stream.ReadByte();
         if (value < 0) throw new IOException("CONNECT closed");
         response.Append((char)value);
     }
     if (!response.ToString().StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
         throw new IOException("CONNECT refused");
     return stream;
 }

 static string Request(LocalProxyEndpoint channel, int target, string marker, bool bad = false, string host = "127.0.0.1")
 {
     using (var client = new TcpClient())
     {
         var stream = OpenTunnel(client, channel, target, host, bad);
         byte[] payload = Encoding.ASCII.GetBytes(marker);
         stream.Write(payload, 0, payload.Length);
         return Encoding.ASCII.GetString(ReadExact(stream, payload.Length));
     }
 }

 static string QueryFixtureDns(LocalProxyEndpoint channel, int target, string domain, bool reject = false, bool fakeIp = true,
     bool txt = false, string expectedTxt = null, bool expectFailure = false)
 {
     var question = new List<byte>();
     foreach (string label in domain.Split('.'))
     {
         byte[] bytes = Encoding.ASCII.GetBytes(label);
         question.Add((byte)bytes.Length);
         question.AddRange(bytes);
     }
     question.AddRange(new byte[] { 0, 0, txt ? (byte)16 : (byte)1, 0, 1 });
     byte[] query = new byte[] { 0x46, 0x50, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }.Concat(question).ToArray();
     using (var client = new TcpClient())
     {
         var stream = OpenTunnel(client, channel, target, "127.0.0.1");
         byte[] frame = new byte[] { (byte)(query.Length >> 8), (byte)query.Length }.Concat(query).ToArray();
         stream.Write(frame, 0, frame.Length);
         byte[] length = ReadExact(stream, 2);
         byte[] reply = ReadExact(stream, (length[0] << 8) | length[1]);
         Check(reply.Length >= 12 + question.Count && reply[0] == 0x46 && reply[1] == 0x50 &&
             (reply[2] & 0x80) != 0 && reply[4] == 0 && reply[5] == 1 &&
             reply.Skip(12).Take(question.Count).SequenceEqual(question),
             domain + " DNS response for the exact query");
         if (reject)
         {
             Check((reply[3] & 15) == 5 && reply[6] == 0 && reply[7] == 0, domain + " refused without an answer");
             return null;
         }
         if (txt)
         {
             int rcode = reply[3] & 15;
             if (expectFailure)
             {
                 Check(rcode == 2 && reply[6] == 0 && reply[7] == 0, domain + " failed without an answer");
                 return null;
             }
             if (expectedTxt != null)
             {
                 int record = 12 + question.Count;
                 while (record < reply.Length && reply[record] > 0 && reply[record] < 64)
                     record += 1 + reply[record];
                 Check(record < reply.Length && (reply[record] == 0 || (reply[record] & 0xc0) == 0xc0),
                     domain + " has a valid answer name");
                 record += reply[record] == 0 ? 1 : 2;
                 Check(rcode == 0 && reply[6] == 0 && reply[7] == 1 &&
                     reply.Length >= record + 11 + expectedTxt.Length &&
                     reply.Skip(record).Take(4).SequenceEqual(new byte[] { 0, 16, 0, 1 }) &&
                     reply[record + 8] == 0 && reply[record + 9] == 1 + expectedTxt.Length &&
                     reply[record + 10] == expectedTxt.Length &&
                     Encoding.ASCII.GetString(reply, record + 11, expectedTxt.Length) == expectedTxt,
                     domain + " returned the selected resolver's TXT value");
             }
             else Check(rcode == 0 || rcode == 3, domain + " reached the fixture resolver");
             return "TXT";
         }
         Check((reply[3] & 15) == 0 && reply[6] == 0 && reply[7] == 1, domain + " has one successful answer");
         int answer = 12 + question.Count;
         if ((reply[answer] & 0xc0) == 0xc0)
             answer += 2;
         else
         {
             while (reply[answer] != 0) answer += reply[answer] + 1;
             answer++;
         }
         Check(reply.Length >= answer + 14 && reply[answer] == 0 && reply[answer + 1] == 1 &&
             reply[answer + 2] == 0 && reply[answer + 3] == 1 &&
             reply[answer + 8] == 0 && reply[answer + 9] == 4 &&
             (!fakeIp || reply[answer + 10] == 198 && reply[answer + 11] == 19),
             domain + " valid IPv4 answer");
         return new IPAddress(reply.Skip(answer + 10).Take(4).ToArray()).ToString();
     }
 }
 static bool Refused(Func<string> request){try{request();return false;}catch(IOException){return true;}catch(SocketException){return true;}}
 static VpnProfile Profile(string id,int port)=>new VpnProfile{Id=id,Name=id,Protocol="shadowsocks",Server="127.0.0.1",Port=port,Method="aes-128-gcm",Password="native-multi-fixture"};
 static void Run(string core,string root,bool sites,int dohPort=0,string caPath=null){
  ThreadPool.SetMinThreads(16,16);
  Directory.CreateDirectory(root);Process upstream=null,client=null;
  using(var echo=new Listener(Echo)){
   int upstreamPort=FreePort();string serverConfig=Path.Combine(root,"server.json");File.WriteAllText(serverConfig,Json.Serialize(Obj("log",Obj("level","error"),"inbounds",new[]{Obj("type","shadowsocks","listen","127.0.0.1","listen_port",upstreamPort,"method","aes-128-gcm","password","native-multi-fixture")},"outbounds",new[]{Obj("type","direct","tag","direct")},"route",Obj("final","direct"))));
   if (dohPort > 0)
   {
       string emptyHosts = Path.Combine(root, "empty-hosts");
       File.WriteAllText(emptyHosts, "");
       var server = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(serverConfig));
       server["dns"] = Obj("servers", new[] { Obj("type", "hosts", "tag", "fixture-empty", "path", new[] { emptyHosts }) }, "final", "fixture-empty");
       ((Dictionary<string, object>)server["route"])["default_domain_resolver"] = "fixture-empty";
       File.WriteAllText(serverConfig, Json.Serialize(server));
   }
   using(var frRelay=new Listener(c=>Relay(c,upstreamPort)))using(var usRelay=new Listener(c=>Relay(c,upstreamPort)))using(var probe=new ActiveCoreProbe()){
    try{
     var fr=Profile("fr",frRelay.Port);var us=Profile("us",usRelay.Port);var profiles=new[]{fr,us};var policy=new MultiServerSettings{DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord>{new MultiServerRuleRecord{Domain="fixture.invalid",Target="Profile",ProfileId="us"}}};
     if(sites){
      policy.Rules.Add(new MultiServerRuleRecord{Domain="site.example.test",Target="Profile",ProfileId="us"});
      policy.Rules.Add(new MultiServerRuleRecord{Domain="direct.example.test",Target="Direct"});
      policy.Rules.Add(new MultiServerRuleRecord{Domain="blocked.example.test",Target="Block"});
      policy.Rules.Add(new MultiServerRuleRecord{Domain="missing.example.test",Target="Profile",ProfileId="absent"});
     }
     if (dohPort > 0) policy.Rules.Add(new MultiServerRuleRecord { Domain = "fresh.example.test", Target = "Profile", ProfileId = "us" });
     var full=MultiServerConfigBuilder.Build(new RoutingSettings{MultiServer=policy,BypassLan=false},profiles,Path.Combine(root,"fixture-dns.db"));
     var config=Json.Deserialize<Dictionary<string,object>>(full);config["inbounds"]=new object[0]; // Explicitly remove every TUN before any native run.
     LocalProxyEndpoint siteChannel=null;
     if(sites){siteChannel=new LocalProxyEndpoint{Port=FreePort()};config["inbounds"]=new[]{Obj("type","http","tag","fixture-sites","listen","127.0.0.1","listen_port",siteChannel.Port,"users",new[]{Obj("username",siteChannel.Username,"password",siteChannel.Password)})};}
     var prepared=Json.Deserialize<Dictionary<string,object>>(probe.ApplyMulti(Json.Serialize(config),policy,profiles));
     if (dohPort > 0)
     {
         var dns = (Dictionary<string, object>)prepared["dns"];
         var paths = new Dictionary<string, string>
         {
             ["nova-exit-0"] = "/fr",
             ["nova-exit-1"] = "/us",
             ["nova-direct"] = "/direct"
         };
         foreach (var resolver in ((IEnumerable)dns["servers"]).Cast<Dictionary<string, object>>().Where(item => Equals(item["type"], "https")))
         {
             // Preserve resolver type, tags, and detours from the compiler.
             resolver["server"] = "127.0.0.1";
             resolver["server_port"] = dohPort;
             resolver["path"] = paths[(string)resolver["detour"]];
             resolver["tls"] = Obj("enabled", true, "server_name", "localhost", "certificate_path", caPath);
         }
         var bootstrap = ((IEnumerable)dns["servers"]).Cast<Dictionary<string, object>>()
             .Single(item => Equals(item["tag"], "nova-bootstrap-dns"));
         bootstrap["type"] = "hosts";
         bootstrap["path"] = new[] { Path.Combine(root, "empty-hosts") };
         ((Dictionary<string, object>)prepared["route"])["auto_detect_interface"] = false;
     }
     else
     if(sites){
      // Replace only DNS implementation in this fixture after production probe
      // binding validation. Preserve compiled resolver tags and route rules.
      // No system hosts file is read and no external DNS request is required.
      var emptyHosts=Path.Combine(root,"empty-hosts");File.WriteAllText(emptyHosts,"");
      var dns=(Dictionary<string,object>)prepared["dns"];
      dns["servers"]=((IEnumerable)dns["servers"]).Cast<object>().Cast<Dictionary<string,object>>().Select(server=>
       Equals(server["type"],"https")?Obj("type","hosts","tag",server["tag"],"path",new[]{emptyHosts},"predefined",Obj("site.example.test","127.0.0.1","other.example.test","127.0.0.1","direct.example.test","127.0.0.1","blocked.example.test","127.0.0.1","missing.example.test","127.0.0.1")):server).ToArray();
     }
     var inbounds=((IEnumerable)prepared["inbounds"]).Cast<object>().Cast<Dictionary<string,object>>().ToArray();Check(inbounds.Length==(sites?2:1)&&inbounds.All(i=>Equals(i["type"],"http")&&Equals(i["listen"],"127.0.0.1")&&i.ContainsKey("users")),"Only authenticated loopback HTTP inbound; no TUN");
     string clientConfig=Path.Combine(root,"client.json");File.WriteAllText(clientConfig,Json.Serialize(prepared));NativeCheck(core,serverConfig);NativeCheck(core,clientConfig);
     upstream=Start(core,serverConfig,Path.Combine(root,"server.log"));client=Start(core,clientConfig,Path.Combine(root,"client.log"));
     if (dohPort > 0)
     {
         void Trial(string host, bool expected, int frDelta, int usDelta)
         {
             int beforeFr = frRelay.Connections, beforeUs = usRelay.Connections, beforeEcho = echo.Connections;
             string marker = Guid.NewGuid().ToString("N");
             Func<string> request = () => Request(siteChannel, echo.Port, marker, host: host);
             Check(expected ? request() == marker : Refused(request), host + " exact outcome");
             int actualFr = frRelay.Connections - beforeFr, actualUs = usRelay.Connections - beforeUs;
             Check((frDelta == 0 ? actualFr == 0 : actualFr >= frDelta) &&
                 (usDelta == 0 ? actualUs == 0 : actualUs >= usDelta),
                 host + " DNS and echo exit counts " + actualFr + "/" + actualUs);
             Check(echo.Connections - beforeEcho == (expected ? 1 : 0), host + " exact destination count");
         }

         Trial("other.example.test", true, 2, 0);
         Trial("site.example.test", true, 0, 2);
         Trial("direct.example.test", true, 0, 0);
         Trial("blocked.example.test", false, 0, 0);
         Trial("missing.example.test", false, 0, 0);
         frRelay.Dispose();
         Trial("fr-failure.example.test", false, 0, 0);
         Trial("fresh.example.test", true, 0, 2);
         Check(!client.HasExited && !upstream.HasExited, "Native processes survived the DoH trials");
         return;
     }
     var channels=(Dictionary<string,LocalProxyEndpoint>)typeof(ActiveCoreProbe).GetField("multiChannels",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(probe);
     Check(Request(channels["fr"],echo.Port,"from-fr")=="from-fr","FR native encrypted path echoes payload");Check(frRelay.Connections==1&&usRelay.Connections==0,"FR credentials select only FR upstream");
     Check(Request(channels["us"],echo.Port,"from-us")=="from-us","US native encrypted path echoes payload");Check(frRelay.Connections==1&&usRelay.Connections==1,"US credentials select only US upstream");
     Check(Refused(()=>Request(channels["fr"],echo.Port,"bad-auth",true)),"Bad loopback auth refused");Check(frRelay.Connections==1&&usRelay.Connections==1,"Bad auth reaches neither exit");
     if(sites){
      Check(Request(siteChannel,echo.Port,"site-us",false,"site.example.test")=="site-us","Compiled site rule reaches US exit");
      Check(usRelay.Connections==2&&frRelay.Connections==1,"Site target has no FR fallback");
      Check(Request(siteChannel,echo.Port,"default-fr",false,"other.example.test")=="default-fr","Unmatched site reaches default FR exit");
      Check(frRelay.Connections==2&&usRelay.Connections==2,"Default chooses FR only");
      Check(Request(siteChannel,echo.Port,"explicit-direct",false,"direct.example.test")=="explicit-direct","Explicit direct site reaches echo");
      Check(frRelay.Connections==2&&usRelay.Connections==2,"Direct site uses neither encrypted exit");
      Check(Refused(()=>Request(siteChannel,echo.Port,"blocked",false,"blocked.example.test")),"Explicit blocked site refuses");
      Check(Refused(()=>Request(siteChannel,echo.Port,"missing",false,"missing.example.test")),"Unavailable pinned site refuses");
      Check(frRelay.Connections==2&&usRelay.Connections==2,"Blocked and missing site do not reach either exit");
     }
     frRelay.Dispose();int beforeUs=usRelay.Connections;
     Check(Refused(()=>Request(channels["fr"],echo.Port,"must-not-fallback")),"Stopped pinned FR exit cannot use direct or US");Check(usRelay.Connections==beforeUs,"Failed FR request never falls back to US");
     Check(Request(channels["us"],echo.Port,"us-survives")=="us-survives","US still works after FR failure");Check(usRelay.Connections==beforeUs+1,"US still uses its own exit");
     if(sites){
      Check(Refused(()=>Request(siteChannel,echo.Port,"default-must-fail",false,"other.example.test")),"Failed default site never changes exit");
      Check(Request(siteChannel,echo.Port,"site-us-survives",false,"site.example.test")=="site-us-survives","Explicit US site survives default exit failure");
      Check(usRelay.Connections==beforeUs+2,"Both surviving requests use only US");
     }
     using(var direct=new TcpClient()){direct.Connect(IPAddress.Loopback,echo.Port);var s=direct.GetStream();s.ReadTimeout=3000;s.WriteByte(77);Check(s.ReadByte()==77,"Direct echo control remains reachable while FR stays failed");}
     Check(!client.HasExited&&!upstream.HasExited,"Same native processes remain running throughout exit failure");
     File.WriteAllText(Path.Combine(root,"observations.json"),Json.Serialize(Obj("scope","Proxy-only native TCP CONNECT and encrypted exit isolation; no TLS health certification, app/site TUN or DNS packet proof","frConnections",frRelay.Connections,"usConnections",usRelay.Connections,"echoConnections",echo.Connections,"siteFixture",sites,"clientPid",client.Id,"serverPid",upstream.Id,"checks",checks)));
    }finally{try{Stop(client);}finally{Stop(upstream);}}
   }
  }
 }
 static int Main(string[] args)
 {
     try
     {
         string mode = args.Length >= 3 ? args[2] : "";
         if (args.Length > 0 && args[0] == "--app-client")
             return RunAppClient(args);
         if (args.Length > 0 && args[0] == "--udp-app-client")
             return RunUdpAppClient(args);
         string core = Path.GetFullPath(args[0]);
         string root = Path.GetFullPath(args[1]);
         if (mode == "--apps" || mode == "--apps-lan" || mode == "--apps-retired-default" || mode == "--apps-missing-default" || mode == "--apps-doh" || mode == "--apps-advanced" || mode == "--apps-names")
             RunApps(core, root, mode == "--apps-lan", mode == "--apps-retired-default" || mode == "--apps-missing-default",
                 mode == "--apps-doh" ? int.Parse(args[3]) : 0, mode == "--apps-doh" ? Path.GetFullPath(args[4]) : null,
                 mode == "--apps-advanced", mode == "--apps-names", mode == "--apps-missing-default");
         else if (mode == "--udp" || mode == "--udp6" || mode == "--udp6-disabled" || mode == "--udp6-mapped" || mode == "--udp-apps" || mode == "--udp-advanced")
             RunUdp(core, root, mode.StartsWith("--udp6", StringComparison.Ordinal),
                 mode == "--udp6-disabled", mode == "--udp6-mapped", mode == "--udp-apps", mode == "--udp-advanced");
         else
             Run(core, root, mode == "--sites" || mode == "--doh", mode == "--doh" ? int.Parse(args[3]) : 0, mode == "--doh" ? Path.GetFullPath(args[4]) : null);
         Console.WriteLine("PASS " + checks + " native multi-exit checks (" + mode + ")");
         return 0;
     }
     catch (Exception error)
     {
         Console.Error.WriteLine(error);
         return 1;
     }
 }
    // Each request is completed before the next starts. One backend socket is
    // sufficient for this fixture and lets us count encrypted ingress exactly.
    sealed class DatagramFixture : IDisposable
    {
        readonly UdpClient listener;
        readonly Task worker;
        volatile bool stopping;
        int packets;
        public int Port { get; }
        public int Packets => Volatile.Read(ref packets);

        public DatagramFixture(int? backendPort = null, IPAddress address = null)
        {
            address = address ?? IPAddress.Loopback;
            listener = new UdpClient(new IPEndPoint(address, 0));
            Port = ((IPEndPoint)listener.Client.LocalEndPoint).Port;
            listener.Client.ReceiveTimeout = 150;
            worker = Task.Factory.StartNew(() =>
            {
                using (var backend = backendPort.HasValue ? new UdpClient(AddressFamily.InterNetwork) : null)
                {
                    if (backend != null)
                    {
                        backend.Connect(IPAddress.Loopback, backendPort.Value);
                        backend.Client.ReceiveTimeout = 700;
                    }
                    while (!stopping)
                    {
                        var peer = new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                        byte[] payload;
                        try { payload = listener.Receive(ref peer); }
                        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { continue; }
                        Interlocked.Increment(ref packets);
                        if (backend != null)
                        {
                            backend.Send(payload, payload.Length);
                            var sender = new IPEndPoint(IPAddress.Any, 0);
                            try { payload = backend.Receive(ref sender); }
                            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { continue; }
                        }
                        listener.Send(payload, payload.Length, peer);
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            try
            {
                if (!worker.Wait(3000)) throw new TimeoutException("UDP fixture worker did not stop");
            }
            finally { listener.Close(); }
        }
    }

    static byte[] ReadExact(Stream stream, int length)
    {
        var bytes = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int count = stream.Read(bytes, offset, length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }

    static byte[] RequestUdp(int socksPort, string host, IPAddress targetAddress, int targetPort, byte[] payload,
        Action ready = null, bool alignAssociationPort = false)
    {
        using (var tcp = new TcpClient { ReceiveTimeout = 3000, SendTimeout = 3000 })
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            tcp.Connect(IPAddress.Loopback, socksPort);
            // SOCKS supplies the control source port to the router. Bind the test
            // datagram socket there too so UDP owner lookup sees the real app.
            int bindPort = alignAssociationPort ? ((IPEndPoint)tcp.Client.LocalEndPoint).Port : 0;
            udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, bindPort));
            var stream = tcp.GetStream();
            stream.Write(new byte[] { 5, 1, 0 }, 0, 3);
            if (!ReadExact(stream, 2).SequenceEqual(new byte[] { 5, 0 }))
                throw new IOException("SOCKS greeting refused");

            int localPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            var associate = new byte[] { 5, 3, 0, 1, 127, 0, 0, 1, (byte)(localPort >> 8), (byte)localPort };
            stream.Write(associate, 0, associate.Length);
            if (!ReadExact(stream, 4).SequenceEqual(new byte[] { 5, 0, 0, 1 }))
                throw new IOException("Expected IPv4 SOCKS UDP relay");
            var address = new IPAddress(ReadExact(stream, 4));
            if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
            if (!IPAddress.IsLoopback(address)) throw new IOException("SOCKS relay outside loopback");
            var port = ReadExact(stream, 2);
            udp.Connect(address, (port[0] << 8) | port[1]);
            udp.Client.ReceiveTimeout = 1500;

            byte[] destination;
            if (IPAddress.TryParse(host, out IPAddress literal))
                destination = new byte[] { (byte)(literal.AddressFamily == AddressFamily.InterNetworkV6 ? 4 : 1) }
                    .Concat(literal.GetAddressBytes()).ToArray();
            else
            {
                byte[] domain = Encoding.ASCII.GetBytes(host);
                destination = new byte[] { 3, (byte)domain.Length }.Concat(domain).ToArray();
            }
            byte[] request = new byte[] { 0, 0, 0 }.Concat(destination)
                .Concat(new byte[] { (byte)(targetPort >> 8), (byte)targetPort }).Concat(payload).ToArray();
            ready?.Invoke();
            udp.Send(request, request.Length);
            var peer = new IPEndPoint(IPAddress.Any, 0);
            byte[] response;
            try { response = udp.Receive(ref peer); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { return null; }

            if (response.Length < 7 || response.Take(3).Any(value => value != 0))
                throw new IOException("Invalid SOCKS UDP response");
            int offset;
            if (response[3] == 1 && response.Skip(4).Take(4).SequenceEqual(targetAddress.GetAddressBytes()))
                offset = 8;
            else if (response[3] == 4 && response.Skip(4).Take(16).SequenceEqual(targetAddress.GetAddressBytes()))
                offset = 20;
            else if (response[3] == 3 && response.Length >= 7 + response[4] &&
                Encoding.ASCII.GetString(response, 5, response[4]) == host)
                offset = 5 + response[4];
            else throw new IOException("Unexpected UDP response address");
            if (response.Length < offset + 2 || ((response[offset] << 8) | response[offset + 1]) != targetPort)
                throw new IOException("Unexpected UDP response port");
            return response.Skip(offset + 2).ToArray();
        }
    }

    static int RunUdpAppClient(string[] args)
    {
        var marker = Encoding.ASCII.GetBytes(args[4]);
        Action ready = null;
        if (args.Length == 6)
        {
            ready = () =>
            {
                File.WriteAllText(Path.Combine(args[5], args[4] + ".ready"), "");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(args[5], "release")))
                {
                    if (deadline.ElapsedMilliseconds > 6000) throw new TimeoutException("UDP fixture barrier");
                    Thread.Sleep(20);
                }
            };
        }
        var response = RequestUdp(int.Parse(args[1]), args[2], IPAddress.Loopback, int.Parse(args[3]), marker, ready, alignAssociationPort: true);
        if (response == null) return 3;
        if (!response.SequenceEqual(marker)) throw new IOException("Wrong UDP application payload");
        return 0;
    }

    static void RunUdpApplications(string root, IReadOnlyDictionary<string, string> apps, int socksPort,
        DatagramFixture target, DatagramFixture fr, DatagramFixture us)
    {
        Process StartProbe(string app, string host, string marker, bool barrier = false)
        {
            var args = $"--udp-app-client {socksPort} {host} {target.Port} {marker}";
            if (barrier) args += " \"" + root + "\"";
            return Process.Start(new ProcessStartInfo(apps[app], args) { UseShellExecute = false, CreateNoWindow = true });
        }
        void CheckProbe(Process process, int expected, string label)
        {
            Check(process.WaitForExit(9000) && process.ExitCode == expected, label + " exact UDP outcome");
        }
        void Trial(string app, string host, string exit)
        {
            int oldFr = fr.Packets, oldUs = us.Packets, oldTarget = target.Packets;
            var process = StartProbe(app, host, Guid.NewGuid().ToString("N"));
            try { CheckProbe(process, exit == null ? 3 : 0, app + " / " + host); }
            finally { Stop(process); }
            Check(fr.Packets - oldFr == (exit == "fr" ? 1 : 0) && us.Packets - oldUs == (exit == "us" ? 1 : 0),
                app + " / " + host + " exact encrypted exit");
            Check(target.Packets - oldTarget == (exit == null ? 0 : 1), app + " / " + host + " exact delivery");
        }

        Process frApp = null, usApp = null;
        try
        {
            frApp = StartProbe("fr", "127.0.0.1", "concurrent-fr", true);
            usApp = StartProbe("us", "127.0.0.1", "concurrent-us", true);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(root, "concurrent-fr.ready")) || !File.Exists(Path.Combine(root, "concurrent-us.ready")))
            {
                if (frApp.HasExited || usApp.HasExited || deadline.ElapsedMilliseconds > 5000)
                    throw new TimeoutException("Both application UDP associations must be live together");
                Thread.Sleep(20);
            }
            File.WriteAllText(Path.Combine(root, "release"), "");
            CheckProbe(frApp, 0, "concurrent FR application");
            CheckProbe(usApp, 0, "concurrent US game");
            Check(fr.Packets == 1 && us.Packets == 1 && target.Packets == 2, "Concurrent apps used distinct exits to the same endpoint");
        }
        finally
        {
            try { Stop(frApp); }
            finally { Stop(usApp); }
        }

        Trial("fr", "site.example.test", "us");
        Trial("us", "site-fr.example.test", "fr");
        Trial("blocked", "site.example.test", null);
        Trial("missing", "127.0.0.1", null);
        Trial("default", "127.0.0.1", "fr");
        fr.Dispose();
        Trial("fr", "127.0.0.1", null);
        Trial("us", "127.0.0.1", "us");
    }

    static void RunUdp(string core, string root, bool ipv6, bool ipv6Disabled, bool ipv6Mapped, bool applicationMode, bool advancedBlock)
    {
        string serverPath = Path.Combine(root, "server.json");
        string clientPath = Path.Combine(root, "client.json");
        string hostsPath = Path.Combine(root, "empty-hosts");
        File.WriteAllText(hostsPath, "");
        int backendPort = FreePort();
        int socksPort = FreePort();
        var targetAddress = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using (var target = new DatagramFixture(address: ipv6Mapped ? IPAddress.Loopback : targetAddress))
        using (var fr = new DatagramFixture(backendPort))
        using (var us = new DatagramFixture(backendPort))
        {
            var serverOutbound = Obj("type", "direct", "tag", "direct");
            var serverRoute = Obj("final", "direct");
            if (ipv6Mapped)
            {
                // Match the original IPv6 destination at the encrypted peer. Only the
                // fixture's final socket is remapped because this host cannot send on ::1.
                serverRoute["rules"] = new[] {
                    Obj("ip_cidr", new[] { "::1/128" }, "port", new[] { target.Port }, "network", "udp",
                        "action", "route", "outbound", "direct", "override_address", "127.0.0.1"),
                    Obj("action", "reject")
                };
            }
            File.WriteAllText(serverPath, Json.Serialize(Obj("log", Obj("level", "debug"),
                "inbounds", new[] { Obj("type", "shadowsocks", "listen", "127.0.0.1", "listen_port", backendPort,
                    "method", "aes-128-gcm", "password", "native-multi-fixture") },
                "outbounds", new[] { serverOutbound }, "route", serverRoute)));

            if (ipv6 && !ipv6Disabled && !ipv6Mapped)
            {
                using (var control = new UdpClient(AddressFamily.InterNetworkV6))
                {
                    control.Connect(targetAddress, target.Port);
                    control.Client.ReceiveTimeout = 1500;
                    byte[] probe = { 1, 2, 3 };
                    control.Send(probe, probe.Length);
                    var peer = new IPEndPoint(IPAddress.IPv6Any, 0);
                    try { Check(control.Receive(ref peer).SequenceEqual(probe), "Local IPv6 UDP control"); }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
                    {
                        throw new IOException("IPv6 UDP loopback control timed out before native startup", error);
                    }
                }
            }
            var policy = new MultiServerSettings { DefaultProfileId = "fr" };
            policy.Rules.Add(new MultiServerRuleRecord { Domain = "site.example.test", Target = "Profile", ProfileId = "us" });
            policy.Rules.Add(new MultiServerRuleRecord { Domain = "direct.example.test", Target = "Direct" });
            policy.Rules.Add(new MultiServerRuleRecord { Domain = "blocked.example.test", Target = "Block" });
            policy.Rules.Add(new MultiServerRuleRecord { Domain = "missing.example.test", Target = "Profile", ProfileId = "absent" });

            var routing = new RoutingSettings { MultiServer = policy, BypassLan = false, EnableIpv6 = ipv6 && !ipv6Disabled };
            if (advancedBlock) routing.AdvancedRules.Add("block|udp|" + target.Port);
            var frProfile = Profile("fr", fr.Port);
            var usProfile = Profile("us", us.Port);
            var apps = new Dictionary<string, string>();
            if (applicationMode)
            {
                string executable = Process.GetCurrentProcess().MainModule.FileName;
                foreach (string id in new[] { "fr", "us", "blocked", "missing", "default" })
                {
                    string path = Path.Combine(root, "udp-app-" + id + ".exe");
                    File.Copy(executable, path);
                    apps.Add(id, path);
                }
                policy.Rules.Add(new MultiServerRuleRecord { Domain = "site-fr.example.test", Target = "Profile", ProfileId = "fr" });
                policy.Rules.Add(new MultiServerRuleRecord { ApplicationPath = apps["fr"].ToUpperInvariant(), Target = "Profile", ProfileId = "fr" });
                policy.Rules.Add(new MultiServerRuleRecord { ApplicationPath = apps["blocked"].ToUpperInvariant(), Target = "Block" });
                policy.Rules.Add(new MultiServerRuleRecord { ApplicationPath = apps["missing"].ToUpperInvariant(), Target = "Profile", ProfileId = "absent" });
                routing = GamingRouting.Effective(routing, new GamingRoute
                {
                    GameId = "udp-fixture", Game = "UDP fixture", ExecutablePaths = new[] { apps["us"].ToUpperInvariant() }, Profile = usProfile
                }, false);
            }

            var rootConfig = Json.Deserialize<Dictionary<string, object>>(MultiServerConfigBuilder.Build(
                routing, new[] { frProfile, usProfile }, Path.Combine(root, "fixture-dns.db")));
            rootConfig["inbounds"] = new[] { Obj("type", "socks", "tag", "fixture-udp", "listen", "127.0.0.1", "listen_port", socksPort) };
            // This fixture exercises the Windows loopback adapter only.
            ((Dictionary<string, object>)rootConfig["route"])["auto_detect_interface"] = false;
            var dns = (Dictionary<string, object>)rootConfig["dns"];
            string address = targetAddress.ToString();
            var hosts = Obj("site.example.test", address, "site-fr.example.test", address, "other.example.test", address,
                "direct.example.test", address, "blocked.example.test", address, "missing.example.test", address);
            dns["servers"] = ((IEnumerable)dns["servers"]).Cast<Dictionary<string, object>>()
                .Select(server => Equals(server["type"], "https")
                    ? Obj("type", "hosts", "tag", server["tag"], "path", new[] { hostsPath }, "predefined", hosts)
                    : server).ToArray();
            if (ipv6Disabled || ipv6Mapped || applicationMode || advancedBlock) rootConfig["log"] = Obj("level", "debug");
            File.WriteAllText(clientPath, Json.Serialize(rootConfig));

            Process server = null, client = null;
            try
            {
                NativeCheck(core, serverPath);
                NativeCheck(core, clientPath);
                server = Start(core, serverPath, Path.Combine(root, "server.log"));
                client = Start(core, clientPath, Path.Combine(root, "client.log"));
                void Trial(string host, int frDelta, int usDelta, bool arrives)
                {
                    int beforeFr = fr.Packets, beforeUs = us.Packets, beforeTarget = target.Packets;
                    var payload = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
                    byte[] response = RequestUdp(socksPort, host, targetAddress, target.Port, payload);
                    Check(arrives ? response != null && response.SequenceEqual(payload) : response == null, host + " exact UDP outcome, relay delta " + (fr.Packets - beforeFr) + "/" + (us.Packets - beforeUs) + ", echo delta " + (target.Packets - beforeTarget));
                    Check(fr.Packets - beforeFr == frDelta && us.Packets - beforeUs == usDelta, host + " exact encrypted exit counts");
                    Check(target.Packets - beforeTarget == (arrives ? 1 : 0), host + " exact destination count");
                }

                if (applicationMode)
                    RunUdpApplications(root, apps, socksPort, target, fr, us);
                else if (advancedBlock)
                    Trial("site.example.test", 0, 0, false);
                else if (ipv6Disabled)
                    Trial(address, 0, 0, false);
                else
                {
                    if (ipv6) Trial(address, 1, 0, true);
                    Trial("site.example.test", 0, 1, true);
                    Trial("other.example.test", 1, 0, true);
                    if (!ipv6Mapped) Trial("direct.example.test", 0, 0, true);
                    Trial("blocked.example.test", 0, 0, false);
                    Trial("missing.example.test", 0, 0, false);
                    fr.Dispose();
                    Trial("other.example.test", 0, 0, false);
                    Trial("site.example.test", 0, 1, true);
                    if (!ipv6Mapped) Trial("direct.example.test", 0, 0, true);
                }
                Check(!client.HasExited && !server.HasExited, "Native processes survived the UDP trials");
                int clientPid = client.Id;
                if (ipv6Disabled || advancedBlock)
                {
                    var stoppedClient = client;
                    client = null;
                    Stop(stoppedClient);
                    string reject = advancedBlock ? "network=udp port=" + target.Port + " => reject" : "ip_version=6 => reject";
                    Check(File.ReadLines(Path.Combine(root, "client.log")).Any(line => line.Contains(reject)),
                        "Native " + (advancedBlock ? "advanced port" : "IPv6") + " reject rule handled the datagram");
                }
                if (ipv6Mapped)
                {
                    var stoppedServer = server;
                    server = null;
                    int serverPid = stoppedServer.Id;
                    Stop(stoppedServer);
                    var received = File.ReadLines(Path.Combine(root, "server.log"))
                        .Count(line => line.Contains("inbound packet connection to [::1]:" + target.Port));
                    Check(received == 2 && target.Packets == 4, "Both encrypted peer sessions received the original IPv6 UDP destination");
                    File.WriteAllText(Path.Combine(root, "observations.json"), Json.Serialize(Obj("checks", checks,
                        "ipv6SessionsAtPeer", received, "frRelayPackets", fr.Packets, "usRelayPackets", us.Packets, "ipv4EchoPackets", target.Packets, "clientPid", clientPid, "serverPid", serverPid)));
                    return;
                }
                File.WriteAllText(Path.Combine(root, "observations.json"), Json.Serialize(Obj(
                    "checks", checks, "frPackets", fr.Packets, "usPackets", us.Packets, "echoPackets", target.Packets,
                    "clientPid", clientPid, "serverPid", server.Id)));
            }
            finally
            {
                try { Stop(client); }
                finally { Stop(server); }
            }
        }
    }

    static int RunAppClient(string[] args)
    {
        var channel = new LocalProxyEndpoint { Port = int.Parse(args[1]), Username = args[2], Password = args[3] };
        try
        {
            if (args[6].StartsWith("dns-txt", StringComparison.Ordinal))
            {
                string expected = args[6] == "dns-txt-fr" ? "fr" : args[6] == "dns-txt-us" ? "us" : null;
                bool expectFailure = args[6] == "dns-txt-failure";
                QueryFixtureDns(channel, int.Parse(args[4]), args[5], reject: args[6] == "dns-txt-reject",
                    txt: true, expectedTxt: expected, expectFailure: expectFailure);
                return expectFailure ? 3 : 0;
            }
            string response = Request(channel, int.Parse(args[4]), args[6], host: args[5]);
            if (response != args[6]) throw new IOException("Unexpected application fixture response");
            Console.WriteLine(response);
            return 0;
        }
        catch (IOException) { return 3; }
        catch (SocketException) { return 3; }
    }

    static void RunApps(string core, string root, bool bypassLan, bool unavailableDefault, int dohPort, string caPath, bool advancedMode, bool nameMode, bool missingDefault)
    {
        ThreadPool.SetMinThreads(16, 16);
        string executable = Process.GetCurrentProcess().MainModule.FileName;
        var apps = new Dictionary<string, string>();
        foreach (string id in new[] { "fr", "us", "us-peer", "default", "blocked", "missing" })
        {
            string path = Path.Combine(root, "fixture-app-" + id + ".exe");
            File.Copy(executable, path);
            apps.Add(id, path);
        }

        using (var echo = new Listener(Echo))
        {
            int upstreamPort = FreePort();
            string serverPath = Path.Combine(root, "server.json");
            File.WriteAllText(serverPath, Json.Serialize(Obj("log", Obj("level", "error"),
                "inbounds", new[] { Obj("type", "shadowsocks", "listen", "127.0.0.1", "listen_port", upstreamPort,
                    "method", "aes-128-gcm", "password", "native-multi-fixture") },
                "outbounds", new[] { Obj("type", "direct", "tag", "direct") },
                "route", Obj("rules", new[] {
                    Obj("ip_cidr", new[] { "127.0.0.1/32" }, "port", new[] { dohPort == 0 ? echo.Port : dohPort }, "action", "route", "outbound", "direct"),
                    Obj("action", "reject")
                }, "final", "direct"))));

            using (var fr = new Listener(client => Relay(client, upstreamPort)))
            using (var us = new Listener(client => Relay(client, upstreamPort)))
            {
                var policy = new MultiServerSettings { DefaultProfileId = "fr" };
                foreach (string id in new[] { "fr", "us", "us-peer" })
                    policy.Rules.Add(new MultiServerRuleRecord { ApplicationPath = apps[id].ToUpperInvariant(), Target = "Profile", ProfileId = id == "us-peer" ? "us" : id });
                policy.Rules.Add(new MultiServerRuleRecord { ApplicationPath = apps["blocked"].ToUpperInvariant(), Target = "Block" });
                policy.Rules.Add(new MultiServerRuleRecord { ApplicationPath = apps["missing"].ToUpperInvariant(), Target = "Profile", ProfileId = "absent" });
                policy.Rules.Add(new MultiServerRuleRecord { Domain = "site-us.example.test", Target = "Profile", ProfileId = "us" });
                policy.Rules.Add(new MultiServerRuleRecord { Domain = "site-fr.example.test", Target = "Profile", ProfileId = "fr" });
                policy.Rules.Add(new MultiServerRuleRecord { Domain = "site-block.example.test", Target = "Block" });
                policy.Rules.Add(new MultiServerRuleRecord { Domain = "blocked.local", Target = "Block", IncludeSubdomains = true });
                policy.Rules.Add(new MultiServerRuleRecord { Domain = "pinned.local", Target = "Profile", ProfileId = "us", IncludeSubdomains = true });
                policy.Rules.Add(new MultiServerRuleRecord { Domain = "direct.pinned.local", Target = "Direct" });
                if (bypassLan)
                {
                    policy.Rules.Add(new MultiServerRuleRecord { Domain = "localhost", Target = "Direct" });
                    policy.Rules.Add(new MultiServerRuleRecord { Domain = ".legacy.local", Target = "Direct" });
                    policy.Rules.Add(new MultiServerRuleRecord { Domain = "pinned.youtube.com", Target = "Profile", ProfileId = "fr" });
                    policy.Rules.Add(new MultiServerRuleRecord { Domain = "blocked.youtube.com", Target = "Block" });
                }

                var defaultProfile = Profile("fr", fr.Port);
                defaultProfile.RetiredFromSubscription = unavailableDefault;
                var usProfile = Profile("us", us.Port);
                var catalog = missingDefault ? new[] { usProfile } : new[] { defaultProfile, usProfile };
                string frExit = unavailableDefault ? null : "fr";
                string usTag = unavailableDefault ? "nova-exit-0" : "nova-exit-1";
                var routing = new RoutingSettings { MultiServer = policy, BypassLan = bypassLan, BypassPrivateDns = true };
                if (advancedMode) routing.AdvancedRules.Add("direct|tcp|" + echo.Port);
                if (nameMode)
                {
                    routing.DirectProcesses.AddRange(new[] { "fixture-app-default.exe", "fixture-app-us.exe", "fixture-app-blocked.exe" });
                    routing.ProxyProcesses.Add("fixture-app-default.exe");
                }
                if (bypassLan)
                    ZapretRoutingTransitionService.SetActive(new AppState { Routing = routing }, true);
                var config = Json.Deserialize<Dictionary<string, object>>(MultiServerConfigBuilder.Build(
                    routing, catalog, Path.Combine(root, "fixture-dns.db")));
                if (unavailableDefault)
                {
                    var exits = ((IEnumerable)config["outbounds"]).Cast<Dictionary<string, object>>().Select(outbound => (string)outbound["tag"]);
                    Check(exits.SequenceEqual(new[] { "nova-direct", usTag }), "Unavailable default has no outbound");
                }
                var channel = new LocalProxyEndpoint { Port = FreePort() };
                var dnsChannel = new LocalProxyEndpoint { Port = FreePort(), Username = channel.Username, Password = channel.Password };
                config["inbounds"] = new[] {
                    Obj("type", "http", "tag", "fixture-apps", "listen", "127.0.0.1", "listen_port", channel.Port,
                        "users", new[] { Obj("username", channel.Username, "password", channel.Password) }),
                    Obj("type", "http", "tag", "fixture-apps-dns", "listen", "127.0.0.1", "listen_port", dnsChannel.Port,
                        "users", new[] { Obj("username", dnsChannel.Username, "password", dnsChannel.Password) })
                };
                ((Dictionary<string, object>)config["route"])["auto_detect_interface"] = false;
                config["log"] = Obj("level", "debug");
                string hostsFile = Path.Combine(root, "empty-hosts");
                File.WriteAllText(hostsFile, "");
                var hosts = Obj("same.example.test", "127.0.0.1", "site-us.example.test", "127.0.0.1",
                    "site-fr.example.test", "127.0.0.1", "site-block.example.test", "127.0.0.1",
                    "child.blocked.local", "127.0.0.1", "printer.local", "127.0.0.1", "child.pinned.local", "127.0.0.1",
                    "direct.pinned.local", "127.0.0.1", "localhost", "127.0.0.1", "sub.localhost", "127.0.0.1",
                    "child.legacy.local", "127.0.0.1", "legacy.local", "127.0.0.1",
                    "assets.youtube.com", "127.0.0.1", "pinned.youtube.com", "127.0.0.1", "blocked.youtube.com", "127.0.0.1");
                var dns = (Dictionary<string, object>)config["dns"];
                dns["servers"] = ((IEnumerable)dns["servers"]).Cast<Dictionary<string, object>>().Select(resolver =>
                {
                    if (dohPort > 0 && Equals(resolver["type"], "https"))
                    {
                        resolver["server"] = "127.0.0.1";
                        resolver["server_port"] = dohPort;
                        resolver["path"] = Equals(resolver["detour"], usTag) ? "/us" :
                            Equals(resolver["detour"], "nova-exit-0") ? "/fr" : "/direct";
                        resolver["tls"] = Obj("enabled", true, "server_name", "localhost", "certificate_path", caPath);
                        return resolver;
                    }
                    return Equals(resolver["type"], "https") || Equals(resolver["type"], "local")
                        ? Obj("type", "hosts", "tag", resolver["tag"], "path", new[] { hostsFile }, "predefined", hosts)
                        : resolver;
                }).ToArray();
                string clientPath = Path.Combine(root, "client.json");
                File.WriteAllText(clientPath, Json.Serialize(config));
                NativeCheck(core, serverPath);
                NativeCheck(core, clientPath);
                Process upstream = null, client = null;
                var observations = new List<Dictionary<string, object>>();
                var dnsObservations = new[] {
                    new { App = "us", Domain = "app-txt.example.test", Decision = "route(dns-" + usTag + ")" },
                    new { App = "us", Domain = "site-fr.example.test", Decision = unavailableDefault ? "reject" : "route(dns-nova-exit-0)" },
                    new { App = "blocked", Domain = "site-us.example.test", Decision = "reject" },
                    new { App = "missing", Domain = "missing-txt.example.test", Decision = "reject" }
                }.ToList();
                if (nameMode)
                    dnsObservations.Add(new { App = "default", Domain = "name-txt.example.test", Decision = "route(dns-nova-direct)" });
                try
                {
                    upstream = Start(core, serverPath, Path.Combine(root, "server.log"));
                    client = Start(core, clientPath, Path.Combine(root, "client.log"));
                    int dnsDestination = FreePort();
                    void TrialTxt(string app, string domain, string marker, int expectedExitCode = 0)
                    {
                        var start = new ProcessStartInfo(apps[app],
                            $"--app-client {dnsChannel.Port} {dnsChannel.Username} {dnsChannel.Password} {dnsDestination} {domain} {marker}")
                        {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                        };
                        using (var child = Process.Start(start))
                        {
                            var output = child.StandardOutput.ReadToEndAsync();
                            if (!child.WaitForExit(8000))
                            {
                                child.Kill();
                                child.WaitForExit();
                                throw new TimeoutException("Application DNS fixture did not exit");
                            }
                            Check(child.ExitCode == expectedExitCode, app + " / " + domain + " TXT response " + output.GetAwaiter().GetResult().Trim());
                        }
                    }

                    if (dohPort > 0)
                    {
                        void TrialDoh(string app, string domain, string outcome)
                        {
                            int beforeFr = fr.Connections, beforeUs = us.Connections;
                            TrialTxt(app, domain, "dns-txt-" + outcome, outcome == "failure" ? 3 : 0);
                            int frDelta = fr.Connections - beforeFr, usDelta = us.Connections - beforeUs;
                            Check((outcome == "fr" ? frDelta >= 1 : frDelta == 0) &&
                                (outcome == "us" ? usDelta >= 1 : usDelta == 0), domain + " exact encrypted exit");
                        }

                        TrialDoh("us", "shared-txt.example.test", "us");
                        TrialDoh("default", "shared-txt.example.test", "fr");
                        TrialDoh("us", "site-fr.example.test", "fr");
                        TrialDoh("blocked", "site-us.example.test", "reject");
                        TrialDoh("missing", "missing-txt.example.test", "reject");
                        fr.Dispose();
                        TrialDoh("fr", "failed-txt.example.test", "failure");
                        TrialDoh("us", "fresh-txt.example.test", "us");
                        return;
                    }

                    void Trial(string app, string host, string exit, bool available = true, string recoveredDomain = null)
                    {
                        int beforeFr = fr.Connections, beforeUs = us.Connections, beforeEcho = echo.Connections;
                        string marker = Guid.NewGuid().ToString("N");
                        var start = new ProcessStartInfo(apps[app],
                            $"--app-client {channel.Port} {channel.Username} {channel.Password} {echo.Port} {host} {marker}")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        bool shouldArrive = exit != null && available;
                        using (var child = Process.Start(start))
                        {
                            var output = child.StandardOutput.ReadToEndAsync();
                            var errors = child.StandardError.ReadToEndAsync();
                            if (!child.WaitForExit(8000))
                            {
                                child.Kill();
                                child.WaitForExit();
                                throw new TimeoutException("Application fixture did not exit");
                            }
                            string response = output.GetAwaiter().GetResult().Trim();
                            Check(shouldArrive ? child.ExitCode == 0 && response == marker : child.ExitCode == 3 && response.Length == 0,
                                app + " / " + host + " exact outcome " + errors.GetAwaiter().GetResult());
                        }
                        Check(fr.Connections - beforeFr == (shouldArrive && exit == "fr" ? 1 : 0) &&
                            us.Connections - beforeUs == (shouldArrive && exit == "us" ? 1 : 0), app + " exact egress");
                        Check(echo.Connections - beforeEcho == (shouldArrive ? 1 : 0), app + " exact delivery");
                        observations.Add(Obj("app", app, "path", apps[app], "host", host, "expectedExit", exit, "delivered", shouldArrive, "recoveredDomain", recoveredDomain));
                    }

                    if (bypassLan)
                    {
                        Trial("blocked", "127.0.0.1", null);
                        Trial("missing", "127.0.0.1", null);
                        Trial("fr", "127.0.0.1", "fr");
                        Trial("us", "127.0.0.1", "us");
                        Trial("default", "127.0.0.1", "direct");
                    }
                    Trial("fr", "same.example.test", frExit);
                    Trial("us", "same.example.test", "us");
                    Trial("us-peer", "same.example.test", "us");
                    Trial("default", "same.example.test", advancedMode || nameMode ? "direct" : frExit);
                    Trial("fr", "site-us.example.test", "us");
                    Trial("us", "site-fr.example.test", frExit);
                    Trial("blocked", "site-us.example.test", null);
                    Trial("missing", "same.example.test", null);
                    Trial("us", "site-block.example.test", null);
                    foreach (var dnsTrial in dnsObservations)
                        TrialTxt(dnsTrial.App, dnsTrial.Domain, dnsTrial.Decision == "reject" ? "dns-txt-reject" : "dns-txt");
                    if (bypassLan)
                    {
                        string localhostIp = QueryFixtureDns(dnsChannel, dnsDestination, "localhost");
                        Trial("us", localhostIp, "direct", recoveredDomain: "localhost");
                        Trial("us", "sub.localhost", "us");
                        string wildcardIp = QueryFixtureDns(dnsChannel, dnsDestination, "child.legacy.local");
                        Trial("us", wildcardIp, "direct", recoveredDomain: "child.legacy.local");
                        Trial("us", "legacy.local", "us");
                        string zapretIp = QueryFixtureDns(dnsChannel, dnsDestination, "assets.youtube.com");
                        Trial("us", zapretIp, "direct", recoveredDomain: "assets.youtube.com");
                        Trial("us", "pinned.youtube.com", "fr");
                        Trial("us", "blocked.youtube.com", null);
                        Trial("blocked", "assets.youtube.com", null);
                    }
                    QueryFixtureDns(dnsChannel, dnsDestination, "child.blocked.local", reject: true);
                    QueryFixtureDns(dnsChannel, dnsDestination, "site-block.example.test", reject: true);
                    Check(QueryFixtureDns(dnsChannel, dnsDestination, "printer.local", fakeIp: false) == "127.0.0.1",
                        "Unassigned local name still uses the local resolver");
                    string pinnedIp = QueryFixtureDns(dnsChannel, dnsDestination, "child.pinned.local");
                    Trial("fr", pinnedIp, "us", recoveredDomain: "child.pinned.local");
                    string directIp = QueryFixtureDns(dnsChannel, dnsDestination, "direct.pinned.local");
                    Trial("fr", directIp, "direct", recoveredDomain: "direct.pinned.local");
                    string sharedIp = QueryFixtureDns(dnsChannel, dnsDestination, "same.example.test");
                    string siteIp = QueryFixtureDns(dnsChannel, dnsDestination, "site-us.example.test");
                    Check(sharedIp != siteIp, "Different names have distinct FakeIP addresses");
                    Trial("fr", sharedIp, frExit, recoveredDomain: "same.example.test");
                    Trial("us", sharedIp, "us", recoveredDomain: "same.example.test");
                    Trial("fr", siteIp, "us", recoveredDomain: "site-us.example.test");
                    Trial("blocked", siteIp, null, recoveredDomain: "site-us.example.test");
                    Trial("missing", sharedIp, null, recoveredDomain: "same.example.test");
                    if (unavailableDefault)
                    {
                        Trial("default", sharedIp, null, recoveredDomain: "same.example.test");
                        Trial("default", siteIp, "us", recoveredDomain: "site-us.example.test");
                    }
                    else
                    {
                        fr.Dispose();
                        Trial("fr", "same.example.test", "fr", false);
                    }
                    Trial("us", "same.example.test", "us");
                    Check(!client.HasExited && !upstream.HasExited, "Native processes survived app trials");
                }
                finally
                {
                    try { Stop(client); }
                    finally { Stop(upstream); }
                }
                string trace = System.Text.RegularExpressions.Regex.Replace(
                    File.ReadAllText(Path.Combine(root, "client.log")), @"\x1b\[[0-9;]*m", "");
                var ingress = System.Text.RegularExpressions.Regex.Matches(trace,
                    @"\[(\d+) [^\]]+\][^\r\n]*inbound/http\[fixture-apps\]:[^\r\n]*inbound connection to ([^:\s]+):\d+");
                Check(ingress.Count == observations.Count, "One native inbound connection per application trial");
                string[] lines = trace.Split('\n');
                foreach (var dnsTrial in dnsObservations)
                {
                    var exchange = System.Text.RegularExpressions.Regex.Match(trace,
                        @"\[(\d+) [^\]]+\][^\r\n]*dns: exchange " +
                        System.Text.RegularExpressions.Regex.Escape(dnsTrial.Domain) + @"\. IN TXT");
                    string connection = "[" + exchange.Groups[1].Value + " ";
                    var events = lines.Where(line => line.Contains(connection)).ToArray();
                    Check(exchange.Success && events.Any(line =>
                        line.TrimEnd().EndsWith("found process path: " + apps[dnsTrial.App], StringComparison.OrdinalIgnoreCase)),
                        dnsTrial.App + " TXT native owner");
                    Check(events.Any(line => line.Contains("dns: match[") && line.TrimEnd().EndsWith("=> " + dnsTrial.Decision, StringComparison.Ordinal)),
                        dnsTrial.Domain + " TXT resolver decision " + dnsTrial.Decision);
                }
                for (int index = 0; index < observations.Count; index++)
                {
                    var observation = observations[index];
                    string connection = "[" + ingress[index].Groups[1].Value + " ";
                    var events = lines.Where(line => line.Contains(connection)).ToArray();
                    string expectedPath = (string)observation["path"];
                    string expectedExit = (string)observation["expectedExit"];
                    string expectedTag = expectedExit == null ? null : expectedExit == "fr" ? "nova-exit-0" : expectedExit == "us" ? usTag : "nova-direct";
                    Check(ingress[index].Groups[2].Value == (string)observation["host"] &&
                        events.Any(line => line.TrimEnd().EndsWith("found process path: " + expectedPath, StringComparison.OrdinalIgnoreCase)),
                        (string)observation["app"] + " native owner and destination");
                    var outbound = events.Where(line => line.Contains("outbound connection to")).ToArray();
                    Check(expectedTag == null ? outbound.Length == 0 && events.Any(line => line.Contains("=> reject")) :
                        outbound.Length == 1 && outbound[0].Contains("[" + expectedTag + "]"),
                        (string)observation["app"] + " native route decision");
                    string recovered = (string)observation["recoveredDomain"];
                    if (recovered != null)
                        Check(events.Any(line => line.TrimEnd().EndsWith("found fakeip domain: " + recovered, StringComparison.Ordinal)),
                            (string)observation["app"] + " native FakeIP domain recovery");
                    observation["observedTag"] = expectedTag;
                }
                File.WriteAllText(Path.Combine(root, "app-trials.json"), Json.Serialize(observations));
            }
        }
    }
}
