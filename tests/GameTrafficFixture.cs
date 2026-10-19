using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
internal static class GameTrafficFixture
{
    static int Main(string[] args)
    {
        try {
            using (var socket = new TcpClient()) {
                socket.ReceiveTimeout = 5000; socket.SendTimeout = 5000; socket.Connect(args[0], int.Parse(args[1]));
                Console.Error.WriteLine("Fixture PID=" + System.Diagnostics.Process.GetCurrentProcess().Id + "; image=" + System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName + "; source=" + socket.Client.LocalEndPoint);
                var stream = socket.GetStream(); byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: fixture.invalid\r\nConnection: close\r\n\r\n"); stream.Write(request, 0, request.Length);
                // HTTP completion is Content-Length, not transport EOF. A TUN relay may keep
                // a half-closed TCP connection alive after the entire response arrived.
                using (var reader = new StreamReader(stream, Encoding.ASCII)) {
                    string status = reader.ReadLine();
                    if (status == null || !status.StartsWith("HTTP/1.1 200 ")) throw new IOException("Unexpected HTTP status: " + status);
                    int length = -1; string line; int headerBytes = status.Length;
                    while ((line = reader.ReadLine()) != null && line.Length != 0) {
                        headerBytes += line.Length;
                        if (headerBytes > 8192) throw new IOException("Response headers too large");
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                            !int.TryParse(line.Substring(15).Trim(), out length)) throw new IOException("Invalid response length");
                    }
                    if (line == null || length < 0 || length > 4096) throw new IOException("Missing or oversized response length");
                    char[] body = new char[length]; int offset = 0;
                    while (offset < body.Length) {
                        int count = reader.Read(body, offset, body.Length - offset);
                        if (count == 0) throw new EndOfStreamException("Incomplete HTTP body");
                        offset += count;
                    }
                    Console.Write(new string(body));
                }
            }
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }
}
