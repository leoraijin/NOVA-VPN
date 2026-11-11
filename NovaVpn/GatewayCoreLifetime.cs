using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace NovaVpn;

// Read-only lifetime handle, never process control or a WFP authorization.
internal sealed class GatewayCoreLifetime : IDisposable
{
    readonly object sync=new object();SafeProcessHandle process;
    internal GatewayCoreLifetime(int pid,long created,ApplicationBandwidthIdentity expected)
    {
        var held=OpenProcess(0x00101000,false,pid);
        try{if(held==null||held.IsInvalid||!LocalTcpPeerObservation.ReadIdentity(held,out var actualCreated,out var actual)||actualCreated!=created||actual.UserSid!=expected.UserSid||!String.Equals(actual.ExecutablePath,expected.ExecutablePath,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Не удалось подтвердить процесс DNS-ядра.");process=held;held=null;}finally{held?.Dispose();}
    }
    internal bool IsAlive{get{lock(sync)return process!=null&&!process.IsClosed&&WaitForSingleObject(process,0)==258;}}
    public void Dispose(){lock(sync){process?.Dispose();process=null;}}
    [DllImport("kernel32.dll",SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
}
