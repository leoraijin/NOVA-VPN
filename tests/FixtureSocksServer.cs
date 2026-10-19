using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

internal sealed class FixtureSocksServer : IDisposable
{
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    readonly string response;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FixtureSocksServer(string response) { this.response = response; listener.Start(); _ = Task.Run(Accept); }
    async Task Accept() {
        try { while (true) { var client = await listener.AcceptTcpClientAsync(); _ = Task.Run(() => Serve(client)); } } catch (ObjectDisposedException) { } catch (SocketException) { }
    }
    static byte[] Read(NetworkStream stream, int count) { byte[] buffer = new byte[count]; int offset = 0; while (offset < count) { int n = stream.Read(buffer, offset, count - offset); if (n == 0) throw new EndOfStreamException(); offset += n; } return buffer; }
    void Serve(TcpClient client) {
        using (client) try {
            client.ReceiveTimeout = 5000; var stream = client.GetStream(); var hello = Read(stream, 2); Read(stream, hello[1]); stream.Write(new byte[] {5,0}, 0, 2);
            var request = Read(stream, 4); int size = request[3] == 1 ? 4 : request[3] == 4 ? 16 : Read(stream, 1)[0]; Read(stream, size + 2);
            byte[] reply = {5,0,0,1,127,0,0,1,0,0}; stream.Write(reply, 0, reply.Length);
            var header = new StringBuilder(); while (header.Length < 4096 && !header.ToString().EndsWith("\r\n\r\n")) { int b = stream.ReadByte(); if (b < 0) return; header.Append((char)b); }
            byte[] body = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + response.Length + "\r\nConnection: close\r\n\r\n" + response); stream.Write(body, 0, body.Length);
        } catch (IOException) { } catch (SocketException) { }
    }
    public void Dispose() { listener.Stop(); }
}
