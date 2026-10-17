using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NovaVpn;

internal static class UpdateDownloadTests
{
    private sealed class Handler : HttpMessageHandler
    {
        internal byte[] Payload;
        internal int Calls;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Payload) });
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static AppReleaseInfo Release(byte[] bytes) { return new AppReleaseInfo {
        Version = new Version(9, 9, 9, 0), Tag = "v9.9.9", AssetName = "NOVA.VPN.Setup.9.9.9.exe",
        DownloadUrl = "https://github.com/leoraijin/NOVA-VPN/releases/download/v9.9.9/NOVA.VPN.Setup.9.9.9.exe",
        Size = bytes.Length, Sha256 = Hash(bytes) }; }
    private static void Case(string folder, string name, byte[] bytes, Action<AppReleaseInfo, Handler> change, bool success, bool existing = false)
    {
        string path = Path.Combine(folder, name + ".exe");
        var handler = new Handler { Payload = bytes };
        using (var service = new AppUpdateService())
        {
            var field = typeof(AppUpdateService).GetField("downloadClient", BindingFlags.Instance | BindingFlags.NonPublic);
            ((HttpClient)field.GetValue(service)).Dispose();
            field.SetValue(service, new HttpClient(handler));
            var release = Release(bytes);
            change(release, handler);
            if (existing) File.WriteAllText(path, "preserve-existing");
            bool completed = false;
            try { service.DownloadInstallerAsync(release, path).GetAwaiter().GetResult(); completed = true; }
            catch { if (success) throw; }
            Check(completed == success, "Incorrect result: " + name);
            if (existing) Check(File.ReadAllText(path) == "preserve-existing", "Existing destination overwritten");
            else Check(File.Exists(path) == success, "Incomplete or invalid download retained: " + name);
            if (success) Check(Hash(File.ReadAllBytes(path)) == release.Sha256, "Wrong file content");
            if (File.Exists(path)) File.Delete(path);
        }
    }
    public static int Main(string[] args)
    {
        try {
            string folder = Path.Combine(Path.GetFullPath(args[0]), "updater-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            byte[] bytes = new byte[] { 77, 90, 1, 2, 3, 4, 5, 6 };
            Case(folder, "valid", bytes, (r,h) => {}, true);
            Case(folder, "hash-mismatch", bytes, (r,h) => r.Sha256 = new string('a',64), false);
            Case(folder, "missing-hash", bytes, (r,h) => r.Sha256 = null, false);
            Case(folder, "invalid-hash", bytes, (r,h) => r.Sha256 = new string('z',64), false);
            Case(folder, "truncated", bytes, (r,h) => r.Size++, false);
            Case(folder, "too-large", bytes, (r,h) => r.Size = 201L * 1024 * 1024, false);
            Case(folder, "wrong-header", new byte[] { 1,2,3,4 }, (r,h) => {}, false);
            Case(folder, "http-error", bytes, (r,h) => h.Status = HttpStatusCode.NotFound, false);
            Case(folder, "untrusted-url", bytes, (r,h) => r.DownloadUrl = "https://example.org/update.exe", false);
            Case(folder, "existing", bytes, (r,h) => {}, false, true);
            string installer = Path.Combine(folder, "installer with spaces.exe");
            string target = Path.Combine(folder, "NOVA VPN") + Path.DirectorySeparatorChar;
            var plan = AppUpdateService.CreateInstallerStartInfo(installer, target, 123);
            Check(plan.FileName == installer && plan.UseShellExecute && plan.Verb == "runas", "Not a local elevated installer launch");
            Check(plan.WorkingDirectory == folder && plan.Arguments == "--update-path " + AppUpdateService.QuoteWindowsArgument(Path.GetFullPath(target)) + " --wait-pid 123 --delete-self-on-reboot", "Incorrect update arguments");
            bool rejected = false;
            try { AppUpdateService.CreateInstallerStartInfo(installer, target, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Invalid PID accepted");
            Directory.Delete(folder);
            Console.WriteLine("PASS: 10 download/integrity/failure cases; local elevated installer plan and safe arguments. No real installer or VPN was launched.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
