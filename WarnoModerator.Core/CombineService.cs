namespace WarnoModerator.Core;

public sealed class CombineService(
    SourceDeltaAnalyzer deltaAnalyzer,
    IProcessRunner processRunner)
{
    public async Task<CombineResult> CombineAsync(
        CombineRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<CombineProgress>? operationProgress = null,
        bool preserveIncompleteOutput = true,
        Func<CombineResult, Task>? finalize = null)
    {
        var logLines = new List<string>();
        void Log(string line)
        {
            lock (logLines) logLines.Add(line);
            progress?.Report(line);
        }
        void Report(int percent, string stage) =>
            operationProgress?.Report(new CombineProgress(Math.Clamp(percent, 0, 100), stage));

        Report(0, "Preparing");
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
        Report(5, "Local mod created");

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

            Log("Generating the local mod manifest with the installed WARNO build...");
            Report(10, "Generating with WARNO");
            await RunGenerateAsync(request.Paths, outputSource, request.OutputName, Log, cancellationToken)
                .ConfigureAwait(false);
            ValidateGeneratedCompatibility(request, outputRuntime);
            Report(40, "WARNO generation complete");

            IReadOnlyList<string>? compiledPaths = null;
            if (request.OtherMod.Kind == ModKind.WorkshopCompiled
                || request.UltiMod.Kind == ModKind.WorkshopCompiled)
            {
                Log($"Composing compiled payloads with {request.UltiMod.Name} precedence...");
                compiledPaths = ComposeCompiledPayload(request, outputSource, outputRuntime, Log, Report);
            }

            VerifyResult(request, outputSource, outputRuntime, compiledPaths, Log, Report);
            Report(99, "Checking inputs and recording result");
            var result = new CombineResult(outputSource, outputRuntime, logLines.ToArray());
            if (finalize is not null) await finalize(result).ConfigureAwait(false);
            Log("Combination and verification completed successfully.");
            Report(100, "Complete");
            return result;
        }
        catch
        {
            Log(preserveIncompleteOutput
                ? "The operation stopped. Input mods were not changed; the incomplete output was preserved for inspection."
                : "The operation stopped. Input mods were not changed.");
            throw;
        }
    }

    public async Task<CombineResult> RebuildAsync(
        CombineRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<CombineProgress>? operationProgress = null,
        Func<CombineResult, Task>? finalize = null)
    {
        var outputSource = Path.Combine(request.Paths.ModsRoot, request.OutputName);
        var outputRuntime = Path.Combine(request.Paths.SavedModsRoot, request.OutputName);
        if (!Directory.Exists(outputSource) && !Directory.Exists(outputRuntime))
        {
            throw new CombineException("The existing combined mod could not be found. Refresh the mod list and try again.");
        }

        VerifyModDirectoryWritable(request.Paths.ModsRoot);
        var sourceBackup = outputSource + ".warno-moderator-backup-" + Guid.NewGuid().ToString("N");
        var runtimeBackup = outputRuntime + ".warno-moderator-backup-" + Guid.NewGuid().ToString("N");
        var sourceExisted = Directory.Exists(outputSource);
        var runtimeExisted = Directory.Exists(outputRuntime);
        var sourceMoved = false;
        var runtimeMoved = false;

        try
        {
            progress?.Report("Safeguarding the existing combined mod...");
            if (Directory.Exists(outputSource))
            {
                Directory.Move(outputSource, sourceBackup);
                sourceMoved = true;
            }
            if (Directory.Exists(outputRuntime))
            {
                Directory.Move(outputRuntime, runtimeBackup);
                runtimeMoved = true;
            }

            var result = await CombineAsync(
                request,
                progress,
                cancellationToken,
                operationProgress,
                preserveIncompleteOutput: false,
                finalize: finalize).ConfigureAwait(false);

            TryDeleteDirectory(sourceBackup);
            TryDeleteDirectory(runtimeBackup);
            progress?.Report("The previous combined mod was replaced successfully.");
            return result;
        }
        catch
        {
            progress?.Report("Rebuild failed; restoring the previous combined mod...");
            if (sourceMoved || !sourceExisted) DeleteDirectoryIfExists(outputSource);
            if (runtimeMoved || !runtimeExisted) DeleteDirectoryIfExists(outputRuntime);
            if (sourceMoved && Directory.Exists(sourceBackup))
            {
                Directory.Move(sourceBackup, outputSource);
            }
            if (runtimeMoved && Directory.Exists(runtimeBackup))
            {
                Directory.Move(runtimeBackup, outputRuntime);
            }
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

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException)
        {
            // A successful rebuild remains usable if Windows temporarily retains a backup file handle.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the uniquely named backup in place rather than failing a completed rebuild.
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
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

    private static void ValidateGeneratedCompatibility(CombineRequest request, string outputRuntime)
    {
        var configPath = Path.Combine(outputRuntime, "Config.ini");
        if (!File.Exists(configPath))
        {
            throw new CombineException("WARNO did not generate the local mod compatibility manifest.");
        }

        var generatedConfig = IniDocument.Load(configPath);
        var currentModGen = generatedConfig.GetInt("Properties", "ModGenVersion", -1);
        if (currentModGen < 0)
        {
            throw new CombineException("WARNO generated a compatibility manifest without a ModGen revision.");
        }

        foreach (var input in new[] { request.OtherMod, request.UltiMod })
        {
            if (input.ModGenVersion is int inputModGen && inputModGen != currentModGen)
            {
                throw new CombineException(
                    $"{input.Name} uses ModGen {inputModGen}, but the installed WARNO build requires {currentModGen}. " +
                    "Update the Workshop subscription before combining it.");
            }
        }
    }

    private IReadOnlyList<string> ComposeCompiledPayload(
        CombineRequest request,
        string outputSource,
        string outputRuntime,
        Action<string> log,
        Action<int, string> report)
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

            var baseFiles = MergePlanner.EnumerateRuntimeFiles(otherRuntimeRoot).ToArray();
            var basePaths = baseFiles.Select(x => x.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preserveOtherComponents = baseFiles.Any(x => MergePlanner.IsUiComponents(x.RelativePath));
            var overlayFiles = MergePlanner.EnumerateUltiOverlayFiles(Path.Combine(priorityRuntimeRoot, "Gen"))
                .Where(x => !MergePlanner.IsUiComponents(x.RelativePath) || !basePaths.Contains(x.RelativePath))
                .ToArray();
            var expectedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in baseFiles)
            {
                expectedFiles[item.RelativePath] = item.FullPath;
            }
            foreach (var item in overlayFiles)
            {
                expectedFiles[item.RelativePath] = item.FullPath;
            }
            var copyCount = baseFiles.Length + overlayFiles.Length;
            var copied = 0;

            foreach (var item in baseFiles)
            {
                FileSystemOps.CopyFileAtomic(
                    item.FullPath,
                    FileSystemOps.SafeCombine(staging, item.RelativePath));
                copied++;
                ReportFileProgress(report, 42, 63, copied, copyCount, "Composing files");
            }

            var outputGen = Path.Combine(outputSource, "Gen");
            var priorityGen = Path.Combine(priorityRuntimeRoot, "Gen");
            foreach (var item in overlayFiles)
            {
                FileSystemOps.CopyFileAtomic(
                    item.FullPath,
                    FileSystemOps.SafeCombine(staging, item.RelativePath));
                copied++;
                ReportFileProgress(report, 42, 63, copied, copyCount, "Composing files");
            }

            var stagedCatalog = Path.Combine(staging, "Gen", "ResourceFile", "Catalog.cat");
            if (!File.Exists(stagedCatalog))
            {
                var priorityCatalog = Path.Combine(priorityGen, "ResourceFile", "Catalog.cat");
                if (File.Exists(priorityCatalog))
                {
                    FileSystemOps.CopyFileAtomic(priorityCatalog, stagedCatalog);
                    expectedFiles["Gen\\ResourceFile\\Catalog.cat"] = priorityCatalog;
                }
            }

            VerifyCompiledPlan(expectedFiles, staging, report);

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
            report(78, "Copying runtime assets");

            Directory.CreateDirectory(outputRuntime);
            var runtimeGen = Path.Combine(outputRuntime, "Gen");
            if (Directory.Exists(runtimeGen)) Directory.Delete(runtimeGen, true);
            FileSystemOps.CopyDirectory(outputGen, runtimeGen);
            report(85, "Writing compatibility manifest");

            SynthesizeConfig(request, outputRuntime, preserveOtherComponents);
            log(preserveOtherComponents
                ? "Other mod UI components and base catalog retained; Ulti precedence applied to remaining compiled databases."
                : "Base catalog retained; compiled Ulti databases applied afterward.");
            return expectedFiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (Directory.Exists(generatedBackup)) Directory.Delete(generatedBackup, true);
        }
    }

    private static void SynthesizeConfig(CombineRequest request, string outputRuntime, bool preserveOtherComponents)
    {
        var outputConfigPath = Path.Combine(outputRuntime, "Config.ini");
        Directory.CreateDirectory(outputRuntime);
        if (!File.Exists(outputConfigPath))
        {
            throw new CombineException("WARNO's generated local mod Config.ini is missing.");
        }

        var output = IniDocument.Load(outputConfigPath);
        output.Set("Properties", "Name", request.OutputName);
        output.Set("Properties", "TagList", string.Join(',', request.OtherMod.Tags
            .Union(request.UltiMod.Tags, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        output.Set("Properties", "ID", "0");
        output.Set("Properties", "DeckFormatVersion", Math.Max(
            request.OtherMod.DeckFormatVersion,
            request.UltiMod.DeckFormatVersion).ToString());

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

        if (preserveOtherComponents && request.OtherMod.ConfigKeys.TryGetValue("UI/Components", out var componentsFingerprint))
        {
            output.Set("Config", "UI/Components", componentsFingerprint);
        }

        output.Save(outputConfigPath);
    }

    private static void VerifyCompiledPlan(
        IReadOnlyDictionary<string, string> expectedFiles,
        string staging,
        Action<int, string> report)
    {
        var index = 0;
        foreach (var expectedFile in expectedFiles.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var actual = FileSystemOps.SafeCombine(staging, expectedFile.Key);
            if (!File.Exists(actual))
            {
                throw new CombineException($"The staged package is missing {expectedFile.Key}.");
            }

            if (!File.Exists(expectedFile.Value)
                || !SourceDeltaAnalyzer.ComputeSha256(actual).Equals(
                    SourceDeltaAnalyzer.ComputeSha256(expectedFile.Value),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CombineException($"Precedence verification failed for {expectedFile.Key}.");
            }

            index++;
            ReportFileProgress(report, 64, 76, index, expectedFiles.Count, "Verifying precedence");
        }
    }

    private static void VerifyResult(
        CombineRequest request,
        string outputSource,
        string outputRuntime,
        IReadOnlyList<string>? compiledPaths,
        Action<string> log,
        Action<int, string> report)
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
            for (var index = 0; index < request.Preview.Decisions.Count; index++)
            {
                var decision = request.Preview.Decisions[index];
                var actual = FileSystemOps.SafeCombine(outputSource, decision.RelativePath);
                if (decision.Kind == MergeDecisionKind.Delete)
                {
                    if (File.Exists(actual))
                    {
                        throw new CombineException($"Deletion verification failed for {decision.RelativePath}.");
                    }
                    ReportFileProgress(report, 86, 99, index + 1, request.Preview.Decisions.Count, "Final verification");
                    continue;
                }

                var winnerRoot = decision.Kind is MergeDecisionKind.UltiOnly or MergeDecisionKind.UltiOverride
                    ? request.UltiMod.RootPath
                    : request.OtherMod.RootPath;
                VerifySameFile(actual, FileSystemOps.SafeCombine(winnerRoot, decision.RelativePath), decision.RelativePath);
                ReportFileProgress(report, 86, 99, index + 1, request.Preview.Decisions.Count, "Final verification");
            }
        }
        else
        {
            var paths = compiledPaths
                ?? throw new CombineException("The compiled payload verification plan is missing.");
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                var actual = FileSystemOps.SafeCombine(outputRuntime, path);
                VerifySameFile(actual, FileSystemOps.SafeCombine(outputSource, path), path);
                ReportFileProgress(report, 86, 99, index + 1, paths.Count, "Final verification");
            }
        }

        log($"Verified {(compiledPaths?.Count ?? request.Preview.Decisions.Count)} merge decisions.");
    }

    private static void ReportFileProgress(
        Action<int, string> report,
        int startPercent,
        int endPercent,
        int completed,
        int total,
        string stage)
    {
        if (total <= 0 || (completed != total && completed % 25 != 0))
        {
            return;
        }

        var percent = startPercent + (int)Math.Round(
            (endPercent - startPercent) * (completed / (double)total));
        report(percent, stage);
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
