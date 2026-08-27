using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace WarnoModerator.Core;

public sealed class WarnoLocator
{
    private static readonly string[] RegistryKeys =
    [
        @"HKEY_CURRENT_USER\Software\Valve\Steam",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
    ];

    public WarnoPaths? Locate()
    {
        foreach (var steamRoot in GetSteamRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var library in GetLibraries(steamRoot))
            {
                var warnoRoot = Path.Combine(library, "steamapps", "common", "WARNO");
                if (!Directory.Exists(warnoRoot))
                {
                    continue;
                }

                var savedMods = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Saved Games", "EugenSystems", "WARNO", "mod");

                return new WarnoPaths(
                    steamRoot,
                    warnoRoot,
                    Path.Combine(warnoRoot, "Mods"),
                    Path.Combine(library, "steamapps", "workshop", "content", "1611600"),
                    savedMods);
            }
        }

        return null;
    }

    public WarnoPaths FromWarnoRoot(string warnoRoot)
    {
        var fullRoot = Path.GetFullPath(warnoRoot);
        if (!Directory.Exists(fullRoot)
            || !File.Exists(Path.Combine(fullRoot, "WARNO.exe"))
            || !File.Exists(Path.Combine(fullRoot, "Mods", "CreateNewMod.bat")))
        {
            throw new CombineException("The selected folder is not a valid WARNO installation.");
        }

        var common = Directory.GetParent(fullRoot)?.Parent?.Parent;
        var library = common?.FullName ?? fullRoot;
        var steamRoot = Directory.GetParent(library)?.FullName ?? library;
        var savedMods = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "EugenSystems", "WARNO", "mod");

        return new WarnoPaths(
            steamRoot,
            fullRoot,
            Path.Combine(fullRoot, "Mods"),
            Path.Combine(library, "steamapps", "workshop", "content", "1611600"),
            savedMods);
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in RegistryKeys)
            {
                foreach (var valueName in new[] { "SteamPath", "InstallPath" })
                {
                    if (Registry.GetValue(key, valueName, null) is string path && Directory.Exists(path))
                    {
                        yield return Path.GetFullPath(path);
                    }
                }
            }
        }

        foreach (var fallback in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam"
                 })
        {
            if (Directory.Exists(fallback))
            {
                yield return fallback;
            }
        }
    }

    private static IEnumerable<string> GetLibraries(string steamRoot)
    {
        yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }

        var text = File.ReadAllText(vdf);
        foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
        {
            var path = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }
}
