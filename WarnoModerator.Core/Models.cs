namespace WarnoModerator.Core;

public enum ModKind
{
    EditableSource,
    WorkshopCompiled
}

public enum DeltaKind
{
    Added,
    Modified,
    Deleted
}

public enum MergeDecisionKind
{
    OtherOnly,
    UltiOnly,
    UltiOverride,
    Delete
}

public sealed record WarnoPaths(
    string SteamRoot,
    string WarnoRoot,
    string ModsRoot,
    string WorkshopRoot,
    string SavedModsRoot)
{
    public string CreateNewModBatch => Path.Combine(ModsRoot, "CreateNewMod.bat");
    public string ModDataBaseZip => Path.Combine(ModsRoot, "ModData", "base.zip");
}

public sealed record ModDescriptor(
    string Name,
    string RootPath,
    ModKind Kind,
    string? WorkshopId,
    int? ModGenVersion,
    int Version,
    int DeckFormatVersion,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> ConfigKeys)
{
    public string DisplayName => Kind == ModKind.WorkshopCompiled
        ? $"{Name}  •  Workshop {WorkshopId}"
        : $"{Name}  •  Editable source";

    public string GenPath => Path.Combine(RootPath, "Gen");
    public string BaseZipPath => Path.Combine(RootPath, "base.zip");
}

public sealed record SourceDelta(string RelativePath, DeltaKind Kind, string? SourcePath);

public sealed record MergeDecision(
    string RelativePath,
    MergeDecisionKind Kind,
    string Winner,
    string Detail);

public sealed record MergePreview(
    string OutputName,
    ModDescriptor OtherMod,
    ModDescriptor UltiMod,
    IReadOnlyList<MergeDecision> Decisions,
    IReadOnlyList<string> Warnings,
    bool CanExecute)
{
    public int OverrideCount => Decisions.Count(x => x.Kind == MergeDecisionKind.UltiOverride);
    public int OtherCount => Decisions.Count(x => x.Kind == MergeDecisionKind.OtherOnly);
    public int UltiCount => Decisions.Count(x => x.Kind == MergeDecisionKind.UltiOnly);
}

public sealed record CombineRequest(
    WarnoPaths Paths,
    ModDescriptor OtherMod,
    ModDescriptor UltiMod,
    string OutputName,
    MergePreview Preview);

public sealed record CombineResult(
    string OutputSourcePath,
    string OutputRuntimePath,
    IReadOnlyList<string> LogLines);

public sealed record CombineProgress(int Percent, string Stage);

public sealed class CombineException(string message) : Exception(message);
