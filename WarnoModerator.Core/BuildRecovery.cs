using System.Text.Json;

namespace WarnoModerator.Core;

public sealed record RecoveryRecord(string Id, string OutputName, bool SourceExisted, bool RuntimeExisted, bool Committed = false);

public sealed class BuildRecovery
{
    private const string Prefix = ".warno-moderator-recovery-";
    public static FileStream AcquireLock(WarnoPaths paths, string name)
    {
        MergePlanner.ValidateOutputName(name);
        try
        {
            return new FileStream(Path.Combine(paths.ModsRoot, $".warno-moderator-operation-{name}.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex) { throw new CombineException($"Another operation is using '{name}'. Close it or wait for it to finish.", ex); }
    }

    public IReadOnlyList<RecoveryRecord> Find(WarnoPaths paths)
    {
        var records = new List<RecoveryRecord>();
        if (!Directory.Exists(paths.ModsRoot)) return records;
        foreach (var file in Directory.EnumerateFiles(paths.ModsRoot, Prefix + "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(file));
                if (record is null || !Guid.TryParseExact(record.Id, "N", out _) || string.IsNullOrWhiteSpace(record.OutputName)) continue;
                MergePlanner.ValidateOutputName(record.OutputName);
                if (Path.GetFileName(file) == Prefix + record.Id + ".json") records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or CombineException) { }
        }
        return records;
    }

    internal RecoveryRecord Begin(WarnoPaths paths, string name)
    {
        if (Find(paths).Any(x => x.OutputName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new CombineException($"An interrupted rebuild of '{name}' requires recovery. Refresh mods to recover it first.");
        var record = new RecoveryRecord(Guid.NewGuid().ToString("N"), name,
            Directory.Exists(Path.Combine(paths.ModsRoot, name)), Directory.Exists(Path.Combine(paths.SavedModsRoot, name)));
        Save(paths, record);
        return record;
    }

    internal static string Backup(string root, RecoveryRecord record) =>
        FileSystemOps.SafeCombine(root, record.OutputName + ".warno-moderator-backup-" + record.Id);

    internal void MoveOriginals(WarnoPaths paths, RecoveryRecord record)
    {
        if (record.SourceExisted) Directory.Move(Path.Combine(paths.ModsRoot, record.OutputName), Backup(paths.ModsRoot, record));
        if (record.RuntimeExisted) Directory.Move(Path.Combine(paths.SavedModsRoot, record.OutputName), Backup(paths.SavedModsRoot, record));
    }

    public void Recover(WarnoPaths paths, RecoveryRecord record, Action<string> log)
    {
        ValidateRecord(record);
        using var operationLock = AcquireLock(paths, record.OutputName);
        if (record.Committed) Cleanup(paths, record, log);
        else Restore(paths, record, log);
    }

    internal void Restore(WarnoPaths paths, RecoveryRecord record, Action<string> log)
    {
        var failures = new List<Exception>();
        RestoreOne(paths.ModsRoot, record.SourceExisted);
        RestoreOne(paths.SavedModsRoot, record.RuntimeExisted);
        if (failures.Count > 0)
            throw new CombineException("Automatic recovery is incomplete. Close WARNO and programs using these files, then refresh mods to retry. "
                + string.Join(" ", failures.Select(x => x.Message)), new AggregateException(failures));
        File.Delete(Journal(paths, record));
        log("Previous outputs restored.");

        void RestoreOne(string root, bool existed)
        {
            var output = Path.Combine(root, record.OutputName);
            var backup = Backup(root, record);
            try
            {
                // A missing backup means this side was never moved or was already restored.
                if (existed && !Directory.Exists(backup)) return;
                if (Directory.Exists(output))
                {
                    var incomplete = output + ".warno-moderator-incomplete-" + Guid.NewGuid().ToString("N");
                    Directory.Move(output, incomplete);
                    log($"Incomplete output preserved at: {incomplete}");
                }
                if (Directory.Exists(backup)) Directory.Move(backup, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = $"Restore '{output}' from '{backup}': {ex.Message}";
                log(message);
                failures.Add(new IOException(message, ex));
            }
        }
    }

    internal void Complete(WarnoPaths paths, RecoveryRecord record, Action<string> log)
    {
        // Persist the commit before cleanup; a restart must never restore a partially deleted backup.
        var committed = record with { Committed = true };
        Save(paths, committed);
        Cleanup(paths, committed, log);
    }

    private static void Cleanup(WarnoPaths paths, RecoveryRecord record, Action<string> log)
    {
        var pending = false;
        foreach (var root in new[] { paths.ModsRoot, paths.SavedModsRoot })
        {
            var backup = Backup(root, record);
            try { if (Directory.Exists(backup)) Directory.Delete(backup, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { pending = true; log($"Build succeeded. Backup cleanup pending at '{backup}': {ex.Message}"); }
        }
        if (!pending)
        {
            try { File.Delete(Journal(paths, record)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log($"Build succeeded. Recovery record cleanup pending: {ex.Message}"); }
        }
    }

    private static void ValidateRecord(RecoveryRecord record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _)) throw new CombineException("Invalid recovery record.");
        MergePlanner.ValidateOutputName(record.OutputName);
    }

    private static string Journal(WarnoPaths paths, RecoveryRecord record) => Path.Combine(paths.ModsRoot, Prefix + record.Id + ".json");
    private static void Save(WarnoPaths paths, RecoveryRecord record)
    {
        ValidateRecord(record);
        var file = Journal(paths, record);
        var temporary = file + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
