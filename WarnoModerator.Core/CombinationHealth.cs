namespace WarnoModerator.Core;

public sealed class CombinationHealth
{
public async Task<string> RuntimeHashAsync(string root, CancellationToken cancellationToken = default)
    {
        var descriptor = new ModDescriptor("Combined output", root, ModKind.WorkshopCompiled,
            null, null, 0, [], new Dictionary<string, string>());
        return (await new ModFingerprintService().ComputeAsync([descriptor], cancellationToken: cancellationToken))[0].Fingerprint;
    }

    public string GameHash(WarnoPaths paths, CancellationToken cancellationToken = default) => SourceDeltaAnalyzer.ComputeSha256(paths.ModDataBaseZip, cancellationToken);

    public async Task<string> CheckAsync(WarnoPaths paths, CombinedModState state, CancellationToken cancellationToken = default)
    {
        var runtime = Path.Combine(paths.SavedModsRoot, state.OutputName);
        if (!File.Exists(Path.Combine(runtime, "Config.ini")) || !Directory.Exists(Path.Combine(runtime, "Gen"))
            || !Directory.Exists(Path.Combine(paths.ModsRoot, state.OutputName, "Gen")))
            return "Combined output is missing or incomplete. Rebuild required.";
        if (state.RuntimeFingerprint is null || state.GameFingerprint is null)
            return "Rebuild once to verify and track the existing output.";
        if (state.GameFingerprint != GameHash(paths, cancellationToken))
            return "WARNO build data changed. Rebuild required.";
        if (state.RuntimeFingerprint != await RuntimeHashAsync(runtime, cancellationToken))
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
