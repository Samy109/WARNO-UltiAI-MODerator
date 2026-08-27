namespace WarnoModerator.Core;

public sealed class CombineService(
    SourceDeltaAnalyzer deltaAnalyzer,
    IProcessRunner processRunner)
{
    public async Task<CombineResult> CombineAsync(
        CombineRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var logLines = new List<string>();
        void Log(string line)
        {
            lock (logLines) logLines.Add(line);
            progress?.Report(line);
        }

        var outputSource = Path.Combine(request.Paths.ModsRoot, request.OutputName);
        var outputRuntime = Path.Combine(request.Paths.SavedModsRoot, request.OutputName);

        if (Directory.Exists(outputSource) || Directory.Exists(outputRuntime))
        {
            throw new CombineException("The output appeared after preview. Refresh and choose another name.");
        }

        Log($"Creating '{request.OutputName}' with WARNO's mod SDK...");
        await RunCreateNewModAsync(request, Log, cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(outputSource))
        {
            throw new CombineException("CreateNewMod.bat completed without creating the output directory.");
        }

        try
        {
            if (request.OtherMod.Kind == ModKind.EditableSource)
            {
                Log($"Applying source delta from {request.OtherMod.Name}...");
                ApplySourceDelta(deltaAnalyzer.Analyze(request.OtherMod), outputSource);
            }

            Log($"Applying {request.UltiMod.Name} with highest precedence...");
            ApplySourceDelta(deltaAnalyzer.Analyze(request.UltiMod), outputSource);

            Log("Generating the Ulti-aware output with WARNO...");
            await RunGenerateAsync(request.Paths, outputSource, request.OutputName, Log, cancellationToken)
                .ConfigureAwait(false);

            if (request.OtherMod.Kind == ModKind.WorkshopCompiled)
            {
                Log($"Composing compiled Workshop payload from {request.OtherMod.Name}...");
                ComposeWorkshopPayload(request, outputSource, outputRuntime, Log);
            }

            VerifyResult(request, outputSource, outputRuntime, Log);
            Log("Combination and verification completed successfully.");
            return new CombineResult(outputSource, outputRuntime, logLines.ToArray());
        }
        catch
        {
            Log("The operation stopped. Input mods were not changed; the incomplete output was preserved for inspection.");
            throw;
        }
    }

    private async Task RunCreateNewModAsync(
        CombineRequest request,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.Paths.CreateNewModBatch))
        {
            throw new CombineException("CreateNewMod.bat was not found in WARNO\\Mods.");
        }

        var command = $"\"\"{request.Paths.CreateNewModBatch}\" \"{request.OutputName}\"\"";
        var exitCode = await processRunner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/s", "/c", command],
            request.Paths.ModsRoot,
            log,
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new CombineException($"CreateNewMod.bat failed with exit code {exitCode}.");
        }
    }

    private async Task RunGenerateAsync(
        WarnoPaths paths,
        string outputSource,
        string outputName,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var python = Path.Combine(paths.ModsRoot, "Utils", "Python", "python.exe");
        var script = Path.Combine(paths.ModsRoot, "Utils", "Scripts", "GenerateMod.py");
        if (!File.Exists(python) || !File.Exists(script))
        {
            throw new CombineException("WARNO's non-interactive generation tools are missing.");
        }

        var exitCode = await processRunner.RunAsync(
            python,
            [script, "WARNO", outputName],
            outputSource,
            log,
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new CombineException($"WARNO mod generation failed with exit code {exitCode}.");
        }
    }

    private static void ApplySourceDelta(IEnumerable<SourceDelta> deltas, string outputRoot)
    {
        foreach (var delta in deltas)
        {
            var destination = FileSystemOps.SafeCombine(outputRoot, delta.RelativePath);
            if (delta.Kind == DeltaKind.Deleted)
            {
                if (File.Exists(destination)) File.Delete(destination);
                continue;
            }

            FileSystemOps.CopyFileAtomic(delta.SourcePath!, destination);
        }
    }

    private void ComposeWorkshopPayload(
        CombineRequest request,
        string outputSource,
        string outputRuntime,
        Action<string> log)
    {
        var staging = Path.Combine(outputSource, ".combine-staging-" + Guid.NewGuid().ToString("N"));
        var generatedBackup = Path.Combine(outputSource, ".generated-ulti-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var item in MergePlanner.EnumerateRuntimeFiles(request.OtherMod.RootPath))
            {
                FileSystemOps.CopyFileAtomic(
                    item.FullPath,
                    FileSystemOps.SafeCombine(staging, item.RelativePath));
            }

            var outputGen = Path.Combine(outputSource, "Gen");
            foreach (var item in MergePlanner.EnumerateUltiOverlayFiles(outputGen))
            {
                FileSystemOps.CopyFileAtomic(
                    item.FullPath,
                    FileSystemOps.SafeCombine(staging, item.RelativePath));
            }

            var stagedCatalog = Path.Combine(staging, "Gen", "ResourceFile", "Catalog.cat");
            if (!File.Exists(stagedCatalog))
            {
                var generatedCatalog = Path.Combine(outputGen, "ResourceFile", "Catalog.cat");
                if (File.Exists(generatedCatalog))
                {
                    FileSystemOps.CopyFileAtomic(generatedCatalog, stagedCatalog);
                }
            }

            VerifyCompiledPlan(request.Preview, staging, outputGen, request.OtherMod.RootPath);

            Directory.Move(outputGen, generatedBackup);
            Directory.Move(Path.Combine(staging, "Gen"), outputGen);

            foreach (var child in new[] { "GameData", "DatasMap", "DecorsSets", "Maps", "Scenarios" })
            {
                var stagedChild = Path.Combine(staging, child);
                if (Directory.Exists(stagedChild))
                {
                    FileSystemOps.CopyDirectory(stagedChild, Path.Combine(outputSource, child));
                    FileSystemOps.CopyDirectory(stagedChild, Path.Combine(outputRuntime, child));
                }
            }

            Directory.CreateDirectory(outputRuntime);
            var runtimeGen = Path.Combine(outputRuntime, "Gen");
            if (Directory.Exists(runtimeGen)) Directory.Delete(runtimeGen, true);
            FileSystemOps.CopyDirectory(outputGen, runtimeGen);

            SynthesizeConfig(request, outputRuntime);
            log("Workshop catalog retained; compiled Ulti databases applied afterward.");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (Directory.Exists(generatedBackup)) Directory.Delete(generatedBackup, true);
        }
    }

    private static void SynthesizeConfig(CombineRequest request, string outputRuntime)
    {
        var outputConfigPath = Path.Combine(outputRuntime, "Config.ini");
        if (!File.Exists(outputConfigPath))
        {
            throw new CombineException("WARNO did not create the output Config.ini.");
        }

        var output = IniDocument.Load(outputConfigPath);
        output.Set("Properties", "Name", request.OutputName);
        output.Set("Properties", "TagList", string.Join(',', request.OtherMod.Tags
            .Union(request.UltiMod.Tags, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        output.Set("Properties", "Version", Math.Max(request.OtherMod.Version, request.UltiMod.Version).ToString());
        output.Set("Properties", "DeckFormatVersion", Math.Max(
            request.OtherMod.DeckFormatVersion,
            request.UltiMod.DeckFormatVersion).ToString());
        if (request.UltiMod.ModGenVersion is int modGen)
        {
            output.Set("Properties", "ModGenVersion", modGen.ToString());
        }

        foreach (var pair in request.OtherMod.ConfigKeys)
        {
            if (output.Get("Config", pair.Key) is null)
            {
                output.Set("Config", pair.Key, pair.Value);
            }
        }

        foreach (var pair in request.UltiMod.ConfigKeys)
        {
            output.Set("Config", pair.Key, pair.Value);
        }

        output.Save(outputConfigPath);
    }

    private static void VerifyCompiledPlan(
        MergePreview preview,
        string staging,
        string generatedUltiRoot,
        string workshopRoot)
    {
        foreach (var decision in preview.Decisions)
        {
            var actual = FileSystemOps.SafeCombine(staging, decision.RelativePath);
            if (!File.Exists(actual))
            {
                throw new CombineException($"The staged package is missing {decision.RelativePath}.");
            }

            var expected = decision.Kind is MergeDecisionKind.UltiOnly or MergeDecisionKind.UltiOverride
                ? FileSystemOps.SafeCombine(Path.GetDirectoryName(generatedUltiRoot)!, decision.RelativePath)
                : FileSystemOps.SafeCombine(workshopRoot, decision.RelativePath);
            if (!File.Exists(expected)
                || !SourceDeltaAnalyzer.ComputeSha256(actual).Equals(
                    SourceDeltaAnalyzer.ComputeSha256(expected),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CombineException($"Precedence verification failed for {decision.RelativePath}.");
            }
        }
    }

    private static void VerifyResult(
        CombineRequest request,
        string outputSource,
        string outputRuntime,
        Action<string> log)
    {
        if (!File.Exists(Path.Combine(outputRuntime, "Config.ini")))
        {
            throw new CombineException("The combined runtime Config.ini is missing.");
        }

        var outputGen = Path.Combine(outputSource, "Gen");
        var runtimeGen = Path.Combine(outputRuntime, "Gen");
        if (!Directory.Exists(outputGen) || !Directory.Exists(runtimeGen))
        {
            throw new CombineException("The combined Gen output is incomplete.");
        }

        if (request.OtherMod.Kind == ModKind.EditableSource)
        {
            foreach (var decision in request.Preview.Decisions)
            {
                var actual = FileSystemOps.SafeCombine(outputSource, decision.RelativePath);
                if (decision.Kind == MergeDecisionKind.Delete)
                {
                    if (File.Exists(actual))
                    {
                        throw new CombineException($"Deletion verification failed for {decision.RelativePath}.");
                    }
                    continue;
                }

                var winnerRoot = decision.Kind is MergeDecisionKind.UltiOnly or MergeDecisionKind.UltiOverride
                    ? request.UltiMod.RootPath
                    : request.OtherMod.RootPath;
                VerifySameFile(actual, FileSystemOps.SafeCombine(winnerRoot, decision.RelativePath), decision.RelativePath);
            }
        }
        else
        {
            foreach (var decision in request.Preview.Decisions)
            {
                var actual = FileSystemOps.SafeCombine(outputRuntime, decision.RelativePath);
                var expectedRoot = decision.Kind is MergeDecisionKind.UltiOnly or MergeDecisionKind.UltiOverride
                    ? outputSource
                    : request.OtherMod.RootPath;
                VerifySameFile(actual, FileSystemOps.SafeCombine(expectedRoot, decision.RelativePath), decision.RelativePath);
            }
        }

        log($"Verified {request.Preview.Decisions.Count} merge decisions.");
    }

    private static void VerifySameFile(string actual, string expected, string relativePath)
    {
        if (!File.Exists(actual) || !File.Exists(expected)
            || !SourceDeltaAnalyzer.ComputeSha256(actual).Equals(
                SourceDeltaAnalyzer.ComputeSha256(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw new CombineException($"Final precedence verification failed for {relativePath}.");
        }
    }
}
