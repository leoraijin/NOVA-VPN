using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
internal static class UdpTrafficFixture
{
    static void Main(string[] args) {
        using (var udp = new UdpClient()) {
            var target = new IPEndPoint(IPAddress.Parse(args[0]), int.Parse(args[1]));
            for (int i = 0; i < 200; i++) { udp.Send(new byte[64], 64, target); Thread.Sleep(50); }
        }
    }
}
