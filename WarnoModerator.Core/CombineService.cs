namespace WarnoModerator.Core;

public sealed class CombineService(SourceDeltaAnalyzer deltaAnalyzer, IProcessRunner processRunner)
{
    public Task<CombineResult> CombineAsync(CombineRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, IProgress<CombineProgress>? operationProgress = null) =>
        ExecuteAsync(request, false, progress, cancellationToken, operationProgress);

    public Task<CombineResult> RebuildAsync(CombineRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, IProgress<CombineProgress>? operationProgress = null) =>
        ExecuteAsync(request, true, progress, cancellationToken, operationProgress);

    private async Task<CombineResult> ExecuteAsync(CombineRequest request, bool rebuild, IProgress<string>? progress,
        CancellationToken token, IProgress<CombineProgress>? operationProgress)
    {
        void Log(string message) => progress?.Report(message);
        void Report(int percent, string stage) { token.ThrowIfCancellationRequested(); operationProgress?.Report(new(percent, stage)); }
        MergePlanner.ValidateOutputName(request.OutputName);
        MergePlanner.ValidateInputs(request.Paths, request.OtherMod, request.UltiMod, token);
        var outputSource = Path.Combine(request.Paths.ModsRoot, request.OutputName);
        var outputRuntime = Path.Combine(request.Paths.SavedModsRoot, request.OutputName);
        foreach (var output in new[] { outputSource, outputRuntime })
            if (MergePlanner.SamePath(output, request.OtherMod.RootPath) || MergePlanner.SamePath(output, request.UltiMod.RootPath))
                throw new CombineException("The output must not replace an input mod.");
        VerifyModDirectoryWritable(request.Paths.ModsRoot);
        using var operationLock = BuildRecovery.AcquireLock(request.Paths, request.OutputName);
        var exists = Directory.Exists(outputSource) || Directory.Exists(outputRuntime);
        if (rebuild && !exists) throw new CombineException("The existing combined mod is missing. Refresh mods.");
        if (!rebuild && exists) throw new CombineException("The output already exists. Refresh mods and rebuild it instead.");
        var fingerprints = new ModFingerprintService();
        var health = new CombinationHealth();
        Report(0, "Checking inputs");
        var before = await fingerprints.ComputeAsync([request.OtherMod, request.UltiMod], cancellationToken: token).ConfigureAwait(false);
        var gameHash = health.GameHash(request.Paths, token);
        var planner = new MergePlanner(deltaAnalyzer);
        var sourcePlan = planner.PlanSourceMerge(request.OtherMod, request.UltiMod, token);
        var recovery = new BuildRecovery();
        var record = recovery.Begin(request.Paths, request.OutputName);
        CombineResult result;
        try
        {
            recovery.MoveOriginals(request.Paths, record);
            Report(5, "Creating local mod");
            await RunSdkAsync(request, "CreateNewMod.py", [request.OutputName], request.Paths.ModsRoot, Log, token).ConfigureAwait(false);
            if (!Directory.Exists(outputSource)) throw new CombineException("WARNO did not create the output directory.");
            foreach (var decision in sourcePlan)
            {
                token.ThrowIfCancellationRequested();
                var destination = FileSystemOps.SafeCombine(outputSource, decision.RelativePath);
                if (decision.Kind == MergeDecisionKind.Delete) File.Delete(destination);
                else FileSystemOps.CopyFileAtomic(decision.SourcePath!, destination, token);
            }
            Report(10, "Generating with WARNO");
            await RunSdkAsync(request, "GenerateMod.py", ["WARNO", request.OutputName], outputSource, Log, token).ConfigureAwait(false);
            var generatedConfig = LoadGeneratedConfig(request, outputRuntime);
            MergePreview plan;
            if (request.OtherMod.Kind == ModKind.WorkshopCompiled || request.UltiMod.Kind == ModKind.WorkshopCompiled)
            {
                var otherRoot = request.OtherMod.Kind == ModKind.WorkshopCompiled ? request.OtherMod.RootPath : outputRuntime;
                var ultiRoot = request.UltiMod.Kind == ModKind.WorkshopCompiled ? request.UltiMod.RootPath : outputSource;
                plan = MergePlanner.CreateCompiledPlan(request.OutputName, request.OtherMod, request.UltiMod, otherRoot, ultiRoot, token);
                ComposeCompiledPayload(request, plan, generatedConfig, outputSource, outputRuntime, Report, token);
            }
            else
            {
                plan = new MergePreview(request.OutputName, request.OtherMod, request.UltiMod, sourcePlan, []);
                foreach (var decision in plan.Decisions)
                {
                    token.ThrowIfCancellationRequested();
                    var actual = FileSystemOps.SafeCombine(outputSource, decision.RelativePath);
                    if (decision.Kind == MergeDecisionKind.Delete)
                    { if (File.Exists(actual)) throw new CombineException($"Deletion verification failed for {decision.RelativePath}."); }
                    else VerifySameFile(actual, decision.SourcePath!, decision.RelativePath, token);
                }
            }
            if (!Directory.Exists(Path.Combine(outputSource, "Gen")) || !Directory.Exists(Path.Combine(outputRuntime, "Gen")))
                throw new CombineException("WARNO generated incomplete output.");
            Report(92, "Verifying inputs and output");
            var runtimeHash = await health.RuntimeHashAsync(outputRuntime, token).ConfigureAwait(false);
            var after = await fingerprints.ComputeAsync([request.OtherMod, request.UltiMod], cancellationToken: token).ConfigureAwait(false);
            CombinationHealth.VerifyInputs(before, after);
            if (health.GameHash(request.Paths, token) != gameHash)
                throw new CombineException("WARNO build data changed during the merge. Let Steam finish updating, then retry.");
            var state = new CombinedModState(CombinedModState.CurrentSchemaVersion, request.OutputName, after[0], after[1],
                runtimeHash, gameHash);
            token.ThrowIfCancellationRequested();
            new CombinedModStateStore().Save(outputSource, state);
            result = new CombineResult(outputSource, outputRuntime, plan, state);
            recovery.Complete(request.Paths, record, Log);
        }
        catch (Exception original)
        {
            Log("Operation stopped; restoring previous outputs independently...");
            try { recovery.Restore(request.Paths, record, Log); }
            catch (Exception restoration)
            {
                throw new CombineException($"{original.Message}{Environment.NewLine}{restoration.Message}",
                    new AggregateException(original, restoration));
            }
            throw;
        }
        // Cancellation after the commit cannot turn a completed build into a rollback.
        Log($"Verified {result.Plan.Decisions.Count} merge decisions. Combination completed successfully.");
        operationProgress?.Report(new(100, "Complete"));
        return result;
    }

    private async Task RunSdkAsync(CombineRequest request, string scriptName, IEnumerable<string> arguments,
        string workingDirectory, Action<string> log, CancellationToken token)
    {
        var python = Path.Combine(request.Paths.ModsRoot, "Utils", "Python", "python.exe");
        var script = Path.Combine(request.Paths.ModsRoot, "Utils", "Scripts", scriptName);
        if (!File.Exists(python) || !File.Exists(script)) throw new CombineException($"WARNO's {scriptName} tools are missing.");
        token.ThrowIfCancellationRequested();
        var exitCode = await processRunner.RunAsync(python, new[] { script }.Concat(arguments), workingDirectory, log, token).ConfigureAwait(false);
        if (exitCode != 0) throw new CombineException($"WARNO {scriptName} failed with exit code {exitCode}.");
    }

    private static void VerifyModDirectoryWritable(string root)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(root, $".warno-moderator-write-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { throw new CombineException("WARNO\\Mods is not writable. Check permissions or run the app as administrator.", ex); }
    }

    private static IniDocument LoadGeneratedConfig(CombineRequest request, string runtime)
    {
        var path = Path.Combine(runtime, "Config.ini");
        if (!File.Exists(path)) throw new CombineException("WARNO did not generate the compatibility manifest.");
        var config = IniDocument.Load(path);
        var version = config.GetInt("Properties", "ModGenVersion", -1);
        if (version < 0) throw new CombineException("WARNO generated a manifest without a ModGen revision.");
        foreach (var input in new[] { request.OtherMod, request.UltiMod }.Where(x => x.Kind == ModKind.WorkshopCompiled))
            if (input.ModGenVersion is null || input.ModGenVersion != version)
                throw new CombineException($"{input.Name} uses ModGen {input.ModGenVersion}, but installed WARNO requires {version}. Refresh the Workshop subscription.");
        return config;
    }

    private static void ComposeCompiledPayload(CombineRequest request, MergePreview plan, IniDocument generatedConfig,
        string source, string runtime, Action<int, string> report, CancellationToken token)
    {
        var staging = Path.Combine(source, ".combine-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        // The plan is the only source of file winners. Stage and verify before replacing generated inputs.
        for (var i = 0; i < plan.Decisions.Count; i++)
        {
            var decision = plan.Decisions[i];
            var staged = FileSystemOps.SafeCombine(staging, decision.RelativePath);
            FileSystemOps.CopyFileAtomic(decision.SourcePath!, staged, token);
            VerifySameFile(staged, decision.SourcePath!, decision.RelativePath, token);
            if (i % 25 == 0) report(40 + 30 * i / Math.Max(1, plan.Decisions.Count), "Composing and verifying files");
        }
        SynthesizeConfig(request, plan, generatedConfig);
        foreach (var root in new[] { source, runtime })
        {
            token.ThrowIfCancellationRequested();
            var gen = Path.Combine(root, "Gen");
            if (Directory.Exists(gen)) Directory.Delete(gen, true);
            foreach (var decision in plan.Decisions)
            {
                var staged = FileSystemOps.SafeCombine(staging, decision.RelativePath);
                var destination = FileSystemOps.SafeCombine(root, decision.RelativePath);
                FileSystemOps.CopyFileAtomic(staged, destination, token);
                VerifySameFile(destination, staged, decision.RelativePath, token);
            }
        }
        report(85, "Writing compatibility manifest");
        var configPath = Path.Combine(runtime, "Config.ini");
        generatedConfig.Save(configPath);
        var saved = IniDocument.Load(configPath);
        foreach (var entry in generatedConfig.GetSection("Config"))
            if (saved.Get("Config", entry.Key) != entry.Value) throw new CombineException($"Manifest verification failed for {entry.Key}.");
        Directory.Delete(staging, true);
    }

    private static void SynthesizeConfig(CombineRequest request, MergePreview plan, IniDocument output)
    {
        var generatedKeys = output.GetSection("Config");
        foreach (var decision in plan.Decisions.Where(x => x.RelativePath.StartsWith("Gen\\NDF\\", StringComparison.OrdinalIgnoreCase)
                     && x.RelativePath.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase)))
        {
            var key = decision.RelativePath[8..^7].Replace('\\', '/');
            var winner = decision.Kind is MergeDecisionKind.OtherOnly or MergeDecisionKind.OtherOverride ? request.OtherMod : request.UltiMod;
            var keys = winner.Kind == ModKind.EditableSource ? generatedKeys : winner.ConfigKeys;
            if (!keys.TryGetValue(key, out var fingerprint) || string.IsNullOrWhiteSpace(fingerprint))
            {
                // WARNO never fingerprints per-mod-name databases (Localisation/<Mod>, ResourcePacks/<Mod>); the
                // output gets its own from runtime generation, so an input's copy is inert rather than unverifiable.
                if (IsInputNamedDatabase(key, request)) continue;
                throw new CombineException($"{winner.Name}'s selected database {decision.RelativePath} has no matching manifest fingerprint ({key}). Refresh or regenerate that input.");
            }
            output.Set("Config", key, fingerprint);
        }
        output.Set("Properties", "Name", request.OutputName);
        output.Set("Properties", "TagList", string.Join(',', request.OtherMod.Tags.Union(request.UltiMod.Tags, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)));
        output.Set("Properties", "ID", "0");
        output.Set("Properties", "DeckFormatVersion", Math.Max(output.GetInt("Properties", "DeckFormatVersion"),
            Math.Max(request.OtherMod.DeckFormatVersion, request.UltiMod.DeckFormatVersion)).ToString());
    }

    private static bool IsInputNamedDatabase(string key, CombineRequest request)
    {
        var slash = key.IndexOf('/');
        if (slash < 0) return false;
        var folder = key[..slash];
        if (!folder.Equals("Localisation", StringComparison.OrdinalIgnoreCase)
            && !folder.Equals("ResourcePacks", StringComparison.OrdinalIgnoreCase)) return false;
        // Mods ship extra per-mod databases such as Localisation/<Mod>_test, so any database in these folders
        // is treated as inert when unfingerprinted; the output regenerates its own at runtime.
        return true;
    }

    private static void VerifySameFile(string actual, string expected, string relativePath, CancellationToken token)
    {
        if (!File.Exists(actual) || !File.Exists(expected)
            || SourceDeltaAnalyzer.ComputeSha256(actual, token) != SourceDeltaAnalyzer.ComputeSha256(expected, token))
            throw new CombineException($"Precedence verification failed for {relativePath}.");
    }
}
