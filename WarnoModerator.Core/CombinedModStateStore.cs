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

    public CombinedModState? FindForSources(
        WarnoPaths paths,
        ModDescriptor other,
        ModDescriptor priority)
    {
        if (!Directory.Exists(paths.ModsRoot))
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories(paths.ModsRoot))
        {
            var state = TryLoad(directory);
            if (state is not null
                && SamePath(state.OtherMod.RootPath, other.RootPath)
                && SamePath(state.PriorityMod.RootPath, priority.RootPath))
            {
                return state;
            }
        }

        return null;
    }

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

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
}
