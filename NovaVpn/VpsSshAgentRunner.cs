using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Transport only. A lost response after dispatch is ambiguous; never silently
// retry a deployment or claim rollback. UI activation/native acceptance is pending.
public sealed class VpsSshAgentRunner
{
    public Task<VpsAgentResponse> ExecuteAsync(VpsSshTarget target, VpsAgentFrame frame, string identityFile, string privateRuntimeDirectory, CancellationToken cancellation)
        => ExecuteAsync(target, frame, identityFile, privateRuntimeDirectory, VpsSshPreflightRunner.WindowsSshPath, TimeSpan.FromMinutes(3), cancellation);

    internal async Task<VpsAgentResponse> ExecuteAsync(VpsSshTarget target, VpsAgentFrame frame, string identityFile, string privateRuntimeDirectory,
        string sshExecutable, TimeSpan timeout, CancellationToken cancellation)
    {
        if (target == null || frame == null || frame.TargetIdentityKey != target.IdentityKey) throw new ArgumentException("Запрос подготовлен для другого SSH-сервера.");
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellation.ThrowIfCancellationRequested();
        // Validate literal executable/identity/trust paths before creating runtime files.
        string runtime = Path.GetFullPath(privateRuntimeDirectory);
        string session = Path.Combine(runtime, "vps-agent-" + Guid.NewGuid().ToString("N"));
        string hosts = Path.Combine(session, "known_hosts");
        var info = target.CreateAgentStartInfo(sshExecutable, identityFile, hosts, frame);
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
                    Task input = SendFrameAsync(process, frame, timer.Token, abort);
                    var completed = Task.WhenAll(exited.Task, output, errors, input);
                    pending = completed;
                    Task canceled = Task.Delay(Timeout.Infinite, timer.Token);
                    if (await Task.WhenAny(completed, canceled).ConfigureAwait(false) != completed) {
                        TryKill(process);
                        if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                        throw new TimeoutException("Время обмена с VPS истекло.");
                    }
                    await completed.ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    return VpsAgentResponse.Parse(await output.ConfigureAwait(false), process.ExitCode, frame);
                }
                catch (Exception error) when (started) {
                    // Status is strictly read-only; cancelling it cannot leave a dispatched mutation.
                    if (frame.Action == "status" && cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                    string reason = error is OperationCanceledException ? "canceled" : error is TimeoutException ? "timeout" : error is InvalidDataException ? "protocol" : "transport";
                    throw new VpsRemoteOutcomeUnknownException(frame, reason);
                }
                finally {
                    timer.Cancel();
                    if (pending != null) _ = pending.ContinueWith(faulted => { var observed = faulted.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    if (started) {
                        TryKill(process);
                        // Only our exact SSH process is observed/terminated, never by name.
                        if (!process.WaitForExit(3000)) throw new VpsRemoteOutcomeUnknownException(frame, "stop_unconfirmed");
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
    static async Task SendFrameAsync(Process process, VpsAgentFrame frame, CancellationToken token, Action abort)
    {
        try { await frame.WriteAsync(process.StandardInput.BaseStream, token).ConfigureAwait(false); process.StandardInput.Close(); }
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

public sealed class VpsRemoteOutcomeUnknownException : IOException
{
    public string TargetIdentityKey { get; }
    public string InstallationId { get; }
    public string OperationId { get; }
    public string RequestSha256 { get; }
    public string Reason { get; }
    internal VpsRemoteOutcomeUnknownException(VpsAgentFrame frame, string reason)
        : base("Связь с VPS прервалась. Состояние операции не подтверждено; требуется проверка сервера.")
    {
        TargetIdentityKey=frame.TargetIdentityKey;InstallationId=frame.InstallationId;OperationId=frame.OperationId;RequestSha256=frame.RequestSha256;Reason=reason;
    }
}
