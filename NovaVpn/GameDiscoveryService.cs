using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace NovaVpn;

[DataContract]
public sealed class InstalledGame
{
    [DataMember] public string Id;
    [DataMember] public string Name;
    [DataMember] public string Source;
    [DataMember] public string InstallDirectory;
    [DataMember] public List<string> Executables = new List<string>();
    [DataMember] public string ImagePath;
    [DataMember] public string LaunchUri;
    [DataMember] public string LauncherPath;
    [DataMember] public string LauncherProduct;
    public string Confidence => Executables.Count > 0 ? "Игровые файлы найдены" : "Выберите игровой EXE в подробностях";
    public bool IsRunning => RunningPaths().Any();
    public IEnumerable<string> RunningPaths()
    {
        var allowed = new HashSet<string>(Executables ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses()) using (process) {
            string path = null;
            try { path = GameProcessIdentity.ImagePath(process.Id); } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
            if (path != null && allowed.Contains(path)) yield return path;
            else if (path == null) {
                string matching = null;
                try { matching = allowed.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase)); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                if (matching != null) yield return matching; // Conservative when a protected game denies path access.
            }
        }
    }
    public static InstalledGame FromExecutable(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Выберите существующий игровой EXE.");
        string name = GameCatalog.Games.FirstOrDefault(g => g.Processes.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))?.Name ?? Path.GetFileNameWithoutExtension(path);
        return new InstalledGame { Id = GameDiscoveryService.StableId("manual", "", path), Name = name, Source = "Добавлено вручную", InstallDirectory = Path.GetDirectoryName(path), Executables = new List<string> { path } };
    }
}

public static class GameProcessIdentity
{
    public static string ImagePath(int id)
    {
        IntPtr handle = OpenProcess(0x1000, false, id);
        if (handle == IntPtr.Zero) return null;
        try { var value = new StringBuilder(32768); int length = value.Capacity; return QueryFullProcessImageName(handle, 0, value, ref length) ? value.ToString() : null; }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

// Local manifests only. No launcher credentials, remote account APIs or whole-disk scan.
public static class GameDiscoveryService
{
    public static string StableId(string source, string providerId, string path) {
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return source + ":" + providerId + ":" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
    }
    public static List<InstalledGame> Discover(IEnumerable<InstalledGame> manual = null)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
            foreach (string key in new[] { @"SOFTWARE\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" })
                using (var value = hive.OpenSubKey(key)) {
                    string path = (value?.GetValue("SteamPath") ?? value?.GetValue("InstallPath")) as string;
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) roots.Add(path);
                }
        }
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(standard)) roots.Add(standard);
        var result = new List<InstalledGame>();
        foreach (var root in roots) { try { result.AddRange(DiscoverSteam(root)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        result.AddRange(DiscoverEpic(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests")));
        try { result.AddRange(DiscoverGog()); } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { }
        result.AddRange(DiscoverRiot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json")));
        // Curated standalone installs, plus other launchers exposed through Windows app registration.
        foreach (var entry in ApplicationCatalog.Discover(new string[0])) {
            if (result.Any(g => g.Executables.Contains(entry.ExecutablePath, StringComparer.OrdinalIgnoreCase))) continue;
            if (GameCatalog.Games.Any(g => g.Processes.Contains(Path.GetFileName(entry.ExecutablePath), StringComparer.OrdinalIgnoreCase))) {
                var game = InstalledGame.FromExecutable(entry.ExecutablePath); game.Name = entry.Name; game.Source = "Windows"; result.Add(game);
            }
        }
        foreach (var item in (manual ?? Enumerable.Empty<InstalledGame>()).Where(g => g != null && g.Executables != null && g.Executables.Any(File.Exists))) {
            result.RemoveAll(g => g.Id == item.Id || (item.Id.StartsWith("manual:") && string.Equals(g.InstallDirectory, item.InstallDirectory, StringComparison.OrdinalIgnoreCase)));
            result.Add(item);
        }
        return result.GroupBy(g => g.InstallDirectory.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Executables.Count).First()).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<InstalledGame> DiscoverSteam(string steamRoot)
    {
        var result = new List<InstalledGame>();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steamRoot) };
        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile)) {
            var vdf = ParseVdf(ReadSmallFile(libraryFile));
            var folder = Child(vdf, "libraryfolders");
            foreach (var pair in folder) {
                string path = pair.Value is Dictionary<string, object> node ? Value(node, "path") : (int.TryParse(pair.Key, out _) ? pair.Value as string : null);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) libraries.Add(Path.GetFullPath(path));
            }
        }
        foreach (var library in libraries) {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(10000)) {
                try {
                    var app = Child(ParseVdf(ReadSmallFile(manifest)), "AppState");
                    string id = Value(app, "appid"), name = Value(app, "name"), directory = Value(app, "installdir");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory)) continue;
                    if (long.TryParse(Value(app, "StateFlags"), out long flags) && (flags & 4) == 0) continue;
                    // Ignore download-only manifests until an installation directory exists.
                    string root = Path.GetFullPath(Path.Combine(apps, "common"));
                    string install = Path.GetFullPath(Path.Combine(root, directory));
                    if (!IsInside(install, root) || !Directory.Exists(install)) continue;
                    var game = new InstalledGame { Id = StableId("steam", id, install), Name = name, Source = "Steam", InstallDirectory = install };
                    if (ulong.TryParse(id, out _)) game.LaunchUri = "steam://rungameid/" + id;
                    game.Executables = FindGameExecutables(game);
                    string cache = Path.Combine(steamRoot, "appcache", "librarycache");
                    game.ImagePath = new[] { id + "_library_600x900.jpg", id + "_library_600x900_2x.jpg", id + "_header.jpg", id + "_library_header.jpg", Path.Combine(id,"library_600x900.jpg"), Path.Combine(id,"library_600x900_2x.jpg"), Path.Combine(id,"header.jpg") }.Select(p => Path.Combine(cache, p)).FirstOrDefault(File.Exists);
                    result.Add(game);
                } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
            }
        }
        return result;
    }

    public static List<InstalledGame> DiscoverEpic(string manifestDirectory)
    {
        var result = new List<InstalledGame>();
        if (!Directory.Exists(manifestDirectory)) return result;
        foreach (var file in Directory.EnumerateFiles(manifestDirectory, "*.item").Take(10000)) {
            try {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadSmallFile(file));
                string install = Value(data, "InstallLocation"), launch = Value(data, "LaunchExecutable"), name = Value(data, "DisplayName");
                if (string.IsNullOrEmpty(install) || !Directory.Exists(install) || string.IsNullOrEmpty(name)) continue;
                if (data.TryGetValue("bIsIncompleteInstall", out object incomplete) && Equals(incomplete, true)) continue;
                install = Path.GetFullPath(install);
                var game = new InstalledGame { Id = StableId("epic", Value(data, "AppName"), install), Name = name, Source = "Epic Games", InstallDirectory = install };
                string appName = Value(data, "AppName");
                if (!string.IsNullOrWhiteSpace(appName)) game.LaunchUri = "com.epicgames.launcher://apps/" + Uri.EscapeDataString(appName) + "?action=launch&silent=true";
                game.Executables = FindGameExecutables(game);
                if (!string.IsNullOrEmpty(launch)) {
                    string executable = Path.GetFullPath(Path.Combine(install, launch.Replace('/', Path.DirectorySeparatorChar)));
                    if (IsInside(executable, install) && File.Exists(executable) && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) game.Executables.Insert(0, executable);
                }
                game.Executables = game.Executables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                result.Add(game);
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        return result;
    }

    private static List<InstalledGame> DiscoverGog()
    {
        var result = new List<InstalledGame>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
        using (var games = hive.OpenSubKey(@"SOFTWARE\GOG.com\Games")) {
            if (games == null) continue;
            foreach (var id in games.GetSubKeyNames()) using (var entry = games.OpenSubKey(id)) {
                string root = entry?.GetValue("path") as string, name = entry?.GetValue("gameName") as string;
                if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(name) || !Directory.Exists(root)) continue;
                var game = new InstalledGame { Id = StableId("gog", id, root), Name = name, Source = "GOG Galaxy", InstallDirectory = Path.GetFullPath(root) };
                game.Executables = FindGameExecutables(game); result.Add(game);
            }
        }
        return result;
    }
    public static List<InstalledGame> DiscoverRiot(string manifest)
    {
        var result = new List<InstalledGame>();
        if (!File.Exists(manifest)) return result;
        try {
            var data = new JavaScriptSerializer().DeserializeObject(ReadSmallFile(manifest)) as Dictionary<string, object>;
            if (data == null || !data.TryGetValue("associated_client", out object value) || !(value is Dictionary<string, object> installs)) return result;
            foreach (var entry in installs) {
                string executable = entry.Key.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileNameWithoutExtension(executable), root = Path.GetDirectoryName(executable);
                if (name.IndexOf("valorant", StringComparison.OrdinalIgnoreCase) >= 0) name = "VALORANT";
                else if (name.IndexOf("league", StringComparison.OrdinalIgnoreCase) >= 0) name = "League of Legends";
                var game = new InstalledGame { Id = StableId("riot", "", executable), Name = name, Source = "Riot Games", InstallDirectory = root };
                game.LauncherPath = entry.Value as string;
                game.LauncherProduct = name == "VALORANT" ? "valorant" : name == "League of Legends" ? "league_of_legends" : null;
                game.Executables = FindGameExecutables(game); if (game.Executables.Count == 0) game.Executables.Add(executable); result.Add(game);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        return result;
    }

    private static List<string> FindGameExecutables(InstalledGame game)
    {
        // Exclude redistributables, crash tools and anti-cheat installers rather than routing the launcher ecosystem.
        var files = new List<string>(); var pending = new Queue<Tuple<string, int>>(); pending.Enqueue(Tuple.Create(game.InstallDirectory, 0));
        int visited = 0;
        while (pending.Count > 0 && visited++ < 1000) {
            var node = pending.Dequeue();
            try {
                files.AddRange(Directory.EnumerateFiles(node.Item1, "*.exe").Where(p => !Regex.IsMatch(Path.GetFileName(p), "crash|unins|setup|install|redist|report|benchmark|anticheat|easyanticheat|battleye|launcher", RegexOptions.IgnoreCase)));
                if (node.Item2 < 5) foreach (var directory in Directory.EnumerateDirectories(node.Item1)) {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || Regex.IsMatch(Path.GetFileName(directory), "redist|installer|anticheat|battleye|easyanticheat|support|cache", RegexOptions.IgnoreCase)) continue;
                    pending.Enqueue(Tuple.Create(directory, node.Item2 + 1));
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var definition = GameCatalog.Games.FirstOrDefault(g => game.Name.IndexOf(g.Name, StringComparison.OrdinalIgnoreCase) >= 0);
        var known = files.Where(p => definition != null && definition.Processes.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).ToList();
        if (known.Count > 0) return known.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return files.OrderBy(p => p.Length).Take(8).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static bool IsInside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string ReadSmallFile(string file) {
        if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new IOException("Manifest too large");
        return File.ReadAllText(file);
    }
    private static Dictionary<string, object> Child(Dictionary<string, object> node, string key) => node.TryGetValue(key, out var value) && value is Dictionary<string, object> dict ? dict : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    private static string Value(Dictionary<string, object> node, string key) => node.TryGetValue(key, out var value) ? value as string : null;

    // Bounded KeyValues reader: quoted strings, braces, escaped slash/quote and // comments.
    public static Dictionary<string, object> ParseVdf(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new IOException("Manifest too large");
        var tokens = new List<string>();
        for (int i = 0; i < text.Length;) {
            char c = text[i++];
            if (char.IsWhiteSpace(c) || c == '\uFEFF') continue;
            if (c == '/' && i < text.Length && text[i] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '{' || c == '}') { tokens.Add(c.ToString()); continue; }
            if (c != '"') throw new IOException("Invalid VDF token");
            var token = new StringBuilder(); bool closed = false;
            while (i < text.Length) {
                c = text[i++]; if (c == '"') { closed = true; break; }
                if (c == '\\' && i < text.Length && (text[i] == '\\' || text[i] == '"')) c = text[i++];
                token.Append(c);
            }
            if (!closed) throw new IOException("Truncated VDF string"); tokens.Add(token.ToString());
        }
        int position = 0;
        Dictionary<string, object> Read(int depth, bool nested) {
            if (depth > 32) throw new IOException("VDF nesting limit");
            var node = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (position < tokens.Count) {
                string key = tokens[position++];
                if (key == "}") { if (nested) return node; throw new IOException("Unexpected VDF closing brace"); }
                if (key == "{" || position >= tokens.Count) throw new IOException("Invalid VDF pair");
                string value = tokens[position++];
                if (value == "}") throw new IOException("Missing VDF value");
                node[key] = value == "{" ? (object)Read(depth + 1, true) : value;
            }
            if (nested) throw new IOException("Truncated VDF object"); return node;
        }
        return Read(0, false);
    }
}
