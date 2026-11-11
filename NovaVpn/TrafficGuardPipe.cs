using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NovaVpn;

public enum GuardControlCommand : byte { Status = 1, Enable = 2, Disable = 3, AttachTunnel = 4 }
public enum GuardControlResult : byte { Success = 0, Denied = 1, Invalid = 2, NotReady = 3, Failed = 4 }

public sealed class GuardControlFrame
{
    public byte Kind { get; }
    public Guid RequestId { get; }
    private readonly byte[] body;
    public byte[] Body => (byte[])body.Clone();
    public GuardControlFrame(byte kind, Guid requestId, byte[] body)
    {
        if (requestId == Guid.Empty) throw new FormatException("Missing control request ID.");
        if (body == null || body.Length > 65536 - 18) throw new FormatException("Invalid control body size.");
        Kind = kind; RequestId = requestId; this.body = (byte[])body.Clone();
    }
    public byte[] Encode()
    {
        byte[] encoded = new byte[18 + body.Length]; encoded[0] = 1; encoded[1] = Kind;
        Buffer.BlockCopy(RequestId.ToByteArray(), 0, encoded, 2, 16); Buffer.BlockCopy(body, 0, encoded, 18, body.Length); return encoded;
    }
    public static GuardControlFrame Decode(byte[] encoded, bool response)
    {
        if (encoded == null || encoded.Length < 18 || encoded.Length > 65536 || encoded[0] != 1) throw new FormatException("Invalid control protocol version or frame size.");
        if (response ? encoded[1] > 4 : encoded[1] < 1 || encoded[1] > 4) throw new FormatException("Unknown control frame kind.");
        byte[] id = new byte[16], body = new byte[encoded.Length - 18]; Buffer.BlockCopy(encoded, 2, id, 0, 16); Buffer.BlockCopy(encoded, 18, body, 0, body.Length);
        return new GuardControlFrame(encoded[1], new Guid(id), body);
    }
    internal static async Task WriteAsync(Stream stream, GuardControlFrame frame, CancellationToken token)
    {
        byte[] bytes = frame.Encode(); int length = bytes.Length;
        byte[] prefix = { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24) };
        await stream.WriteAsync(prefix, 0, prefix.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
    }
    internal static async Task<GuardControlFrame> ReadAsync(Stream stream, bool response, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await ReadExactly(stream, prefix, token).ConfigureAwait(false);
        uint length = (uint)(prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24);
        if (length < 18 || length > 65536) throw new FormatException("Invalid control frame length.");
        byte[] bytes = new byte[length]; await ReadExactly(stream, bytes, token).ConfigureAwait(false);
        return Decode(bytes, response);
    }
    private static async Task ReadExactly(Stream stream, byte[] bytes, CancellationToken token)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException(); offset += count;
        }
    }
}

public sealed class GuardControlPeer
{
    public string UserSid { get; }
    public uint ProcessId { get; }
    internal GuardControlPeer(string sid, uint processId) { UserSid = sid; ProcessId = processId; }
}

// Transport only. No command activates protection until the service dispatcher,
// installed owner policy and administrative recovery path are integrated.
internal sealed class GuardControlPipe : IDisposable
{
    internal const string Name = "NovaVPN.TrafficGuard.v1";
    private readonly NamedPipeServerStream server;
    private readonly string ownerSid;
    private readonly int timeout;
    private readonly Func<GuardControlFrame, GuardControlPeer, GuardControlFrame> handler;
    private int running;
    internal static GuardControlPipe OpenForService(string installedOwnerSid, Func<GuardControlFrame, GuardControlPeer, GuardControlFrame> handler)
    {
        TrafficGuardServiceIdentity.Demand();
        return new GuardControlPipe(Name, installedOwnerSid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, 5000, handler);
    }
    internal static void ValidateServiceServer(NamedPipeClientStream client)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)).Equals(system) || !GetNamedPipeServerProcessId(client.SafePipeHandle, out uint processId))
            throw new UnauthorizedAccessException("Untrusted traffic guard pipe owner or process.");
        TrafficGuardServiceIdentity.ValidateServerProcess(processId);
    }
    internal static Task<GuardControlFrame> ExchangeForServiceAsync(GuardControlFrame request, CancellationToken token)
        => ExchangeAsync(Name, request, ValidateServiceServer, 10000, token);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint access, uint mode, uint maximum, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafePipeHandle file, IntPtr overlapped);

    internal static PipeSecurity Security(string ownerSid, string serverSid)
    {
        var owner = new SecurityIdentifier(ownerSid); var service = new SecurityIdentifier(serverSid);
        var security = new PipeSecurity(); security.SetOwner(service); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(service, PipeAccessRights.FullControl, AccessControlType.Allow));
        // GENERIC_WRITE would also grant FILE_CREATE_PIPE_INSTANCE (bit 4).
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }
    internal GuardControlPipe(string name, string ownerSid, string serverSid, int timeoutMilliseconds, Func<GuardControlFrame, GuardControlPeer, GuardControlFrame> handler)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 160 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) throw new ArgumentException("Invalid local pipe name.");
        if (timeoutMilliseconds < 50 || timeoutMilliseconds > 30000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        this.ownerSid = new SecurityIdentifier(ownerSid).Value; timeout = timeoutMilliseconds; this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        byte[] descriptor = Security(ownerSid, serverSid).GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            // DUPLEX | OVERLAPPED | FIRST_PIPE_INSTANCE; BYTE | REJECT_REMOTE_CLIENTS.
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3u | 0x40000000u | 0x80000u, 8, 1, 65536, 65536, 5000, ref security);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { server = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { pinned.Free(); }
    }
    private GuardControlPeer Peer()
    {
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) || pid == 0) throw new UnauthorizedAccessException("Unidentified pipe client.");
        string sid = null;
        // Invoke only after reading the request, as required by pipe impersonation.
        // Never run a handler or native WFP operation while impersonating a peer.
        server.RunAsClient(() =>
        {
            using (var identity = WindowsIdentity.GetCurrent(true))
                if (identity != null && identity.IsAuthenticated && !identity.IsAnonymous && identity.User != null) sid = identity.User.Value;
        });
        if (sid != ownerSid) throw new UnauthorizedAccessException("This account is not the installed tunnel owner.");
        return new GuardControlPeer(sid, pid);
    }
    internal async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Pipe loop already started.");
        using (token.Register(() => { try { CancelIoEx(server.SafePipeHandle, IntPtr.Zero); } catch (ObjectDisposedException) { } }))
        while (!token.IsCancellationRequested)
        {
            try { await server.WaitForConnectionAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (IOException) when (token.IsCancellationRequested) { break; }
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(timeout);
                    using (deadline.Token.Register(() => { try { CancelIoEx(server.SafePipeHandle, IntPtr.Zero); } catch (ObjectDisposedException) { } }))
                    {
                        var request = await GuardControlFrame.ReadAsync(server, false, deadline.Token).ConfigureAwait(false);
                        GuardControlFrame response;
                        try
                        {
                            var peer = Peer(); deadline.Token.ThrowIfCancellationRequested();
                            // Trusted synchronous dispatcher only. Native policy
                            // transactions have their own timeout and must finish
                            // before another request is accepted; IO deadlines do
                            // not abort a partially committed firewall operation.
                            response = handler(request, peer);
                            if (response == null || response.RequestId != request.RequestId || response.Kind > 4) throw new InvalidDataException("Invalid handler response.");
                        }
                        catch (UnauthorizedAccessException) { response = new GuardControlFrame((byte)GuardControlResult.Denied, request.RequestId, new byte[0]); }
                        catch (Exception) { response = new GuardControlFrame((byte)GuardControlResult.Failed, request.RequestId, new byte[0]); }
                        await GuardControlFrame.WriteAsync(server, response, deadline.Token).ConfigureAwait(false);
                        // DisconnectNamedPipe discards unread buffered bytes.
                        // An async bounded ACK proves the client consumed the
                        // response, without a blocking FlushFileBuffers call.
                        byte[] ack = new byte[1];
                        if (await server.ReadAsync(ack, 0, 1, deadline.Token).ConfigureAwait(false) != 1 || ack[0] != 0xA5)
                            throw new FormatException("Missing control response acknowledgement.");
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is FormatException || error is OperationCanceledException || error is Win32Exception) { }
            finally { server.Disconnect(); }
            // Keep the FIRST_PIPE_INSTANCE handle open across requests, avoiding
            // an interval in which another process could claim the service name.
        }
    }
    internal static async Task<GuardControlFrame> ExchangeAsync(string name, GuardControlFrame request, Action<NamedPipeClientStream> verifyServer, int timeout, CancellationToken token)
    {
        if (verifyServer == null) throw new ArgumentNullException(nameof(verifyServer));
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        using (var client = new NamedPipeClientStream(".", name, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None))
        {
            deadline.CancelAfter(timeout);
            using (deadline.Token.Register(() => { try { CancelIoEx(client.SafePipeHandle, IntPtr.Zero); } catch (InvalidOperationException) { } }))
            {
                await client.ConnectAsync(timeout, deadline.Token).ConfigureAwait(false);
                verifyServer(client); // Authenticate the server before disclosing any payload.
                await GuardControlFrame.WriteAsync(client, request, deadline.Token).ConfigureAwait(false);
                var response = await GuardControlFrame.ReadAsync(client, true, deadline.Token).ConfigureAwait(false);
                if (response.RequestId != request.RequestId) throw new InvalidDataException("Mismatched response ID.");
                await client.WriteAsync(new byte[] { 0xA5 }, 0, 1, deadline.Token).ConfigureAwait(false);
                return response;
            }
        }
    }
    public void Dispose() { server.Dispose(); }
}
