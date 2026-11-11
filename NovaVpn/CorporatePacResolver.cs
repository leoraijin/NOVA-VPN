using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class CorporatePacQuery
{
    public Uri Script { get; }
    public Uri Target { get; }
    public CorporatePacQuery(string script, string target) { Script = Address(script, 4096); Target = Address(target, 2048); }
    private static Uri Address(string text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" && uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Укажите HTTP(S)-адрес без логина, пароля, фрагмента # и управляющих символов.");
        return uri;
    }
    public static string ProfileTarget(VpnProfile profile)
    {
        if (profile == null || profile.Protocol == "sing-box" || profile.HasMissingCorporateBase) throw new NotSupportedException("Для проверки адреса VPN выберите отдельный профиль с известным сервером.");
        var node = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ConfigBuilder.ExportBaseOutbound(profile));
        CorporateProxy.ValidateTcpOutbound(node);
        if (!node.TryGetValue("server", out object host) || !(host is string server) || Uri.CheckHostName(server) == UriHostNameType.Unknown ||
            !node.TryGetValue("server_port", out object value) || !int.TryParse(Convert.ToString(value), out int port) || port < 1 || port > 65535)
            throw new NotSupportedException("Не удалось определить TCP-адрес сервера профиля.");
        bool tls = node.TryGetValue("tls", out object raw) && raw is Dictionary<string, object> map && map.TryGetValue("enabled", out object enabled) && Equals(enabled, true);
        // This represents the TCP server, not an application URL or a share link.
        // UUID/password/SNI/path/query from the VPN profile are never disclosed.
        return new UriBuilder(tls ? "https" : "http", server, port, "/").Uri.AbsoluteUri;
    }
}

public sealed class CorporatePacEntry
{
    public int Position { get; }
    public bool IsProxy { get; }
    public bool IsBypass { get; }
    public int NativeScheme { get; }
    public string Server { get; }
    public int Port { get; }
    public string KindLabel => !IsProxy ? (IsBypass ? "DIRECT · исключение" : "DIRECT") : NativeScheme == 1 ? "HTTP CONNECT" : NativeScheme == 2 ? "HTTPS CONNECT" : NativeScheme == 4 ? "SOCKS · версия неизвестна" : "Неизвестный тип " + NativeScheme;
    public string PortLabel => IsProxy ? Port.ToString() : "—";
    public string PolicyLabel => !IsProxy ? "Прямой выход; автоматически не разрешён" : NativeScheme == 4 ? "Нельзя автоматически считать SOCKS5" : NativeScheme == 1 || NativeScheme == 2 ? "Кандидат прокси; подключение не проверено" : "Не поддерживается";
    public CorporatePacEntry(int position, bool proxy, bool bypass, int scheme, string server, int port)
    {
        if (position < 1 || position > 32) throw new InvalidDataException("Слишком много результатов PAC.");
        if (proxy && (string.IsNullOrEmpty(server) || server.Length > 253 || server.Any(char.IsControl) || server.Contains("%") || Uri.CheckHostName(server) == UriHostNameType.Unknown || port < 1 || port > 65535))
            throw new InvalidDataException("Некорректный адрес прокси в ответе PAC.");
        Position = position; IsProxy = proxy; IsBypass = !proxy && bypass; NativeScheme = proxy ? scheme : 0; Server = proxy ? server : ""; Port = proxy ? port : 0;
    }
}

public sealed class CorporatePacInspection
{
    public CorporatePacQuery Query { get; }
    public IReadOnlyList<CorporatePacEntry> Entries { get; }
    public DateTime CheckedUtc { get; }
    public string NetworkStamp { get; }
    public CorporatePacInspection(CorporatePacQuery query, IEnumerable<CorporatePacEntry> entries, string network)
    {
        if (query == null || entries == null) throw new ArgumentNullException();
        var copy = entries.Take(33).ToArray();
        if (copy.Length < 1 || copy.Length > 32 || copy.Where((entry, index) => entry == null || entry.Position != index + 1).Any()) throw new InvalidDataException("Неполный или неверно упорядоченный ответ PAC.");
        Query = query; Entries = Array.AsReadOnly(copy); NetworkStamp = network; CheckedUtc = DateTime.UtcNow;
    }
    public bool IsCurrent(CorporatePacQuery query, string network, DateTime now) => query != null && !string.IsNullOrEmpty(network) && network == NetworkStamp &&
        query.Script.AbsoluteUri == Query.Script.AbsoluteUri && query.Target.AbsoluteUri == Query.Target.AbsoluteUri && now >= CheckedUtc && now - CheckedUtc < TimeSpan.FromMinutes(3);
}

// WinHTTP evaluates PAC out of process. Only an explicit URL is supplied: no
// WPAD/static/system proxy discovery, Windows logon credentials or reordering.
public static class CorporatePacResolver
{
    private static readonly SemaphoreSlim Slots = new SemaphoreSlim(4, 4);
    private static readonly ConcurrentDictionary<IntPtr, Request> Requests = new ConcurrentDictionary<IntPtr, Request>();
    private static readonly NativeCallback Callback = OnStatus; // Root for process lifetime, including late native callbacks.
    private static long nextContext;
    private const uint Complete = 0x01000000, Error = 0x00200000, Closing = 0x00000800;
    private const uint OptionsFlags = 0x00000002 | 0x00040000 | 0x00080000 | 0x00100000; // explicit URL, no DirectAccess/client/service result cache
    public static async Task<CorporatePacInspection> InspectAsync(CorporatePacQuery query, CancellationToken token)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            Request request = null; bool acquired = false;
            try
            {
                await Slots.WaitAsync(deadline.Token).ConfigureAwait(false); acquired = true;
                string network = ProfileHealthEvidence.NetworkKey();
                request = new Request(); request.Start(query); acquired = false; // Request now owns its capacity until native handle closure.
                using (deadline.Token.Register(request.Cancel))
                {
                    var entries = await request.Result.Task.ConfigureAwait(false); deadline.Token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(network) || ProfileHealthEvidence.NetworkKey() != network) throw new InvalidOperationException("Сеть изменилась во время проверки PAC.");
                    return new CorporatePacInspection(query, entries, network);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("PAC не ответил за 12 секунд."); }
            finally
            {
                if (request != null) { request.Close(); if (request.OwnsSlot) acquired = false; }
                if (acquired) Slots.Release();
            }
        }
    }
    private static void OnStatus(IntPtr handle, UIntPtr context, uint status, IntPtr info, uint size)
    {
        if (context == UIntPtr.Zero || !Requests.TryGetValue(new IntPtr(unchecked((long)context.ToUInt64())), out var request)) return;
        // Never allow a managed exception to unwind through a native callback.
        try { request.Status(handle, status, info, size); }
        catch (Exception error) { request.Result.TrySetException(error); request.Close(); }
    }
    private sealed class Request
    {
        private readonly object gate = new object();
        internal readonly TaskCompletionSource<CorporatePacEntry[]> Result = new TaskCompletionSource<CorporatePacEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr session, resolver, script, target, options;
        private IntPtr contextKey;
        private bool closeStarted;
        private int released;
        internal bool OwnsSlot { get; private set; }
        internal void Start(CorporatePacQuery query)
        {
            long id = Interlocked.Increment(ref nextContext);
            if (id < 1 || IntPtr.Size == 4 && id > int.MaxValue) throw new InvalidOperationException("PAC request context exhausted.");
            var context = new UIntPtr((ulong)id); contextKey = new IntPtr(id);
            session = WinHttpOpen("NOVA-PAC/1.0", 1, null, null, 0x10000000);
            if (session == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!WinHttpSetTimeouts(session, 5000, 5000, 5000, 5000)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WinHttpSetStatusCallback(session, Callback, Complete | Error | Closing, UIntPtr.Zero) == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint error = WinHttpCreateProxyResolver(session, out resolver); if (error != 0) throw new Win32Exception((int)error);
            if (!WinHttpSetOption(resolver, 45, ref context, (uint)UIntPtr.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Requests.TryAdd(contextKey, this)) throw new InvalidOperationException("Duplicate native PAC context.");
            OwnsSlot = true;
            script = Marshal.StringToHGlobalUni(query.Script.AbsoluteUri); target = Marshal.StringToHGlobalUni(query.Target.AbsoluteUri);
            options = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AutoOptions)));
            Marshal.StructureToPtr(new AutoOptions { Flags = OptionsFlags, ConfigUrl = script }, options, false); // AutoLogon=false, AutoDetectFlags=0.
            error = WinHttpGetProxyForUrlEx(resolver, target, options, context);
            if (error != 997) throw new Win32Exception((int)error);
        }
        internal void Cancel() { Result.TrySetCanceled(); Close(); }
        internal void Close()
        {
            Result.TrySetCanceled();
            IntPtr child = IntPtr.Zero, parent = IntPtr.Zero;
            lock (gate)
            {
                if (closeStarted) return; closeStarted = true;
                child = resolver;
                if (child == IntPtr.Zero || !OwnsSlot) { parent = session; session = IntPtr.Zero; resolver = IntPtr.Zero; }
            }
            if (child != IntPtr.Zero) WinHttpCloseHandle(child);
            if (parent != IntPtr.Zero) WinHttpCloseHandle(parent);
            // Allocations after resolver registration stay rooted until HANDLE_CLOSING.
        }
        internal void Status(IntPtr handle, uint status, IntPtr info, uint size)
        {
            if (status == Closing)
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                Requests.TryRemove(contextKey, out _);
                IntPtr parent;
                lock (gate) { parent = session; session = IntPtr.Zero; resolver = IntPtr.Zero; }
                foreach (IntPtr pointer in new[] { options, target, script }) if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
                if (parent != IntPtr.Zero) WinHttpCloseHandle(parent);
                Slots.Release();
                Result.TrySetException(new IOException("PAC resolver closed before completion.")); return;
            }
            lock (gate)
            {
                if (closeStarted) return;
                if (status == Error)
                {
                    if (info == IntPtr.Zero || size != Marshal.SizeOf(typeof(AsyncResult))) throw new InvalidDataException("Invalid PAC error callback.");
                    var error = (AsyncResult)Marshal.PtrToStructure(info, typeof(AsyncResult));
                    if (error.Api.ToUInt64() != 6 || error.Error == 0) throw new InvalidDataException("Invalid PAC failure.");
                    Result.TrySetException(new Win32Exception((int)error.Error));
                }
                else if (status == Complete)
                {
                    uint error = WinHttpGetProxyResult(handle, out NativeResult result); if (error != 0) throw new Win32Exception((int)error);
                    try { Result.TrySetResult(ReadResult(result)); } finally { WinHttpFreeProxyResult(ref result); }
                }
            }
        }
    }
    internal static CorporatePacEntry[] ReadResult(NativeResult result)
    {
        if (result.Count < 1 || result.Count > 32 || result.Entries == IntPtr.Zero) throw new InvalidDataException("Invalid PAC entry count.");
        var entries = new CorporatePacEntry[result.Count]; int stride = Marshal.SizeOf(typeof(NativeEntry));
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = (NativeEntry)Marshal.PtrToStructure(IntPtr.Add(result.Entries, index * stride), typeof(NativeEntry));
            string host = entry.Proxy != 0 ? ReadHost(entry.Host) : null;
            entries[index] = new CorporatePacEntry(index + 1, entry.Proxy != 0, entry.Bypass != 0, entry.Scheme, host, entry.Port);
        }
        return entries;
    }
    private static string ReadHost(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) throw new InvalidDataException("Missing PAC proxy host.");
        for (int length = 0; length <= 253; length++) if (Marshal.ReadInt16(pointer, length * 2) == 0) return Marshal.PtrToStringUni(pointer, length);
        throw new InvalidDataException("PAC host is too long.");
    }
    [StructLayout(LayoutKind.Sequential)] internal struct AutoOptions { public uint Flags, Detect; public IntPtr ConfigUrl, Reserved; public uint ReservedValue; public int AutoLogon; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeResult { public uint Count; public IntPtr Entries; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeEntry { public int Proxy, Bypass, Scheme; public IntPtr Host; public ushort Port; }
    [StructLayout(LayoutKind.Sequential)] private struct AsyncResult { public UIntPtr Api; public uint Error; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void NativeCallback(IntPtr internet, UIntPtr context, uint status, IntPtr info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr WinHttpOpen(string agent, uint access, string proxy, string bypass, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSetTimeouts(IntPtr session, int resolve, int connect, int send, int receive);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll", SetLastError = true)] private static extern IntPtr WinHttpSetStatusCallback(IntPtr handle, NativeCallback callback, uint flags, UIntPtr reserved);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll")] private static extern uint WinHttpCreateProxyResolver(IntPtr session, out IntPtr resolver);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSetOption(IntPtr handle, uint option, ref UIntPtr buffer, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll")] private static extern uint WinHttpGetProxyForUrlEx(IntPtr resolver, IntPtr url, IntPtr options, UIntPtr context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll")] private static extern uint WinHttpGetProxyResult(IntPtr resolver, out NativeResult result);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll")] private static extern void WinHttpFreeProxyResult(ref NativeResult result);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpCloseHandle(IntPtr handle);
}
