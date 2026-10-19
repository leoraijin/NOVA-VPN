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
    int accepted, requests, completed, failed;
    public string Diagnostics => "accepted=" + accepted + ", requests=" + requests + ", completed=" + completed + ", failed=" + failed;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FixtureSocksServer(string response) { this.response = response; listener.Start(); _ = Task.Run(Accept); }
    async Task Accept() {
        try { while (true) { var client = await listener.AcceptTcpClientAsync(); System.Threading.Interlocked.Increment(ref accepted); _ = Serve(client); } } catch (ObjectDisposedException) { } catch (SocketException) { }
    }
    static async Task<byte[]> Read(NetworkStream stream, int count) { byte[] buffer = new byte[count]; int offset = 0; while (offset < count) { int n = await stream.ReadAsync(buffer, offset, count - offset); if (n == 0) throw new EndOfStreamException(); offset += n; } return buffer; }
    async Task Serve(TcpClient client) {
        using (client) try {
            using (var deadline = new System.Threading.CancellationTokenSource(5000))
        using (deadline.Token.Register(() => client.Close())) {
            var stream = client.GetStream(); var hello = await Read(stream, 2); await Read(stream, hello[1]); await stream.WriteAsync(new byte[] {5,0}, 0, 2);
            var request = await Read(stream, 4); int size = request[3] == 1 ? 4 : request[3] == 4 ? 16 : (await Read(stream, 1))[0]; await Read(stream, size + 2);
            byte[] reply = {5,0,0,1,127,0,0,1,0,0}; await stream.WriteAsync(reply, 0, reply.Length);
            var header = new StringBuilder(); while (header.Length < 4096 && !header.ToString().EndsWith("\r\n\r\n")) { byte[] b = await Read(stream, 1); header.Append((char)b[0]); }
            System.Threading.Interlocked.Increment(ref requests);
            byte[] body = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + response.Length + "\r\nConnection: close\r\n\r\n" + response); await stream.WriteAsync(body, 0, body.Length); System.Threading.Interlocked.Increment(ref completed);
        }
        } catch (IOException) { System.Threading.Interlocked.Increment(ref failed); } catch (SocketException) { System.Threading.Interlocked.Increment(ref failed); } catch (ObjectDisposedException) { System.Threading.Interlocked.Increment(ref failed); }
    }
    public void Dispose() { listener.Stop(); }
}
