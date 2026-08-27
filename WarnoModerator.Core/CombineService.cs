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

        VerifyModDirectoryWritable(request.Paths.ModsRoot);
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

            if (request.UltiMod.Kind == ModKind.EditableSource)
            {
                Log($"Applying editable {request.UltiMod.Name} with highest precedence...");
                ApplySourceDelta(deltaAnalyzer.Analyze(request.UltiMod), outputSource);
            }

            var requiresGeneration = request.OtherMod.Kind == ModKind.EditableSource
                                     || request.UltiMod.Kind == ModKind.EditableSource;
            if (requiresGeneration)
            {
                Log("Generating the editable source payload with WARNO...");
                await RunGenerateAsync(request.Paths, outputSource, request.OutputName, Log, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (request.OtherMod.Kind == ModKind.WorkshopCompiled
                || request.UltiMod.Kind == ModKind.WorkshopCompiled)
            {
                Log($"Composing compiled payloads with {request.UltiMod.Name} precedence...");
                ComposeCompiledPayload(request, outputSource, outputRuntime, Log);
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
        var python = Path.Combine(request.Paths.ModsRoot, "Utils", "Python", "python.exe");
        var script = Path.Combine(request.Paths.ModsRoot, "Utils", "Scripts", "CreateNewMod.py");
        if (!File.Exists(python) || !File.Exists(script))
        {
            throw new CombineException("WARNO's CreateNewMod tools were not found under WARNO\\Mods\\Utils.");
        }

        var exitCode = await processRunner.RunAsync(
            python,
            [script, request.OutputName],
            request.Paths.ModsRoot,
            log,
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new CombineException($"CreateNewMod.bat failed with exit code {exitCode}.");
        }
    }

    private static void VerifyModDirectoryWritable(string modsRoot)
    {
        var probe = Path.Combine(modsRoot, $".warno-moderator-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new CombineException(
                "WARNO\\Mods is not writable. Close the app, right-click WARNO-UltiAI-MODerator.exe, and choose 'Run as administrator'. If it still fails, verify that Windows Security or antivirus is not blocking the app.");
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
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

    private void ComposeCompiledPayload(
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
            var otherRuntimeRoot = request.OtherMod.Kind == ModKind.WorkshopCompiled
                ? request.OtherMod.RootPath
                : outputRuntime;
            var priorityRuntimeRoot = request.UltiMod.Kind == ModKind.WorkshopCompiled
                ? request.UltiMod.RootPath
                : outputSource;

            foreach (var item in MergePlanner.EnumerateRuntimeFiles(otherRuntimeRoot))
            {
                FileSystemOps.CopyFileAtomic(
                    item.FullPath,
                    FileSystemOps.SafeCombine(staging, item.RelativePath));
            }

            var outputGen = Path.Combine(outputSource, "Gen");
            var priorityGen = Path.Combine(priorityRuntimeRoot, "Gen");
            foreach (var item in MergePlanner.EnumerateUltiOverlayFiles(priorityGen))
            {
                FileSystemOps.CopyFileAtomic(
                    item.FullPath,
                    FileSystemOps.SafeCombine(staging, item.RelativePath));
            }

            var stagedCatalog = Path.Combine(staging, "Gen", "ResourceFile", "Catalog.cat");
            if (!File.Exists(stagedCatalog))
            {
                var priorityCatalog = Path.Combine(priorityGen, "ResourceFile", "Catalog.cat");
                if (File.Exists(priorityCatalog))
                {
                    FileSystemOps.CopyFileAtomic(priorityCatalog, stagedCatalog);
                }
            }

            VerifyCompiledPlan(request.Preview, staging, priorityRuntimeRoot, otherRuntimeRoot);

            if (Directory.Exists(outputGen)) Directory.Move(outputGen, generatedBackup);
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
            log("Base catalog retained; compiled Ulti databases applied afterward.");
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
        Directory.CreateDirectory(outputRuntime);
        var output = File.Exists(outputConfigPath)
            ? IniDocument.Load(outputConfigPath)
            : new IniDocument();
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
        string priorityRoot,
        string otherRoot)
    {
        foreach (var decision in preview.Decisions)
        {
            var actual = FileSystemOps.SafeCombine(staging, decision.RelativePath);
            if (!File.Exists(actual))
            {
                throw new CombineException($"The staged package is missing {decision.RelativePath}.");
            }

            var expected = decision.Kind is MergeDecisionKind.UltiOnly or MergeDecisionKind.UltiOverride
                ? FileSystemOps.SafeCombine(priorityRoot, decision.RelativePath)
                : FileSystemOps.SafeCombine(otherRoot, decision.RelativePath);
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

        if (request.OtherMod.Kind == ModKind.EditableSource
            && request.UltiMod.Kind == ModKind.EditableSource)
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
                VerifySameFile(actual, FileSystemOps.SafeCombine(outputSource, decision.RelativePath), decision.RelativePath);
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
