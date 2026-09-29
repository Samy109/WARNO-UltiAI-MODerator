namespace WarnoModerator.Core;

public sealed class MergePlanner(SourceDeltaAnalyzer deltaAnalyzer)
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public MergePreview CreatePreview(WarnoPaths paths, ModDescriptor other, ModDescriptor ulti,
        string outputName, bool allowExistingOutput = false, CancellationToken cancellationToken = default)
    {
        ValidateOutputName(outputName);
        ValidateInputs(paths, other, ulti, cancellationToken);
        foreach (var root in new[] { paths.ModsRoot, paths.SavedModsRoot })
        {
            var output = Path.Combine(root, outputName);
            if (SamePath(output, other.RootPath) || SamePath(output, ulti.RootPath))
                throw new CombineException("The output must not replace an input mod.");
            if (!allowExistingOutput && (Directory.Exists(output) || File.Exists(output)))
                throw new CombineException($"An output named '{outputName}' already exists.");
        }
        if (other.Kind == ModKind.EditableSource && ulti.Kind == ModKind.EditableSource)
            return new MergePreview(outputName, other, ulti, PlanSourceMerge(other, ulti, cancellationToken), []);

        var provisional = other.Kind == ModKind.EditableSource || ulti.Kind == ModKind.EditableSource;
        var otherRoot = other.Kind == ModKind.WorkshopCompiled ? other.RootPath : Path.Combine(paths.SavedModsRoot, other.Name);
        return CreateCompiledPlan(outputName, other, ulti, otherRoot, ulti.RootPath, cancellationToken, provisional);
    }

    internal static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static void ValidateInputs(WarnoPaths paths, ModDescriptor other, ModDescriptor ulti, CancellationToken token)
    {
        if (SamePath(other.RootPath, ulti.RootPath)) throw new CombineException("Select two different input mods.");
        string? currentBase = null;
        foreach (var mod in new[] { other, ulti })
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(mod.RootPath)) throw new CombineException($"Source mod folder is missing: {mod.RootPath}");
            if (mod.Kind == ModKind.WorkshopCompiled)
            {
                if (mod.ModGenVersion is null or < 0)
                    throw new CombineException($"{mod.Name} has no readable ModGenVersion. Refresh its Workshop subscription before combining it.");
            }
            else
            {
                currentBase ??= SourceDeltaAnalyzer.ComputeSha256(paths.ModDataBaseZip, token);
                if (SourceDeltaAnalyzer.ComputeSha256(mod.BaseZipPath, token) != currentBase)
                    throw new CombineException($"{mod.Name} is based on an older WARNO version. Run its UpdateMod.bat first.");
            }
        }
        if (other.Kind == ModKind.WorkshopCompiled && ulti.Kind == ModKind.WorkshopCompiled
            && other.ModGenVersion != ulti.ModGenVersion)
            throw new CombineException($"{other.Name} uses ModGen {other.ModGenVersion}, but {ulti.Name} uses {ulti.ModGenVersion}.");
    }

    internal IReadOnlyList<MergeDecision> PlanSourceMerge(ModDescriptor other, ModDescriptor ulti, CancellationToken token)
    {
        var otherDelta = other.Kind == ModKind.EditableSource
            ? deltaAnalyzer.Analyze(other, token).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SourceDelta>(StringComparer.OrdinalIgnoreCase);
        var ultiDelta = ulti.Kind == ModKind.EditableSource
            ? deltaAnalyzer.Analyze(ulti, token).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SourceDelta>(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<MergeDecision>();
        foreach (var path in otherDelta.Keys.Union(ultiDelta.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var hasOther = otherDelta.TryGetValue(path, out var otherChange);
            var hasUlti = ultiDelta.TryGetValue(path, out var ultiChange);
            var winner = hasUlti ? ultiChange! : otherChange!;
            var identical = hasOther && hasUlti && otherChange!.Kind == ultiChange!.Kind
                && (winner.Kind == DeltaKind.Deleted || SameContent(otherChange.SourcePath!, ultiChange.SourcePath!, token));
            decisions.Add(new MergeDecision(path,
                winner.Kind == DeltaKind.Deleted ? MergeDecisionKind.Delete :
                    hasUlti ? hasOther ? MergeDecisionKind.UltiOverride : MergeDecisionKind.UltiOnly : MergeDecisionKind.OtherOnly,
                hasUlti ? ulti.Name : other.Name,
                identical ? "Both inputs make the same change." : hasUlti && hasOther
                    ? "Both source mods changed this path; the complete priority file wins."
                    : $"{winner.Kind} source file.", winner.SourcePath, identical, hasOther && hasUlti));
        }
        return decisions;
    }

    internal static bool IsUiComponents(string path) =>
        path.Replace('/', '\\').Equals("Gen\\NDF\\UI\\Components.ndfbin", StringComparison.OrdinalIgnoreCase);

    internal static MergePreview CreateCompiledPlan(string outputName, ModDescriptor other, ModDescriptor ulti,
        string otherRoot, string ultiRoot, CancellationToken token, bool provisional = false)
    {
        var otherFiles = EnumerateRuntimeFiles(otherRoot).ToDictionary(x => x.RelativePath, x => x.FullPath, StringComparer.OrdinalIgnoreCase);
        var ultiFiles = EnumerateUltiOverlayFiles(Path.Combine(ultiRoot, "Gen")).ToDictionary(x => x.RelativePath, x => x.FullPath, StringComparer.OrdinalIgnoreCase);
        if (otherFiles.Count == 0 && (!provisional || other.Kind == ModKind.WorkshopCompiled))
            throw new CombineException($"{other.Name} has no runtime payload.");
        if (!ultiFiles.Keys.Any(x => x.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase))
            && (!provisional || ulti.Kind == ModKind.WorkshopCompiled))
            throw new CombineException($"{ulti.Name} has no compiled priority databases.");
        const string catalog = "Gen\\ResourceFile\\Catalog.cat";
        var priorityCatalog = Path.Combine(ultiRoot, catalog);
        if (!otherFiles.ContainsKey(catalog) && File.Exists(priorityCatalog)) ultiFiles[catalog] = priorityCatalog;
        var decisions = new List<MergeDecision>();
        foreach (var path in otherFiles.Keys.Union(ultiFiles.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var hasOther = otherFiles.TryGetValue(path, out var otherFile);
            var hasUlti = ultiFiles.TryGetValue(path, out var ultiFile);
            var keepOther = hasOther && (!hasUlti || IsUiComponents(path));
            var identical = hasOther && hasUlti && SameContent(otherFile!, ultiFile!, token);
            var kind = keepOther ? hasUlti ? MergeDecisionKind.OtherOverride : MergeDecisionKind.OtherOnly
                : hasOther ? MergeDecisionKind.UltiOverride : MergeDecisionKind.UltiOnly;
            decisions.Add(new MergeDecision(path, kind, keepOther ? other.Name : ulti.Name,
                identical ? "Identical payload in both inputs." : kind == MergeDecisionKind.OtherOverride
                    ? "Other mod UI retained to preserve its interface and texture registrations."
                    : kind == MergeDecisionKind.UltiOverride ? "Complete priority payload replaces the other mod's file."
                    : "Only this input supplies the selected component.", keepOther ? otherFile : ultiFile, identical));
        }
        var warnings = new List<string>();
        if (provisional) warnings.Add("Provisional preview: editable input will be regenerated. The final report will show the exact generated payload and conflicts.");
        var collisions = decisions.Count(x => x.Kind == MergeDecisionKind.UltiOverride && !x.Identical && x.RelativePath.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase));
        if (collisions > 0) warnings.Add($"UltiAI replaces {collisions} complete compiled database(s). Object-level merging is unavailable.");
        if (decisions.Any(x => x.Kind == MergeDecisionKind.OtherOverride && !x.Identical))
            warnings.Add("The other mod's UI takes precedence. Test end-game labels for additional roles such as Siege.");
        if (otherFiles.ContainsKey(catalog)) warnings.Add("The other mod's catalog is retained. UltiAI catalog-only cosmetic assets may be unavailable.");
        return new MergePreview(outputName, other, ulti, decisions, warnings, provisional);
    }

    private static bool SameContent(string left, string right, CancellationToken token) =>
        new FileInfo(left).Length == new FileInfo(right).Length
        && SourceDeltaAnalyzer.ComputeSha256(left, token) == SourceDeltaAnalyzer.ComputeSha256(right, token);
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

}
