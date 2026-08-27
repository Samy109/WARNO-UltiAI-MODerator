namespace WarnoModerator.Core;

public sealed class ModScanner
{
    private static readonly HashSet<string> ReservedModDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModData", "Utils", "ExampleAssets"
    };

    public IReadOnlyList<ModDescriptor> Scan(WarnoPaths paths)
    {
        var mods = new List<ModDescriptor>();
        ScanEditable(paths, mods);
        ScanWorkshop(paths, mods);
        return mods.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ScanEditable(WarnoPaths paths, ICollection<ModDescriptor> mods)
    {
        if (!Directory.Exists(paths.ModsRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(paths.ModsRoot))
        {
            var name = Path.GetFileName(directory);
            if (ReservedModDirectories.Contains(name)
                || !File.Exists(Path.Combine(directory, "base.zip"))
                || !Directory.Exists(Path.Combine(directory, "GameData"))
                || !Directory.Exists(Path.Combine(directory, "CommonData")))
            {
                continue;
            }

            var config = LoadBestConfig(paths, name, directory);
            mods.Add(CreateDescriptor(name, directory, ModKind.EditableSource, null, config));
        }
    }

    private static void ScanWorkshop(WarnoPaths paths, ICollection<ModDescriptor> mods)
    {
        if (!Directory.Exists(paths.WorkshopRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(paths.WorkshopRoot))
        {
            var configPath = Path.Combine(directory, "Config.ini");
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                var config = IniDocument.Load(configPath);
                var name = config.Get("Properties", "Name") ?? Path.GetFileName(directory);
                mods.Add(CreateDescriptor(
                    name,
                    directory,
                    ModKind.WorkshopCompiled,
                    Path.GetFileName(directory),
                    config));
            }
            catch (IOException)
            {
                // Ignore a Workshop item that Steam is currently replacing.
            }
        }
    }

    private static IniDocument? LoadBestConfig(WarnoPaths paths, string name, string root)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(paths.SavedModsRoot, name, "Config.ini"),
                     Path.Combine(root, "Config.ini")
                 })
        {
            if (File.Exists(path))
            {
                return IniDocument.Load(path);
            }
        }

        return null;
    }

    private static ModDescriptor CreateDescriptor(
        string name,
        string root,
        ModKind kind,
        string? workshopId,
        IniDocument? config)
    {
        var tags = (config?.Get("Properties", "TagList") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modGen = config?.GetInt("Properties", "ModGenVersion", -1);

        return new ModDescriptor(
            name,
            root,
            kind,
            workshopId,
            modGen >= 0 ? modGen : ReadGenVersion(root),
            config?.GetInt("Properties", "Version") ?? 0,
            config?.GetInt("Properties", "DeckFormatVersion") ?? 0,
            tags,
            config?.GetSection("Config") ?? new Dictionary<string, string>());
    }

    private static int? ReadGenVersion(string root)
    {
        var file = Path.Combine(root, "Gen", "Version.ndf");
        if (!File.Exists(file))
        {
            return null;
        }

        var text = File.ReadAllText(file);
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var version) ? version : null;
    }
}
