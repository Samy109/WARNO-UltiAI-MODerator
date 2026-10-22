using WarnoModerator.Core.Ndf;

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
        MergePlanner.ValidateInputs(request.Paths, request.Mods, token);
        var outputSource = Path.Combine(request.Paths.ModsRoot, request.OutputName);
        var outputRuntime = Path.Combine(request.Paths.SavedModsRoot, request.OutputName);
        foreach (var output in new[] { outputSource, outputRuntime })
            if (request.Mods.Any(m => MergePlanner.SamePath(output, m.RootPath)))
                throw new CombineException("The output must not replace an input mod.");
        VerifyModDirectoryWritable(request.Paths.ModsRoot);
        using var operationLock = BuildRecovery.AcquireLock(request.Paths, request.OutputName);
        var exists = Directory.Exists(outputSource) || Directory.Exists(outputRuntime);
        if (rebuild && !exists) throw new CombineException("The existing combined mod is missing. Refresh mods.");
        if (!rebuild && exists) throw new CombineException("The output already exists. Refresh mods and rebuild it instead.");
        var fingerprints = new ModFingerprintService();
        var health = new CombinationHealth();
        Log($"[STEP 1/7] Starting {(rebuild ? "rebuild" : "new merge")} for '{request.OutputName}'...");
        for (var i = 0; i < request.Mods.Count; i++)
            Log($"  {(i == 0 ? "Primary" : $"Priority {i + 1}")}: {request.Mods[i].Name} ({request.Mods[i].KindLabel}, {request.Mods[i].ModGenLabel})");
        Report(0, "Checking inputs");
        Log("[STEP 2/7] Computing SHA-256 baseline fingerprints for source mods and WARNO base.zip...");
        var before = await fingerprints.ComputeAsync(request.Mods, cancellationToken: token).ConfigureAwait(false);
        var gameHash = health.GameHash(request.Paths, token);
        foreach (var fingerprint in before) Log($"  {fingerprint.Name} SHA-256: {fingerprint.Fingerprint[..12]}…");
        Log($"  WARNO ModData\\base.zip SHA-256: {gameHash[..12]}…");
        var planner = new MergePlanner(deltaAnalyzer);
        var sourcePlan = planner.PlanSourceMerge(request.Mods, token);
        var recovery = new BuildRecovery();
        var record = recovery.Begin(request.Paths, request.OutputName);
        CombineResult result;
        try
        {
            recovery.MoveOriginals(request.Paths, record);
            if (record.SourceExisted || record.RuntimeExisted)
                Log($"  Safeguarded existing '{request.OutputName}' folders to recovery backup.");
            Report(5, "Creating local mod");
            Log($"[STEP 3/7] Running WARNO SDK CreateNewMod.py to initialize '{request.OutputName}'...");
            await RunSdkAsync(request, "CreateNewMod.py", [request.OutputName], request.Paths.ModsRoot, Log, token).ConfigureAwait(false);
            if (!Directory.Exists(outputSource)) throw new CombineException("WARNO did not create the output directory.");
            if (sourcePlan.Count > 0)
            {
                var deletedCount = sourcePlan.Count(x => x.Kind == MergeDecisionKind.Delete);
                Log($"  Applying {sourcePlan.Count:N0} editable source delta file(s) ({sourcePlan.Count - deletedCount:N0} copied, {deletedCount:N0} deleted)...");
            }
            foreach (var decision in sourcePlan)
            {
                token.ThrowIfCancellationRequested();
                var destination = FileSystemOps.SafeCombine(outputSource, decision.RelativePath);
                if (decision.Kind == MergeDecisionKind.Delete) File.Delete(destination);
                else FileSystemOps.CopyFileAtomic(decision.SourcePath!, destination, token);
            }
            Report(10, "Generating with WARNO");
            Log($"[STEP 4/7] Running WARNO SDK GenerateMod.py to build local mod identity and Config.ini baseline...");
            await RunSdkAsync(request, "GenerateMod.py", ["WARNO", request.OutputName], outputSource, Log, token).ConfigureAwait(false);
            var generatedConfig = LoadGeneratedConfig(request, outputRuntime, Log);
            MergePreview plan;
            if (request.Mods.Any(m => m.Kind == ModKind.WorkshopCompiled))
            {
                var inputs = CompiledInputs(request, outputRuntime, generatedConfig);
                var vanilla = VanillaDatabases.Open(request.Paths.WarnoRoot, generatedConfig.GetInt("Properties", "ModGenVersion", -1));
                Log(vanilla.Available
                    ? $"  Using vanilla databases from {vanilla.ArchivePath} as the delta baseline."
                    : "  [WARN] Vanilla databases (Glad.dat) not found; overlapping databases fall back to whole-file priority.");
                Report(20, "Delta merging databases");
                Log($"  Planning and delta merging compiled databases: {string.Join(" > ", inputs.Select(x => x.Name))}...");
                var mergeDirectory = Path.Combine(outputSource, ".combine-merged-" + Guid.NewGuid().ToString("N"));
                try
                {
                    plan = MergePlanner.CreateCompiledPlan(request.OutputName, request.Mods, inputs, vanilla, mergeDirectory, token);
                    foreach (var warning in plan.Warnings) Log("  [NOTE] " + warning);
                    ComposeCompiledPayload(request, plan, inputs, generatedConfig, outputSource, outputRuntime, Log, Report, token);
                }
                finally
                {
                    if (Directory.Exists(mergeDirectory)) Directory.Delete(mergeDirectory, true);
                }
            }
            else
            {
                plan = new MergePreview(request.OutputName, request.Mods, sourcePlan, []);
                Log($"[STEP 5/7] Verifying {plan.Decisions.Count:N0} merged source file(s) in '{outputSource}'...");
                foreach (var decision in plan.Decisions)
                {
                    token.ThrowIfCancellationRequested();
                    var actual = FileSystemOps.SafeCombine(outputSource, decision.RelativePath);
                    if (decision.Kind == MergeDecisionKind.Delete)
                    { if (File.Exists(actual)) throw new CombineException($"Deletion verification failed for {decision.RelativePath}."); }
                    else VerifySameFile(actual, decision.SourcePath!, decision.RelativePath, token);
                }
                Log($"[STEP 6/7] Verified generated Config.ini compatibility manifest in '{outputRuntime}'.");
            }
            if (!Directory.Exists(Path.Combine(outputSource, "Gen")) || !Directory.Exists(Path.Combine(outputRuntime, "Gen")))
                throw new CombineException("WARNO generated incomplete output.");
            Report(92, "Verifying inputs and output");
            Log("[STEP 7/7] Verifying output runtime SHA-256, input stability, and saving .warno-moderator.json tracking record...");
            var runtimeHash = await health.RuntimeHashAsync(outputRuntime, token).ConfigureAwait(false);
            var after = await fingerprints.ComputeAsync(request.Mods, cancellationToken: token).ConfigureAwait(false);
            CombinationHealth.VerifyInputs(before, after);
            if (health.GameHash(request.Paths, token) != gameHash)
                throw new CombineException("WARNO build data changed during the merge. Let Steam finish updating, then retry.");
            var state = new CombinedModState(CombinedModState.CurrentSchemaVersion, request.OutputName, after,
                runtimeHash, gameHash);
            token.ThrowIfCancellationRequested();
            new CombinedModStateStore().Save(outputSource, state);
            Log($"  Runtime SHA-256: {runtimeHash[..12]}… (inputs and WARNO base.zip verified unchanged during build).");
            result = new CombineResult(outputSource, outputRuntime, plan, state);
            recovery.Complete(request.Paths, record, Log);
        }
        catch (Exception original)
        {
            Log("[ROLLBACK] Operation stopped; restoring previous outputs independently...");
            try { recovery.Restore(request.Paths, record, Log); }
            catch (Exception restoration)
            {
                throw new CombineException($"{original.Message}{Environment.NewLine}{restoration.Message}",
                    new AggregateException(original, restoration));
            }
            throw;
        }
        // Cancellation after the commit cannot turn a completed build into a rollback.
        Log($"[OK] Verified {result.Plan.Summary}. Combination completed successfully.");
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

    private static IniDocument LoadGeneratedConfig(CombineRequest request, string runtime, Action<string> log)
    {
        var path = Path.Combine(runtime, "Config.ini");
        if (!File.Exists(path)) throw new CombineException("WARNO did not generate the compatibility manifest.");
        var config = IniDocument.Load(path);
        var version = config.GetInt("Properties", "ModGenVersion", -1);
        if (version < 0) throw new CombineException("WARNO generated a manifest without a ModGen revision.");
        foreach (var input in request.Mods.Where(x => x.Kind == ModKind.WorkshopCompiled))
            if (input.ModGenVersion is null || input.ModGenVersion != version)
                throw new CombineException($"{input.Name} uses ModGen {input.ModGenVersion}, but installed WARNO requires {version}. Refresh the Workshop subscription.");
        log($"  Verified installed WARNO ModGenVersion = {version} (matches selected inputs).");
        return config;
    }

    /// <summary>Compiled inputs in priority order; editable mods are represented once by WARNO's generation of them.</summary>
    private static IReadOnlyList<CompiledInput> CompiledInputs(CombineRequest request, string runtime, IniDocument generatedConfig)
    {
        var inputs = new List<CompiledInput>();
        var editable = request.Mods.Where(m => m.Kind == ModKind.EditableSource).Select(m => m.Name).ToArray();
        foreach (var mod in request.Mods)
        {
            if (mod.Kind == ModKind.WorkshopCompiled) inputs.Add(new CompiledInput(mod.Name, mod.RootPath, mod.ConfigKeys));
            else if (mod.Name == editable[0])
                inputs.Add(new CompiledInput(editable.Length == 1 ? mod.Name : $"Editable ({string.Join(", ", editable)})",
                    runtime, generatedConfig.GetSection("Config")));
        }
        return inputs;
    }

    private static void ComposeCompiledPayload(CombineRequest request, MergePreview plan, IReadOnlyList<CompiledInput> inputs,
        IniDocument generatedConfig, string source, string runtime, Action<string> log, Action<int, string> report, CancellationToken token)
    {
        log($"[STEP 5/7] Composing compiled payload ({plan.Summary})...");
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
        log($"  Staged and SHA-256 verified all {plan.Decisions.Count:N0} compiled payload files.");
        SynthesizeConfig(request, plan, inputs, generatedConfig);
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
        log($"  Deployed verified payload to Mods\\{request.OutputName} and Saved Games\\...\\mod\\{request.OutputName}.");
        report(85, "Writing compatibility manifest");
        var configPath = Path.Combine(runtime, "Config.ini");
        generatedConfig.Save(configPath);
        var saved = IniDocument.Load(configPath);
        var configSection = generatedConfig.GetSection("Config");
        foreach (var entry in configSection)
            if (saved.Get("Config", entry.Key) != entry.Value) throw new CombineException($"Manifest verification failed for {entry.Key}.");
        log($"[STEP 6/7] Wrote and verified Config.ini manifest ({configSection.Count:N0} NDF database fingerprints, DeckFormatVersion {saved.Get("Properties", "DeckFormatVersion")}, Tags: {saved.Get("Properties", "TagList")}).");
        Directory.Delete(staging, true);
    }

    private static void SynthesizeConfig(CombineRequest request, MergePreview plan, IReadOnlyList<CompiledInput> inputs, IniDocument output)
    {
        foreach (var decision in plan.Decisions.Where(x => x.RelativePath.StartsWith("Gen\\NDF\\", StringComparison.OrdinalIgnoreCase)
                     && x.RelativePath.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase)))
        {
            var key = decision.RelativePath[8..^7].Replace('\\', '/');
            // WARNO's fingerprint identifies the database revision rather than its content, so a merged database
            // takes the entry of an input that ships it, preferring the decision's winner.
            var providers = inputs.Where(i => File.Exists(Path.Combine(i.Root, decision.RelativePath)))
                .OrderByDescending(i => decision.Winner.Split(" + ").Contains(i.Name)).ToArray();
            var fingerprint = providers.Select(i => i.ConfigKeys.GetValueOrDefault(key)).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
            if (fingerprint is null)
            {
                // WARNO never fingerprints per-mod-name databases (Localisation/<Mod>, ResourcePacks/<Mod>); the
                // output gets its own from runtime generation, so an input's copy is inert rather than unverifiable.
                if (IsInputNamedDatabase(key)) continue;
                throw new CombineException($"{decision.Winner}'s selected database {decision.RelativePath} has no matching manifest fingerprint ({key}). Refresh or regenerate that input.");
            }
            output.Set("Config", key, fingerprint);
        }
        output.Set("Properties", "Name", request.OutputName);
        output.Set("Properties", "TagList", string.Join(',', request.Mods.SelectMany(m => m.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)));
        output.Set("Properties", "ID", "0");
        output.Set("Properties", "DeckFormatVersion", Math.Max(output.GetInt("Properties", "DeckFormatVersion"),
            request.Mods.Max(m => m.DeckFormatVersion)).ToString());
    }

    private static bool IsInputNamedDatabase(string key)
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
