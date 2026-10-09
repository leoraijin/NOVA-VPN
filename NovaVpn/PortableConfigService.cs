using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace NovaVpn;

[DataContract]
public sealed class PortableConfigPackage
{
    [DataMember(Order = 1)] public string Format = "nova-portable-config-v1";
    [DataMember(Order = 2)] public DateTime CreatedUtc = DateTime.UtcNow;
    [DataMember(Order = 3)] public AppState State;
}

public static class PortableConfigService
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const string FullConfigFormat = "nova-portable-config-v1";
    private const string SettingsOnlyFormat = "nova-settings-no-secrets-v1";

    public static void Export(AppState state, string path)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не указан путь экспорта.", nameof(path));
        StateStore.NormalizeForSerialization(state);
        PortableConfigPackage package = new PortableConfigPackage { State = state };
        WritePackage(package, path);
    }

    public static void ExportSettingsOnly(AppState state, string path)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не указан путь экспорта.", nameof(path));

        StateStore.NormalizeForSerialization(state);
        AppState safeState = CloneState(state);
        ZapretRoutingTransitionService.SetActive(safeState, false);
        if (safeState.Zapret != null) safeState.Zapret.Mode = "VPN";
        safeState.Profiles = new System.Collections.Generic.List<VpnProfile>();
        safeState.SelectedProfileId = null;
        safeState.ConnectionHistory.Clear();
        safeState.AutoConnect = false;
        safeState.StartWithWindows = false;
        if (safeState.Visual != null) safeState.Visual.CustomBackgroundPath = "";
        if (safeState.Routing != null)
        {
            safeState.Routing.DirectProcessPaths = new System.Collections.Generic.List<string>();
            safeState.Routing.ProxyProcessPaths = new System.Collections.Generic.List<string>();
        }

        WritePackage(new PortableConfigPackage { Format = SettingsOnlyFormat, State = safeState }, path);
    }

    private static void WritePackage(PortableConfigPackage package, string path)
    {
        byte[] data;
        using (MemoryStream stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(PortableConfigPackage)).WriteObject(stream, package);
            data = stream.ToArray();
        }
        if (data.Length > MaxBytes) throw new InvalidDataException("Конфигурация слишком большая.");
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, data);
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }

    public static AppState Import(string path)
    {
        return ReadPackage(path, FullConfigFormat);
    }

    public static AppState ImportSettingsOnly(string path)
    {
        AppState state = ReadPackage(path, SettingsOnlyFormat);
        if (state.Profiles != null && state.Profiles.Count != 0)
            throw new InvalidDataException("Файл настроек не должен содержать VPN-профили.");
        state.Profiles = new System.Collections.Generic.List<VpnProfile>();
        state.SelectedProfileId = null;
        ZapretRoutingTransitionService.SetActive(state, false);
        if (state.Zapret != null) state.Zapret.Mode = "VPN";
        state.ConnectionHistory = new System.Collections.Generic.List<ConnectionHistoryItem>();
        state.AutoConnect = false;
        state.StartWithWindows = false;
        if (state.Visual != null) state.Visual.CustomBackgroundPath = "";
        if (state.Routing != null)
        {
            state.Routing.DirectProcessPaths = new System.Collections.Generic.List<string>();
            state.Routing.ProxyProcessPaths = new System.Collections.Generic.List<string>();
        }
        return state;
    }

    private static AppState ReadPackage(string path, string expectedFormat)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Переносимый конфиг не найден.", path);
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Конфигурация слишком большая.");
        PortableConfigPackage package;
        using (FileStream stream = File.OpenRead(path))
        {
            package = new DataContractJsonSerializer(typeof(PortableConfigPackage)).ReadObject(stream) as PortableConfigPackage;
        }
        if (package == null || !string.Equals(package.Format, expectedFormat, StringComparison.OrdinalIgnoreCase) || package.State == null)
            throw new InvalidDataException("Файл не является переносимым конфигом NOVA VPN.");
        return package.State;
    }

    private static AppState CloneState(AppState state)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppState));
            serializer.WriteObject(stream, state);
            stream.Position = 0;
            return serializer.ReadObject(stream) as AppState ?? throw new InvalidDataException("Не удалось подготовить настройки для экспорта.");
        }
    }
}
