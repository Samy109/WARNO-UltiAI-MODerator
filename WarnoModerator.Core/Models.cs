namespace WarnoModerator.Core;

public enum ModKind { EditableSource, WorkshopCompiled }
public enum DeltaKind { Added, Modified, Deleted }
public enum MergeDecisionKind { OtherOnly, UltiOnly, UltiOverride, Delete, OtherOverride }

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
    public bool IsCollision => Overlap || Kind is MergeDecisionKind.UltiOverride or MergeDecisionKind.OtherOverride;
    public bool IsUltiChange => !Identical && Kind is MergeDecisionKind.UltiOverride or MergeDecisionKind.UltiOnly;
    public string ActionKey => Identical ? "Identical" : Kind.ToString();
    public string ActionLabel => Identical ? "Identical" : Kind switch
    {
        MergeDecisionKind.UltiOverride => "UltiAI Override",
        MergeDecisionKind.UltiOnly => "UltiAI Added",
        MergeDecisionKind.OtherOverride => "Base UI Kept",
        MergeDecisionKind.OtherOnly => "Base Mod Kept",
        MergeDecisionKind.Delete => "Deleted",
        _ => Kind.ToString()
    };
    public string Category => RelativePath.StartsWith("Gen\\NDF\\UI\\", StringComparison.OrdinalIgnoreCase) ? "UI Database"
        : RelativePath.StartsWith("Gen\\NDF\\", StringComparison.OrdinalIgnoreCase)
          && RelativePath.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase) ? "NDF Database"
        : RelativePath.Equals("Gen\\ResourceFile\\Catalog.cat", StringComparison.OrdinalIgnoreCase) ? "Asset Catalog"
        : RelativePath.StartsWith("Gen\\", StringComparison.OrdinalIgnoreCase) ? "Compiled Asset"
        : "Source File";
}

public sealed record MergePreview(string OutputName, ModDescriptor OtherMod, ModDescriptor UltiMod,
    IReadOnlyList<MergeDecision> Decisions, IReadOnlyList<string> Warnings, bool Provisional = false)
{
    public int OverrideCount => Decisions.Count(x => x.Kind == MergeDecisionKind.UltiOverride && !x.Identical);
    public int UltiAddedCount => Decisions.Count(x => x.Kind == MergeDecisionKind.UltiOnly && !x.Identical);
    public int UltiAppliedCount => Decisions.Count(x => x.Kind is MergeDecisionKind.UltiOverride or MergeDecisionKind.UltiOnly && !x.Identical);
    public int BaseKeptCount => Decisions.Count(x => x.Kind == MergeDecisionKind.OtherOnly && !x.Identical);
    public int BaseUiPreservedCount => Decisions.Count(x => x.Kind == MergeDecisionKind.OtherOverride && !x.Identical);
    public int IdenticalCount => Decisions.Count(x => x.Identical);
    public int ConflictCount => Decisions.Count(x => x.IsCollision && !x.Identical);
}

public sealed record CombineRequest(WarnoPaths Paths, ModDescriptor OtherMod, ModDescriptor UltiMod,
    string OutputName, MergePreview Preview);
public sealed record CombineResult(string OutputSourcePath, string OutputRuntimePath, MergePreview Plan, CombinedModState State);
public sealed record CombineProgress(int Percent, string Stage);
public sealed record SourceModFingerprint(string Name, string RootPath, string Fingerprint);
public sealed record CombinedModState(int SchemaVersion, string OutputName,
    SourceModFingerprint OtherMod, SourceModFingerprint PriorityMod,
    string? RuntimeFingerprint = null, string? GameFingerprint = null)
{
    public const int CurrentSchemaVersion = 1;
}
public sealed class CombineException(string message, Exception? innerException = null) : Exception(message, innerException);
