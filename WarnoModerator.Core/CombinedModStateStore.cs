using System.Text.Json;

namespace WarnoModerator.Core;

public sealed class CombinedModStateStore
{
    public const string FileName = ".warno-moderator.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IReadOnlyList<CombinedModState> FindAllForSources(WarnoPaths paths, ModDescriptor other,
        ModDescriptor priority, CancellationToken token = default)
    {
        if (!Directory.Exists(paths.ModsRoot)) return [];
        var results = new List<CombinedModState>();
        foreach (var directory in Directory.EnumerateDirectories(paths.ModsRoot))
        {
            token.ThrowIfCancellationRequested();
            var state = TryLoad(directory);
            if (state is not null && SamePath(state.OtherMod.RootPath, other.RootPath)
                && SamePath(state.PriorityMod.RootPath, priority.RootPath)) results.Add(state);
        }
        return results.OrderBy(state => state.OutputName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string SuggestNewOutputName(WarnoPaths paths, string baseName)
    {
        if (baseName.Length > 105) baseName = baseName[..105].TrimEnd(' ', '.');
        try { MergePlanner.ValidateOutputName(baseName); }
        catch (CombineException) { baseName = "Combined mod"; }
        var candidate = baseName;
        for (var number = 2; OutputExists(paths, candidate); number++) candidate = $"{baseName} ({number})";
        return candidate;
    }

    public static bool OutputExists(WarnoPaths paths, string outputName) =>
        new[] { paths.ModsRoot, paths.SavedModsRoot }.Any(root =>
            Directory.Exists(Path.Combine(root, outputName)) || File.Exists(Path.Combine(root, outputName)));
    public CombinedModState? TryLoad(string outputDirectory)
    {
        var statePath = Path.Combine(outputDirectory, FileName);
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<CombinedModState>(File.ReadAllText(statePath), JsonOptions);
            return state is not null
                && state.SchemaVersion == CombinedModState.CurrentSchemaVersion
                && ValidFingerprint(state.OtherMod)
                && ValidFingerprint(state.PriorityMod)
                && ValidOutputName(state.OutputName)
                && state.OutputName.Equals(Path.GetFileName(outputDirectory), StringComparison.OrdinalIgnoreCase)
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string outputDirectory, CombinedModState state)
    {
        if (!Directory.Exists(outputDirectory))
        {
            throw new CombineException("The combined mod output is missing; its update state could not be saved.");
        }

        var statePath = Path.Combine(outputDirectory, FileName);
        var temporaryPath = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, statePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static bool FingerprintMatches(SourceModFingerprint stored, SourceModFingerprint current) =>
        SamePath(stored.RootPath, current.RootPath)
        && stored.Fingerprint.Equals(current.Fingerprint, StringComparison.OrdinalIgnoreCase);

    private static bool ValidOutputName(string? name)
    {
        if (name is null) return false;
        try { MergePlanner.ValidateOutputName(name); return true; }
        catch (CombineException) { return false; }
    }

    private static bool ValidFingerprint(SourceModFingerprint? fingerprint)
    {
        if (fingerprint is null || string.IsNullOrWhiteSpace(fingerprint.Name)
            || string.IsNullOrWhiteSpace(fingerprint.RootPath) || fingerprint.Fingerprint is null) return false;
        if (fingerprint.Fingerprint.Length != 64 || !fingerprint.Fingerprint.All(Uri.IsHexDigit)) return false;
        try { return Path.IsPathFullyQualified(fingerprint.RootPath) && Path.GetFullPath(fingerprint.RootPath).Length > 0; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
}
