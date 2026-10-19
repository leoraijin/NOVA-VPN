using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

public enum GamingSessionPhase { Idle, Preparing, Active, Restoring, RecoveryRequired }

[DataContract]
public sealed class GamePreferences
{
    [DataMember] public string GameId;
    [DataMember] public string ProfileId;
    [DataMember] public string Host = "";
    [DataMember] public int Port = 443;
    [DataMember] public bool A2S;
    [DataMember] public bool AllowDirect;
    [DataMember] public bool StopWhenGameExits = true;
    [DataMember] public bool EndOnDisconnect = true;
    [DataMember] public System.Collections.Generic.List<SavedGameEndpoint> LastEndpoints;
    [DataMember] public string ObservedUtc;
    [DataMember] public string ObservedNetwork;
}

[DataContract]
public sealed class SavedGameEndpoint
{
    [DataMember] public string Address;
    [DataMember] public int Port;
    [DataMember] public string Network;
    public GameEndpoint ToEndpoint() => new GameEndpoint { Address = Address, Port = Port, Network = Network };
}

public sealed class GamingSessionManager
{
    private readonly string journal;
    public GamingSessionPhase Phase { get; private set; }
    public bool BaseWasConnected { get; private set; }
    public string BaseProfileId { get; private set; }
    public bool GameWasSeen { get; set; }
    public DateTime StartedUtc { get; private set; }
    public GamingSessionManager(string directory) { journal = Path.Combine(directory, "gaming-session.json"); }
    public bool Recover()
    {
        // Journal is a marker only. It contains no profile keys and never reactivates an overlay.
        if (!File.Exists(journal) && !File.Exists(journal + ".tmp")) return false;
        Phase = GamingSessionPhase.RecoveryRequired;
        string runtime = Path.Combine(Path.GetDirectoryName(journal), "runtime");
        if (Directory.Exists(runtime) && (File.GetAttributes(runtime) & FileAttributes.ReparsePoint) == 0) {
            foreach (string file in Directory.EnumerateFiles(runtime, "*.json")) {
                string name = Path.GetFileName(file);
                if (name == "active.json" || System.Text.RegularExpressions.Regex.IsMatch(name, "^(gaming|map)-[a-f0-9]{32}\\.json$")) {
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        Finish(); return true;
    }
    public void Prepare(string gameId, bool baseConnected, string baseProfileId)
    {
        if (Phase != GamingSessionPhase.Idle) throw new InvalidOperationException("Игровая сессия уже существует.");
        Directory.CreateDirectory(Path.GetDirectoryName(journal));
        string json = new JavaScriptSerializer().Serialize(new { schema = 1, session = Guid.NewGuid().ToString("N"), startedUtc = DateTime.UtcNow.ToString("O") });
        string temporary = journal + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
            byte[] bytes = Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
        if (File.Exists(journal)) File.Replace(temporary, journal, null); else File.Move(temporary, journal);
        BaseWasConnected = baseConnected; BaseProfileId = baseProfileId; GameWasSeen = false;
        StartedUtc = DateTime.UtcNow; Phase = GamingSessionPhase.Preparing;
    }
    public void Activate() { if (Phase != GamingSessionPhase.Preparing) throw new InvalidOperationException(); Phase = GamingSessionPhase.Active; }
    public void Restoring() { Phase = GamingSessionPhase.Restoring; }
    public void AbortRestore() { if (Phase == GamingSessionPhase.Restoring) Phase = GamingSessionPhase.Active; }
    public void Finish() { if (File.Exists(journal)) File.Delete(journal); if (File.Exists(journal + ".tmp")) File.Delete(journal + ".tmp"); Phase = GamingSessionPhase.Idle; GameWasSeen = false; }
}
