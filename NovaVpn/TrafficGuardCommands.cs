using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NovaVpn;

public enum GuardCoreKind : byte { SingBox = 1, Reality = 2 }
public sealed class GuardControlEndpoint
{
    public GuardCoreKind Core;
    public string Address;
    public int Port;
    public string Network;
}
public sealed class GuardControlSnapshot
{
    public Guid Epoch;
    public long Revision;
    public GuardRecoveryStatus Status;
    public bool CanEnable;
    public byte[] Encode()
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        { writer.Write(Epoch.ToByteArray()); writer.Write(Revision); writer.Write((byte)Status); writer.Write(CanEnable); return stream.ToArray(); }
    }
    public static GuardControlSnapshot Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 26) throw new FormatException("Invalid guard status size.");
        using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
        {
            var result = new GuardControlSnapshot { Epoch = new Guid(reader.ReadBytes(16)), Revision = reader.ReadInt64(), Status = (GuardRecoveryStatus)reader.ReadByte() };
            byte enable = reader.ReadByte();
            if (result.Epoch == Guid.Empty || result.Revision < 0 || !Enum.IsDefined(typeof(GuardRecoveryStatus), result.Status) || enable > 1) throw new FormatException("Invalid guard status.");
            result.CanEnable = enable != 0; return result;
        }
    }
}

public static class GuardControlPayload
{
    public static byte[] Disable(Guid epoch, long revision) => Encode(epoch, revision, null);
    public static byte[] Enable(Guid epoch, long revision, IEnumerable<GuardControlEndpoint> endpoints)
        => Encode(epoch, revision, endpoints ?? throw new ArgumentNullException(nameof(endpoints)));
    private static byte[] Encode(Guid epoch, long revision, IEnumerable<GuardControlEndpoint> endpoints)
    {
        if (epoch == Guid.Empty || revision < 0) throw new FormatException("Invalid guard state precondition.");
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(epoch.ToByteArray()); writer.Write(revision);
            if (endpoints != null)
            {
                var list = endpoints.Take(257).ToList(); if (list.Count > 256) throw new FormatException("Too many core endpoints.");
                writer.Write((ushort)list.Count);
                foreach (var endpoint in list)
                {
                    if (endpoint == null || !Enum.IsDefined(typeof(GuardCoreKind), endpoint.Core) || !IPAddress.TryParse(endpoint.Address, out var ip) || endpoint.Port < 1 || endpoint.Port > 65535 || endpoint.Network != "tcp" && endpoint.Network != "udp") throw new FormatException("Invalid core endpoint.");
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0) throw new FormatException("Scoped core endpoints require an interface constraint.");
                    byte[] address = ip.GetAddressBytes(); writer.Write((byte)endpoint.Core); writer.Write((byte)address.Length); writer.Write(address); writer.Write((ushort)endpoint.Port); writer.Write((byte)(endpoint.Network == "tcp" ? 6 : 17));
                }
            }
            return stream.ToArray();
        }
    }
    internal static TrafficGuardPolicy Parse(byte[] bytes, bool enable, Guid epoch, long revision, string owner, Func<GuardCoreKind, string> executable)
    {
        if (bytes.Length < 24) throw new FormatException("Missing guard state precondition.");
        using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
        {
            if (new Guid(reader.ReadBytes(16)) != epoch || reader.ReadInt64() != revision) throw new InvalidOperationException("Stale guard state.");
            var policy = new TrafficGuardPolicy { OwnerSid = owner };
            if (enable)
            {
                int count = reader.ReadUInt16(); if (count > 256) throw new FormatException("Too many core endpoints.");
                for (int i = 0; i < count; i++)
                {
                    var core = (GuardCoreKind)reader.ReadByte(); if (!Enum.IsDefined(typeof(GuardCoreKind), core)) throw new FormatException("Unknown protected core.");
                    int size = reader.ReadByte(); if (size != 4 && size != 16) throw new FormatException("Invalid core address size.");
                    byte[] address = reader.ReadBytes(size); if (address.Length != size) throw new EndOfStreamException();
                    int port = reader.ReadUInt16(), network = reader.ReadByte(); if (port == 0 || network != 6 && network != 17) throw new FormatException("Invalid core protocol or port.");
                    policy.Endpoints.Add(new TrafficGuardEndpoint { ApplicationPath = executable(core), Address = new IPAddress(address).ToString(), Port = port, Network = network == 6 ? "tcp" : "udp" });
                }
            }
            if (stream.Position != stream.Length) throw new FormatException("Unexpected guard command bytes.");
            TrafficGuardCompiler.Compile(policy); // Validate all IP/path/port constraints before any journal write.
            return policy;
        }
    }
}

internal sealed class GuardCommandDispatcher
{
    private readonly object sync = new object();
    private readonly GuardOwnerPolicy owner;
    private readonly GuardRecoveryController controller;
    private readonly Func<GuardCoreKind, string> executable;
    private readonly bool canEnable;
    private readonly Guid epoch = Guid.NewGuid();
    private readonly Dictionary<Guid, Tuple<string, GuardControlFrame>> responses = new Dictionary<Guid, Tuple<string, GuardControlFrame>>();
    private readonly Queue<Guid> order = new Queue<Guid>();
    internal GuardCommandDispatcher(GuardOwnerPolicy owner, GuardRecoveryController controller, Func<GuardCoreKind, string> executable, bool canEnable)
    { this.owner = owner ?? throw new ArgumentNullException(nameof(owner)); this.controller = controller ?? throw new ArgumentNullException(nameof(controller)); this.executable = executable ?? throw new ArgumentNullException(nameof(executable)); this.canEnable = canEnable; }
    private GuardControlFrame Reply(GuardControlFrame request, GuardControlResult result)
    {
        var status = controller.RefreshStatus();
        return new GuardControlFrame((byte)result, request.RequestId, new GuardControlSnapshot { Epoch = epoch, Revision = controller.Revision, Status = status, CanEnable = canEnable && OwnerCompatible(status) }.Encode());
    }
    private bool OwnerCompatible(GuardRecoveryStatus status)
        => controller.OwnerSid == null || controller.OwnerSid == owner.OwnerSid || status == GuardRecoveryStatus.Disabled;
    internal GuardControlFrame Execute(GuardControlFrame request, GuardControlPeer peer)
    {
        lock (sync)
        {
            // Authorization precedes cache lookup and every state read/mutation.
            if (peer == null || peer.ProcessId == 0 || peer.UserSid != owner.OwnerSid)
                return new GuardControlFrame((byte)GuardControlResult.Denied, request.RequestId, new byte[0]);
            if (request.Kind == (byte)GuardControlCommand.Status)
                return Reply(request, request.Body.Length == 0 ? GuardControlResult.Success : GuardControlResult.Invalid);
            string hash; using (var sha = SHA256.Create()) hash = Convert.ToBase64String(sha.ComputeHash(request.Encode()));
            if (responses.TryGetValue(request.RequestId, out var cached)) return Reply(request, cached.Item1 == hash ? (GuardControlResult)cached.Item2.Kind : GuardControlResult.Invalid);
            GuardControlFrame response;
            try
            {
                // After an administrative rescue, Disabled is verified against
                // an empty own-provider table. Only the installed owner may then
                // explicitly establish new intent; an active old owner is never transferred.
                if (!OwnerCompatible(controller.RefreshStatus())) response = Reply(request, GuardControlResult.NotReady);
                else if (request.Kind == (byte)GuardControlCommand.Enable || request.Kind == (byte)GuardControlCommand.Disable)
                {
                    var policy = GuardControlPayload.Parse(request.Body, request.Kind == (byte)GuardControlCommand.Enable, epoch, controller.Revision, peer.UserSid, executable);
                    if (request.Kind == (byte)GuardControlCommand.Enable && !canEnable) response = Reply(request, GuardControlResult.NotReady);
                    else
                    {
                        if (request.Kind == (byte)GuardControlCommand.Enable) controller.Enable(policy); else controller.Disable(peer.UserSid);
                        response = Reply(request, GuardControlResult.Success);
                    }
                }
                else response = Reply(request, GuardControlResult.NotReady); // Attested service-owned TUN attachment is still pending.
            }
            catch (Exception error) when (error is FormatException || error is ArgumentException || error is EndOfStreamException) { response = Reply(request, GuardControlResult.Invalid); }
            catch (InvalidOperationException) { response = Reply(request, GuardControlResult.NotReady); }
            catch (Exception) { response = Reply(request, GuardControlResult.Failed); }
            responses.Add(request.RequestId, Tuple.Create(hash, response)); order.Enqueue(request.RequestId);
            while (order.Count > 256) responses.Remove(order.Dequeue());
            return response;
        }
    }
}
