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
                var stream = socket.GetStream(); byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: fixture.invalid\r\nConnection: close\r\n\r\n"); stream.Write(request, 0, request.Length);
                using (var reader = new StreamReader(stream)) { Console.Write(reader.ReadToEnd()); }
            }
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); return 1; }
    }
}
