using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace NovaVpn;

public static class GameLaunchService
{
    public static ProcessStartInfo Build(InstalledGame game)
    {
        string launch = null, arguments = "";
        if (game.Source == "Steam" && Uri.TryCreate(game.LaunchUri, UriKind.Absolute, out var steam) && steam.Scheme == "steam" && steam.Host == "rungameid" &&
            ulong.TryParse(steam.AbsolutePath.Trim('/'), out _)) launch = game.LaunchUri;
        else if (game.Source == "Epic Games" && Uri.TryCreate(game.LaunchUri, UriKind.Absolute, out var epic) && epic.Scheme == "com.epicgames.launcher" && epic.Host == "apps") launch = game.LaunchUri;
        else if (game.Source == "Riot Games") {
            if (!File.Exists(game.LauncherPath) || !Path.GetFileName(game.LauncherPath).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase) ||
                (game.LauncherProduct != "valorant" && game.LauncherProduct != "league_of_legends"))
                throw new IOException("Не найден Riot Client. Запустите игру через установленный лаунчер: NOVA продолжит подбор автоматически.");
            launch = game.LauncherPath; arguments = "--launch-product=" + game.LauncherProduct + " --launch-patchline=live";
        } else if (game.Source != "Steam" && game.Source != "Epic Games") launch = game.Executables.FirstOrDefault(File.Exists);
        if (launch == null) throw new IOException("Не найден способ запуска игры. Обновите библиотеку.");
        return new ProcessStartInfo(launch, arguments) { UseShellExecute = true, WorkingDirectory = game.InstallDirectory };
    }
    public static void Start(InstalledGame game) { using (var process = Process.Start(Build(game))) { } }
}
