namespace WarnoModerator.Core;

public sealed class CombinationHealth
{
    public static bool CanRebuild(bool busy, bool hasSelection, bool existing) =>
        !busy && hasSelection && existing;

    public async Task<string> RuntimeHashAsync(string root)
    {
        var descriptor = new ModDescriptor("Combined output", root, ModKind.WorkshopCompiled,
            null, null, 0, 0, [], new Dictionary<string, string>());
        return (await new ModFingerprintService().ComputeAsync([descriptor]))[0].Fingerprint;
    }

    public string GameHash(WarnoPaths paths) => SourceDeltaAnalyzer.ComputeSha256(paths.ModDataBaseZip);

    public async Task<string> CheckAsync(WarnoPaths paths, CombinedModState state)
    {
        var runtime = Path.Combine(paths.SavedModsRoot, state.OutputName);
        if (!File.Exists(Path.Combine(runtime, "Config.ini")) || !Directory.Exists(Path.Combine(runtime, "Gen"))
            || !Directory.Exists(Path.Combine(paths.ModsRoot, state.OutputName, "Gen")))
            return "Combined output is missing or incomplete. Rebuild required.";
        if (state.RuntimeFingerprint is null || state.GameFingerprint is null)
            return "Rebuild once to verify and track the existing output.";
        if (state.GameFingerprint != GameHash(paths))
            return "WARNO build data changed. Rebuild required.";
        if (state.RuntimeFingerprint != await RuntimeHashAsync(runtime))
            return "Combined output changed. Rebuild required.";
        return "Combined output matches the last merge. You can rebuild again.";
    }

    public static void VerifyInputs(IReadOnlyList<SourceModFingerprint> before, IReadOnlyList<SourceModFingerprint> after)
    {
        if (before.Count != after.Count || before.Where((item, index) =>
            !CombinedModStateStore.FingerprintMatches(item, after[index])).Any())
            throw new CombineException("Source mods changed during the merge. Let Steam finish downloading, then retry.");
    }
}
