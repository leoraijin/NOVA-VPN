using System;
using System.Runtime.InteropServices;
using NovaVpn;
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
internal static class GuardSetupEntry
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--register-guard") return 64;
        try
        {
            string caller = WindowsGuardRegistration.DemandEntry();
            GuardRegistrationCoordinator.Register(new WindowsGuardRegistration(), caller);
            Console.WriteLine("NOVA guard registration verified. Service was not started; protection activation remains unavailable."); return 0;
        }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("Use the protected installed NOVA setup utility with elevated administrator rights."); return 5; }
        catch (Exception error) { Console.Error.WriteLine("Registration was not confirmed: " + error.GetType().Name + ". Existing service configuration and protection must be checked."); return 1; }
    }
}
