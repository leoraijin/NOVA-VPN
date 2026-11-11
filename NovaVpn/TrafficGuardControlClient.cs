using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class GuardControlReply
{
    // Result acknowledges this request's outcome (including a deduplicated old
    // request); Snapshot is the fresh state. Success alone never means Armed.
    public GuardControlResult Result { get; }
    public GuardControlSnapshot Snapshot { get; }
    internal GuardControlReply(GuardControlFrame frame)
    {
        Result = (GuardControlResult)frame.Kind;
        if (frame.Body.Length != 0) Snapshot = GuardControlSnapshot.Decode(frame.Body);
        else if (Result == GuardControlResult.Success) throw new InvalidDataException("Successful guard reply has no verified state.");
    }
}

// Never starts a service or falls back to another transport/name. A missing or
// untrusted service is an error, not an independent-protection acknowledgement.
public static class TrafficGuardControlClient
{
    public static Task<GuardControlReply> QueryAsync(CancellationToken token)
        => SendAsync(new GuardControlFrame((byte)GuardControlCommand.Status, Guid.NewGuid(), new byte[0]), token);
    public static Task<GuardControlReply> DisableAsync(GuardControlSnapshot state, Guid requestId, CancellationToken token)
        => SendAsync(new GuardControlFrame((byte)GuardControlCommand.Disable, requestId, GuardControlPayload.Disable(state.Epoch, state.Revision)), token);
    public static Task<GuardControlReply> EnableAsync(GuardControlSnapshot state, Guid requestId, IEnumerable<GuardControlEndpoint> endpoints, CancellationToken token)
        => SendAsync(new GuardControlFrame((byte)GuardControlCommand.Enable, requestId, GuardControlPayload.Enable(state.Epoch, state.Revision, endpoints)), token);
    private static async Task<GuardControlReply> SendAsync(GuardControlFrame request, CancellationToken token)
        => new GuardControlReply(await GuardControlPipe.ExchangeForServiceAsync(request, token).ConfigureAwait(false));
}
