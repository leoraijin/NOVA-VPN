param([string]$CorePath,[string]$WorkPath)
$ErrorActionPreference='Stop'
$jsonPath=Join-Path $WorkPath 'ip.json'
$config=ConvertFrom-Json ([IO.File]::ReadAllText($jsonPath))
$reservation=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$reservation.Start();$port=$reservation.LocalEndpoint.Port;$reservation.Stop()
$config.inbounds=@(@{type='mixed';tag='test-in';listen='127.0.0.1';listen_port=$port})
$config.route.auto_detect_interface=$false
$config|Add-Member -NotePropertyName log -NotePropertyValue @{level='info'} -Force
$path=Join-Path $WorkPath 'loopback.json'
[IO.File]::WriteAllText($path,($config|ConvertTo-Json -Depth 80),(New-Object Text.UTF8Encoding($false)))
$harness=@'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
public static class StrictDirectLoopback {
 public static void Run(int proxyPort) {
  var server = new TcpListener(IPAddress.Loopback, 0);
  server.Start();
  int targetPort = ((IPEndPoint)server.LocalEndpoint).Port;
  var serving = Task.Run(async () => {
   using (var socket = await server.AcceptTcpClientAsync()) {
    using (var stream = socket.GetStream()) {
     var buffer = new byte[4096]; await stream.ReadAsync(buffer, 0, buffer.Length);
     var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nDIRECT");
     await stream.WriteAsync(reply, 0, reply.Length);
    }
   }
  });
  try {
   using (var client = new TcpClient("127.0.0.1", proxyPort)) {
    client.ReceiveTimeout = 5000;
    using (var stream = client.GetStream()) {
     var request = Encoding.ASCII.GetBytes("GET http://127.0.0.1:" + targetPort + "/ HTTP/1.1\r\nHost: 127.0.0.1:" + targetPort + "\r\nConnection: close\r\n\r\n");
     stream.Write(request, 0, request.Length);
     var body = new StreamReader(stream).ReadToEnd();
     if (!body.Contains("DIRECT")) throw new Exception("Strict route did not reach physical direct destination");
    }
   }
   if (!serving.Wait(5000)) throw new Exception("Target did not receive request");
   server.Stop();
   using (var failed = new TcpClient("127.0.0.1", proxyPort)) {
    failed.ReceiveTimeout = 5000;
    using (var stream = failed.GetStream()) {
     var request = Encoding.ASCII.GetBytes("GET http://127.0.0.1:" + targetPort + "/ HTTP/1.1\r\nHost: 127.0.0.1:" + targetPort + "\r\nConnection: close\r\n\r\n");
     stream.Write(request, 0, request.Length);
     string body = "";
     try { body = new StreamReader(stream).ReadToEnd(); } catch (IOException) { }
     if (body.Contains("200 OK")) throw new Exception("Unavailable direct destination unexpectedly succeeded");
    }
   }
  } finally {server.Stop();}
 }
}
'@
Add-Type -TypeDefinition $harness
$stdout=Join-Path $WorkPath 'loopback-core.out.log';$stderr=Join-Path $WorkPath 'loopback-core.err.log'
$process=Start-Process -FilePath $CorePath -ArgumentList @('run','-c',('"'+$path+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
 $ready=$false
 for($i=0;$i -lt 40;$i++){
  if($process.HasExited){throw 'Loopback core exited'}
  $probe=New-Object Net.Sockets.TcpClient
  try{$probe.Connect('127.0.0.1',$port);$ready=$true;break}catch{Start-Sleep -Milliseconds 100}finally{$probe.Dispose()}
 }
 if(-not $ready){throw 'Loopback proxy not ready'}
 [StrictDirectLoopback]::Run($port)
} finally {if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}
$log=[IO.File]::ReadAllText($stderr)+[IO.File]::ReadAllText($stdout)
if($log -notmatch 'outbound/direct\[nova-strict-direct\]'){throw 'Dedicated direct exit absent in runtime log'}
if($log -match 'outbound/socks\[proxy\]'){throw 'Unexpected proxy use for direct site'}
'PASS: real HTTP direct traffic and failed direct connection without proxy fallback; no TUN or system route changes.'
