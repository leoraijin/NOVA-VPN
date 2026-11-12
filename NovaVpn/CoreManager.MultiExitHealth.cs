using System;
using System.Linq;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed partial class CoreManager
{
    // Same private test seam as the legacy verificationHandlerFactory; production always uses the owned channel.
    private Func<string,Uri,System.Threading.CancellationToken,Task<int>> multiExitVerificationReader=null;
    private async Task<ConnectionVerification> VerifyMultiExitConnectionAsync(VerificationOperation operation)
    {
        // The channel captured exact outbound identities from the prepared immutable catalog.
        // Existing operation ownership binds it to core process, generation and network epoch.
        var result=new ConnectionVerification{ProfileId=operation.ProfileId};
        string network=ProfileHealthEvidence.NetworkKey(),core=ProfileHealthEvidence.CoreKey(operation.CorePath);
        System.Collections.ObjectModel.ReadOnlyCollection<ExitProbeResult> exits;
        try {exits=await MultiExitHealthRunner.RunAsync(operation.Channel.MultiTargets,
            multiExitVerificationReader??operation.Channel.ReadExitStatusAsync,operation.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(operation.Token.IsCancellationRequested) {return result;}
        if(operation.Token.IsCancellationRequested||!IsCurrentVerification(operation)||network!=ProfileHealthEvidence.NetworkKey()||core!=ProfileHealthEvidence.CoreKey(operation.CorePath))
            return new ConnectionVerification{ProfileId=operation.ProfileId,Message="Подключение изменилось во время проверки выходов"};
        result.MultiExits=new MultiExitHealthSnapshot(exits,network,core);
        result.InternetOk=result.MultiExits.AllVerified;
        result.DnsOk=result.InternetOk && exits.All(exit => !exit.UsesIpTarget);
        result.InternetLatencyMs=exits.Where(e=>e.Phase==ExitProbePhase.Verified).Select(e=>e.LatencyMs).DefaultIfEmpty(-1).Max();
        result.Message=result.MultiExits.Summary;
        if(TryPublishVerification(operation,operation.Generation,result))VerificationChanged?.Invoke(result);
        else return new ConnectionVerification{ProfileId=operation.ProfileId,Message="Подключение изменилось во время проверки выходов"};
        return result;
    }
}
