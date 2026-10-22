namespace WarnoModerator.Core;

public enum ModKind { EditableSource, WorkshopCompiled }
public enum DeltaKind { Added, Modified, Deleted }
/// <summary>How a file in the combined output was produced.</summary>
public enum MergeDecisionKind
{
    /// <summary>Only one selected mod provides the file.</summary>
    Single,
    /// <summary>Several mods provide an atomic file; the highest-priority one is used.</summary>
    Override,
    /// <summary>Compiled database merged value by value from each mod's changes against vanilla.</summary>
    Merged,
    /// <summary>A value several mods changed differently inside a merged database (report only).</summary>
    ValueConflict,
    Delete
}

public sealed record WarnoPaths(string WarnoRoot, string ModsRoot, string WorkshopRoot, string SavedModsRoot)
{
    public string ModDataBaseZip => Path.Combine(ModsRoot, "ModData", "base.zip");
}

public sealed record ModDescriptor(string Name, string RootPath, ModKind Kind, string? WorkshopId,
    int? ModGenVersion, int DeckFormatVersion, IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> ConfigKeys)
{
    public string KindLabel => Kind == ModKind.WorkshopCompiled
        ? $"Workshop {WorkshopId}" : "Editable source";
    public string ModGenLabel => ModGenVersion is int v and >= 0 ? $"ModGen {v}" : "ModGen n/a";
    public string DisplayName => Kind == ModKind.WorkshopCompiled
        ? $"{Name}  •  Workshop {WorkshopId}"
        : $"{Name}  •  Editable source";
    public string GenPath => Path.Combine(RootPath, "Gen");
    public string BaseZipPath => Path.Combine(RootPath, "base.zip");
}

public sealed record SourceDelta(string RelativePath, DeltaKind Kind, string? SourcePath);

public sealed record MergeDecision(string RelativePath, MergeDecisionKind Kind, string Winner, string Detail,
    string? SourcePath = null, bool Identical = false, bool Overlap = false)
{
    public bool IsCollision => Kind == MergeDecisionKind.ValueConflict || Overlap && Kind == MergeDecisionKind.Override;
    public string ActionKey => Identical ? "Identical" : Kind.ToString();
    public string ActionLabel => Identical ? "Identical" : Kind switch
    {
        MergeDecisionKind.Single => "Single Mod",
        MergeDecisionKind.Override => "Priority Override",
        MergeDecisionKind.Merged => "Delta Merged",
        MergeDecisionKind.ValueConflict => "Value Conflict",
        MergeDecisionKind.Delete => "Deleted",
        _ => Kind.ToString()
    };
    public string Category => Kind == MergeDecisionKind.ValueConflict ? "Value"
        : RelativePath.StartsWith("Gen\\NDF\\UI\\", StringComparison.OrdinalIgnoreCase) ? "UI Database"
        : RelativePath.StartsWith("Gen\\NDF\\", StringComparison.OrdinalIgnoreCase)
          && RelativePath.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase) ? "NDF Database"
        : RelativePath.Equals("Gen\\ResourceFile\\Catalog.cat", StringComparison.OrdinalIgnoreCase) ? "Asset Catalog"
        : RelativePath.StartsWith("Gen\\", StringComparison.OrdinalIgnoreCase) ? "Compiled Asset"
        : "Source File";
}

/// <param name="Mods">Selected mods in priority order; the first is the primary mod and wins every conflict.</param>
/// <param name="ValueConflicts">Value-level conflicts inside merged databases, for reporting only.</param>
public sealed record MergePreview(string OutputName, IReadOnlyList<ModDescriptor> Mods,
    IReadOnlyList<MergeDecision> Decisions, IReadOnlyList<string> Warnings, bool Provisional = false,
    IReadOnlyList<MergeDecision>? ValueConflicts = null)
{
    public IReadOnlyList<MergeDecision> Conflicts => ValueConflicts ?? [];
    public ModDescriptor Primary => Mods[0];
    public int MergedCount => Decisions.Count(x => x.Kind == MergeDecisionKind.Merged && !x.Identical);
    public int OverrideCount => Decisions.Count(x => x.Kind == MergeDecisionKind.Override && !x.Identical);
    public int SingleCount => Decisions.Count(x => x.Kind == MergeDecisionKind.Single);
    public int IdenticalCount => Decisions.Count(x => x.Identical);
    public int ValueConflictCount => Conflicts.Count;
    public string Summary => $"{Decisions.Count:N0} files · {MergedCount:N0} delta merged · {ValueConflictCount:N0} value conflicts · "
        + $"{OverrideCount:N0} priority overrides · {SingleCount:N0} single-mod · {IdenticalCount:N0} identical";
}

public sealed record CombineRequest(WarnoPaths Paths, IReadOnlyList<ModDescriptor> Mods, string OutputName, MergePreview Preview);
public sealed record CombineResult(string OutputSourcePath, string OutputRuntimePath, MergePreview Plan, CombinedModState State);
public sealed record CombineProgress(int Percent, string Stage);
public sealed record SourceModFingerprint(string Name, string RootPath, string Fingerprint);

/// <param name="Mods">Source mods in priority order (primary first).</param>
public sealed record CombinedModState(int SchemaVersion, string OutputName, IReadOnlyList<SourceModFingerprint>? Mods,
    string? RuntimeFingerprint = null, string? GameFingerprint = null,
    SourceModFingerprint? OtherMod = null, SourceModFingerprint? PriorityMod = null)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Source mods in priority order; schema 1 records stored UltiAI as the priority mod.</summary>
    public IReadOnlyList<SourceModFingerprint> Sources => Mods
        ?? new[] { PriorityMod, OtherMod }.OfType<SourceModFingerprint>().ToArray();

    public string SourcesLabel => string.Join(" > ", Sources.Select(x => x.Name));
}
public sealed class CombineException(string message, Exception? innerException = null) : Exception(message, innerException);
