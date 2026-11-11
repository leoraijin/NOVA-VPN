using System;
using System.IO;
using System.Security.Principal;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using NovaVpn;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

internal static class GuardRecoveryEntry
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--unlock-nova-guard")
        {
            Console.Error.WriteLine("NOVA Guard Recovery --unlock-nova-guard");
            return 64;
        }
        try
        {
            TrafficGuardServiceIdentity.DemandRecoveryProcess();
            TrafficGuardServiceIdentity.StopMainServiceForRecovery();
            string directory = TrafficGuardServiceIdentity.PrepareRecoveryJournalDirectory();
            // Same exclusive lease as the service, held until the final Disabled
            // record is durable. A racing service restart cannot restore intent.
            using (var journal = OpenJournal(directory))
            using (var store = TrafficGuardNativeStore.OpenForRecovery())
            {
                var controller = new GuardRecoveryController(journal, store);
                controller.PrepareAdministrativeDisable();
                string owner = controller.OwnerSid;
                try { var installed = TrafficGuardServiceIdentity.ReadOwnerPolicyForRecovery(directory); if (installed != null) owner = installed.OwnerSid; }
                // Policy ownership is metadata for the final Disabled record,
                // not authority for an elevated administrative unlock. A damaged
                // owner file must not prevent removal of NOVA's own filters.
                catch (Exception error) when (error is FormatException || error is IOException || error is UnauthorizedAccessException) { }
                if (owner == null) { using (var identity = WindowsIdentity.GetCurrent()) owner = identity.User.Value; }
                controller.Disable(owner);
                if (controller.Status != GuardRecoveryStatus.Disabled || store.Snapshot().FilterCount != 0) throw new InvalidDataException("Removal was not verified.");
            }
            Console.WriteLine("NOVA guard filters removed; disabled intent saved. Other firewall providers were not reset.");
            return 0;
        }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("Run the protected installed NOVA recovery utility as administrator."); return 5; }
        catch (Exception error) { Console.Error.WriteLine("NOVA recovery did not complete: " + error.GetType().Name + ". Protection state must be checked."); return 1; }
    }
    private static GuardFileRecoveryJournal OpenJournal(string directory)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            TrafficGuardServiceIdentity.DemandRecovery();
            try { return new GuardFileRecoveryJournal(directory, true); }
            catch (IOException error) when ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33)
            {
                // SCM can report STOPPED just before the old process releases
                // its exclusive journal lease. Never bypass or delete that lock.
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(10)) throw;
                Thread.Sleep(100);
            }
        }
    }
}
