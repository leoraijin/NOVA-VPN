using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Read-only onboarding only. Deployment must use a separate durable transaction;
// a successful report is not permission to replace an existing service.
public sealed class VpsSshPreflightRunner
{
    public static string WindowsSshPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32", "OpenSSH", "ssh.exe");

    public Task<VpsPreflightReport> InspectAsync(VpsSshTarget target, string identityFile, string privateRuntimeDirectory, CancellationToken cancellation)
        => InspectAsync(target, identityFile, privateRuntimeDirectory, WindowsSshPath, TimeSpan.FromSeconds(30), cancellation);

    internal async Task<VpsPreflightReport> InspectAsync(VpsSshTarget target, string identityFile, string privateRuntimeDirectory,
        string sshExecutable, TimeSpan timeout, CancellationToken cancellation)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellation.ThrowIfCancellationRequested();
        // Validate literal executable/identity/trust paths before creating runtime files.
        string runtime = Path.GetFullPath(privateRuntimeDirectory);
        string session = Path.Combine(runtime, "vps-preflight-" + Guid.NewGuid().ToString("N"));
        string hosts = Path.Combine(session, "known_hosts");
        var info = target.CreatePreflightStartInfo(sshExecutable, identityFile, hosts);
        if (!File.Exists(info.FileName) || !File.Exists(Path.GetFullPath(identityFile))) throw new FileNotFoundException("Не найден OpenSSH или выбранный файл ключа.");
        foreach (string variable in new[] { "SSH_AUTH_SOCK", "SSH_ASKPASS", "SSH_SK_PROVIDER", "DISPLAY" }) info.EnvironmentVariables.Remove(variable);
        try {
            PrivateRuntimeFiles.PrepareDirectory(session);
            using (var stream = new FileStream(hosts, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(target.KnownHostsLine);
            using (var pinned = new FileStream(hosts, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var process = new Process { StartInfo = info, EnableRaisingEvents = true }) {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += delegate { exited.TrySetResult(true); };
                timer.CancelAfter(timeout);
                bool started = false; Task pending = null;
                try {
                    timer.Token.ThrowIfCancellationRequested();
                    started = process.Start();
                    if (!started) throw new IOException("Не удалось запустить SSH-проверку.");
                    Action abort = () => TryKill(process);
                    Task<string> output = ReadBoundedAsync(process.StandardOutput, 8192, abort);
                    Task<string> errors = ReadBoundedAsync(process.StandardError, 8192, abort);
                    Task input = SendScriptAsync(process, abort);
                    var completed = Task.WhenAll(exited.Task, output, errors, input);
                    pending = completed;
                    Task canceled = Task.Delay(Timeout.Infinite, timer.Token);
                    if (await Task.WhenAny(completed, canceled).ConfigureAwait(false) != completed) {
                        TryKill(process);
                        if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                        throw new TimeoutException("Время SSH-проверки VPS истекло.");
                    }
                    await completed.ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new IOException("SSH-проверка не завершена. Проверьте адрес, публичный ключ сервера и доступ выбранного ключа пользователя.");
                    return VpsPreflightReport.Parse(await output.ConfigureAwait(false));
                }
                finally {
                    timer.Cancel();
                    if (pending != null) _ = pending.ContinueWith(faulted => { var observed = faulted.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    if (started) {
                        TryKill(process);
                        // Only our exact SSH process is observed/terminated, never by name.
                        if (!process.WaitForExit(3000)) throw new IOException("Остановка SSH-проверки не подтверждена.");
                    }
                }
            }
        }
        finally {
            // Session contains only this operation's public host key, no private key copy.
            try { File.Delete(hosts); Directory.Delete(session, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    static async Task SendScriptAsync(Process process, Action abort)
    {
        try { await process.StandardInput.WriteAsync(VpsPreflightReport.Script).ConfigureAwait(false); process.StandardInput.Close(); }
        catch { abort(); throw; }
    }
    static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, Action abort)
    {
        try {
            var buffer = new char[512]; var result = new StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0) {
                if (count > limit - result.Length) throw new InvalidDataException("Ответ SSH-проверки превысил допустимый размер.");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        catch { abort(); throw; }
    }
}
