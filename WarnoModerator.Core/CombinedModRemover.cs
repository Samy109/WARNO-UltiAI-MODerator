namespace WarnoModerator.Core;

/// <summary>What deleting a combined mod removes: its two output folders and the leftovers of earlier builds.</summary>
public sealed record CombinedModFiles(string OutputName, IReadOnlyList<string> Folders);

/// <summary>
/// Deletes a combined mod everywhere it lives: Mods\&lt;Name&gt;, Saved Games\...\mod\&lt;Name&gt;, and the
/// "&lt;Name&gt;.warno-moderator-incomplete-*" and "-backup-*" folders interrupted builds leave next to them. Only
/// outputs this app created (they carry its tracking record) can be deleted, so an input mod never is.
/// </summary>
public sealed class CombinedModRemover
{
    private static readonly string[] LeftoverMarkers = [".warno-moderator-incomplete-", ".warno-moderator-backup-"];

    public CombinedModFiles Find(WarnoPaths paths, string outputName)
    {
        MergePlanner.ValidateOutputName(outputName);
        var store = new CombinedModStateStore();
        var source = Path.Combine(paths.ModsRoot, outputName);
        var runtime = Path.Combine(paths.SavedModsRoot, outputName);
        if (Directory.Exists(source) && store.TryLoad(source) is null)
            throw new CombineException($"{source} wasn't created by the MODerator, so it won't be deleted.");
        if (new BuildRecovery().Find(paths).Any(r => r.OutputName.Equals(outputName, StringComparison.OrdinalIgnoreCase)))
            throw new CombineException($"An interrupted build of '{outputName}' still needs recovery. Refresh mods to recover it first.");
        var folders = new List<string>();
        foreach (var root in new[] { paths.ModsRoot, paths.SavedModsRoot })
        {
            if (!Directory.Exists(root)) continue;
            var output = Path.Combine(root, outputName);
            if (Directory.Exists(output)) folders.Add(output);
            folders.AddRange(Directory.EnumerateDirectories(root, outputName + ".warno-moderator-*")
                .Where(dir => LeftoverMarkers.Any(marker => Path.GetFileName(dir).StartsWith(outputName + marker, StringComparison.OrdinalIgnoreCase))));
        }
        if (folders.Count == 0) throw new CombineException($"Nothing named '{outputName}' was found to delete.");
        return new CombinedModFiles(outputName, folders);
    }

    /// <summary>Leftovers of interrupted builds of any combined mod, except those a pending recovery still needs.</summary>
    public IReadOnlyList<CombinedModFiles> FindLeftovers(WarnoPaths paths)
    {
        var pending = new BuildRecovery().Find(paths).Select(r => r.OutputName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Name, string Folder)>();
        foreach (var root in new[] { paths.ModsRoot, paths.SavedModsRoot })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root, "*.warno-moderator-*"))
            {
                var name = Path.GetFileName(dir);
                var marker = LeftoverMarkers.Select(m => name.IndexOf(m, StringComparison.OrdinalIgnoreCase)).Where(i => i > 0).DefaultIfEmpty(-1).Max();
                if (marker > 0 && !pending.Contains(name[..marker])) found.Add((name[..marker], dir));
            }
        }
        return found.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CombinedModFiles(g.Key, g.Select(x => x.Folder).ToArray())).ToArray();
    }

    public void Delete(WarnoPaths paths, CombinedModFiles files, Action<string> log)
    {
        using var operationLock = BuildRecovery.AcquireLock(paths, files.OutputName);
        foreach (var folder in files.Folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, true);
            log($"Deleted {folder}");
        }
    }
}
