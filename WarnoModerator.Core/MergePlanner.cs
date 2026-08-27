namespace WarnoModerator.Core;

public sealed class MergePlanner(SourceDeltaAnalyzer deltaAnalyzer)
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public MergePreview CreatePreview(
        WarnoPaths paths,
        ModDescriptor other,
        ModDescriptor ulti,
        string outputName)
    {
        var warnings = new List<string>();
        ValidateOutputName(outputName);

        if (ulti.Kind != ModKind.EditableSource)
        {
            throw new CombineException("UltiAI must be available as an editable source mod.");
        }

        if (other.RootPath.Equals(ulti.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new CombineException("Select a different mod to combine with UltiAI.");
        }

        var outputPath = Path.Combine(paths.ModsRoot, outputName);
        var savedOutputPath = Path.Combine(paths.SavedModsRoot, outputName);
        if (Directory.Exists(outputPath) || Directory.Exists(savedOutputPath))
        {
            throw new CombineException($"An output named '{outputName}' already exists.");
        }

        var decisions = other.Kind == ModKind.EditableSource
            ? PlanSourceMerge(paths, other, ulti, warnings)
            : PlanWorkshopMerge(other, ulti, warnings);

        return new MergePreview(outputName, other, ulti, decisions, warnings, true);
    }

    public static void ValidateOutputName(string outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName))
        {
            throw new CombineException("Enter an output name.");
        }

        if (!string.Equals(outputName, outputName.Trim(), StringComparison.Ordinal)
            || outputName is "." or ".."
            || outputName.EndsWith('.')
            || outputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || outputName.Contains('%')
            || outputName.Length > 120
            || ReservedWindowsNames.Contains(outputName.Split('.')[0]))
        {
            throw new CombineException("The output name is not a valid Windows folder name.");
        }
    }

    private IReadOnlyList<MergeDecision> PlanSourceMerge(
        WarnoPaths paths,
        ModDescriptor other,
        ModDescriptor ulti,
        ICollection<string> warnings)
    {
        var currentBaseHash = SourceDeltaAnalyzer.ComputeSha256(paths.ModDataBaseZip);
        foreach (var mod in new[] { other, ulti })
        {
            var modHash = SourceDeltaAnalyzer.ComputeSha256(mod.BaseZipPath);
            if (!modHash.Equals(currentBaseHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new CombineException(
                    $"{mod.Name} is based on an older WARNO version. Run its UpdateMod.bat first.");
            }
        }

        var otherDelta = deltaAnalyzer.Analyze(other).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var ultiDelta = deltaAnalyzer.Analyze(ulti).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var allPaths = otherDelta.Keys.Union(ultiDelta.Keys, StringComparer.OrdinalIgnoreCase);
        var decisions = new List<MergeDecision>();

        foreach (var path in allPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var hasOther = otherDelta.TryGetValue(path, out var otherChange);
            var hasUlti = ultiDelta.TryGetValue(path, out var ultiChange);

            if (hasUlti && hasOther)
            {
                decisions.Add(new MergeDecision(
                    path,
                    ultiChange!.Kind == DeltaKind.Deleted ? MergeDecisionKind.Delete : MergeDecisionKind.UltiOverride,
                    ulti.Name,
                    "Both source mods changed this path; the complete Ulti file wins."));
            }
            else if (hasUlti)
            {
                decisions.Add(new MergeDecision(
                    path,
                    ultiChange!.Kind == DeltaKind.Deleted ? MergeDecisionKind.Delete : MergeDecisionKind.UltiOnly,
                    ulti.Name,
                    $"Ulti {ultiChange.Kind.ToString().ToLowerInvariant()} source file."));
            }
            else
            {
                decisions.Add(new MergeDecision(
                    path,
                    otherChange!.Kind == DeltaKind.Deleted ? MergeDecisionKind.Delete : MergeDecisionKind.OtherOnly,
                    other.Name,
                    $"Other mod {otherChange.Kind.ToString().ToLowerInvariant()} source file."));
            }
        }

        return decisions;
    }

    private static IReadOnlyList<MergeDecision> PlanWorkshopMerge(
        ModDescriptor other,
        ModDescriptor ulti,
        ICollection<string> warnings)
    {
        ValidateUltiGeneration(ulti);

        if (!Directory.Exists(other.GenPath)
            && !HasRuntimeContent(other.RootPath))
        {
            throw new CombineException($"{other.Name} has no compiled Gen data, maps, scenarios, or runtime assets.");
        }

        if (other.ModGenVersion is not null
            && ulti.ModGenVersion is not null
            && other.ModGenVersion != ulti.ModGenVersion)
        {
            throw new CombineException(
                $"{other.Name} uses ModGen {other.ModGenVersion}, but {ulti.Name} uses {ulti.ModGenVersion}. " +
                "The Workshop mod must be updated by its author before it can be combined safely.");
        }

        var otherFiles = EnumerateRuntimeFiles(other.RootPath)
            .ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var ultiFiles = EnumerateUltiOverlayFiles(ulti.GenPath)
            .ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var allPaths = otherFiles.Keys.Union(ultiFiles.Keys, StringComparer.OrdinalIgnoreCase);
        var decisions = new List<MergeDecision>();

        foreach (var path in allPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var hasOther = otherFiles.ContainsKey(path);
            var hasUlti = ultiFiles.ContainsKey(path);
            if (hasOther && hasUlti)
            {
                decisions.Add(new MergeDecision(
                    path,
                    MergeDecisionKind.UltiOverride,
                    ulti.Name,
                    path.Contains("\\NDF\\", StringComparison.OrdinalIgnoreCase)
                        ? "Compiled database collision; the complete Ulti database wins."
                        : "Generated-file collision; Ulti wins."));
            }
            else
            {
                decisions.Add(new MergeDecision(
                    path,
                    hasUlti ? MergeDecisionKind.UltiOnly : MergeDecisionKind.OtherOnly,
                    hasUlti ? ulti.Name : other.Name,
                    hasUlti ? "Ulti compiled/runtime component." : "Workshop compiled/runtime component."));
            }
        }

        var databaseCollisions = decisions
            .Where(x => x.Kind == MergeDecisionKind.UltiOverride
                        && x.RelativePath.Contains("\\NDF\\", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.RelativePath)
            .ToArray();
        if (databaseCollisions.Length > 0)
        {
            warnings.Add(
                "Workshop binaries cannot be split back into individual NDF files. " +
                $"Ulti will replace {databaseCollisions.Length} complete compiled database(s): " +
                string.Join(", ", databaseCollisions.Select(Path.GetFileName)));
        }

        if (File.Exists(Path.Combine(other.GenPath, "ResourceFile", "Catalog.cat")))
        {
            warnings.Add(
                "The Workshop resource catalog will be retained so its custom assets remain registered. " +
                "Catalog.cat is a compiled binary and cannot be safely merged; Ulti catalog-only cosmetic assets may be unavailable.");
        }

        return decisions;
    }

    private static void ValidateUltiGeneration(ModDescriptor ulti)
    {
        var report = Path.Combine(ulti.GenPath, "GenerationReport.txt");
        var generatedFiles = EnumerateUltiOverlayFiles(ulti.GenPath).ToArray();
        if (!File.Exists(report) || generatedFiles.Length == 0)
        {
            throw new CombineException(
                $"{ulti.Name} has not been generated. Run its GenerateMod.bat before combining it with a Workshop mod.");
        }

        var newestChangedSource = new SourceDeltaAnalyzer().Analyze(ulti)
            .Where(x => x.SourcePath is not null)
            .Select(x => File.GetLastWriteTimeUtc(x.SourcePath!))
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        if (File.GetLastWriteTimeUtc(report).AddSeconds(2) < newestChangedSource)
        {
            throw new CombineException(
                $"{ulti.Name}'s generated files are older than its edited source. Run its GenerateMod.bat, then refresh.");
        }
    }

    internal static IEnumerable<(string RelativePath, string FullPath)> EnumerateRuntimeFiles(string root)
    {
        foreach (var child in new[] { "Gen", "GameData", "DatasMap", "DecorsSets", "Maps", "Scenarios" })
        {
            var directory = Path.Combine(root, child);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".tag", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return (Path.GetRelativePath(root, file).Replace('/', '\\'), file);
            }
        }
    }

    internal static IEnumerable<(string RelativePath, string FullPath)> EnumerateUltiOverlayFiles(string genRoot)
    {
        if (!Directory.Exists(genRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(genRoot, "*", SearchOption.AllDirectories))
        {
            var relativeToGen = Path.GetRelativePath(genRoot, file).Replace('/', '\\');
            if (relativeToGen.EndsWith(".tag", StringComparison.OrdinalIgnoreCase)
                || relativeToGen.Equals("ResourceFile\\Catalog.cat", StringComparison.OrdinalIgnoreCase)
                || relativeToGen.StartsWith("Intermediate\\", StringComparison.OrdinalIgnoreCase)
                || relativeToGen.Equals("DeclaredFiles.txt", StringComparison.OrdinalIgnoreCase)
                || relativeToGen.Equals("UsedFiles.txt", StringComparison.OrdinalIgnoreCase)
                || relativeToGen.Equals("GenerationReport.txt", StringComparison.OrdinalIgnoreCase)
                || relativeToGen.Equals("DecorSetAssets.ndf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isNdf = relativeToGen.StartsWith("NDF\\", StringComparison.OrdinalIgnoreCase);
            var isNamedSupport = relativeToGen.Contains("UltiAI", StringComparison.OrdinalIgnoreCase);
            if (isNdf || isNamedSupport || relativeToGen.Equals("Version.ndf", StringComparison.OrdinalIgnoreCase))
            {
                yield return ($"Gen\\{relativeToGen}", file);
            }
        }
    }

    private static bool HasRuntimeContent(string root) =>
        new[] { "GameData", "DatasMap", "DecorsSets", "Maps", "Scenarios" }
            .Any(x => Directory.Exists(Path.Combine(root, x)));
}
