using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
class GameTrafficProtocolTests {
 static int Main(string[] args) { try {Run(args[0]).GetAwaiter().GetResult();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;} }
 static async Task Run(string fixture) {
  foreach(bool truncated in new[]{false,true}) {
   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
   var accepted=listener.AcceptTcpClientAsync();
   var process=Process.Start(new ProcessStartInfo(fixture,"127.0.0.1 "+((IPEndPoint)listener.LocalEndpoint).Port){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
   try {using(var peer=await accepted) {
    var stream=peer.GetStream();var text=new StringBuilder();var b=new byte[1];while(!text.ToString().EndsWith("\r\n\r\n")){if(await stream.ReadAsync(b,0,1)==0)throw new Exception("No HTTP request");text.Append((char)b[0]);}
    byte[] reply=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\n"+(truncated?"REG":"REGULAR"));await stream.WriteAsync(reply,0,reply.Length);
    if(truncated)peer.Close();
    // Deliberately retain transport ownership after the complete HTTP message.
    if(!process.WaitForExit(2500)){process.Kill();throw new Exception("Complete HTTP response incorrectly waited for TCP EOF");}
    string output=await process.StandardOutput.ReadToEndAsync(),error=await process.StandardError.ReadToEndAsync();
    if(!truncated && (process.ExitCode!=0 || output!="REGULAR"))throw new Exception("Complete response failed: "+error);
    if(truncated && (process.ExitCode==0 || !error.Contains("EndOfStreamException")))throw new Exception("Truncated response accepted: "+error);
   }}finally{if(!process.HasExited)process.Kill();process.Dispose();listener.Stop();}
  }
  using(var server=new FixtureSocksServer("REGULAR")) {
   var idle=new System.Collections.Generic.List<TcpClient>();try {
    for(int i=0;i<48;i++){var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,server.Port);idle.Add(client);}
    using(var client=new TcpClient()) {await client.ConnectAsync(IPAddress.Loopback,server.Port);var stream=client.GetStream();stream.ReadTimeout=2000;
     stream.Write(new byte[]{5,1,0},0,3);var reply=new byte[2];int read=0;while(read<2){int count=stream.Read(reply,read,2-read);if(count==0)throw new EndOfStreamException();read+=count;}if(reply[1]!=0)throw new Exception("Idle probes blocked real SOCKS handshake");}
   }finally{foreach(var client in idle)client.Dispose();}
  }
  Console.WriteLine("PASS: complete HTTP returns before TCP EOF; truncated body rejected; 48 idle probes do not block SOCKS handshake. Loopback only, no TUN.");
 }
}