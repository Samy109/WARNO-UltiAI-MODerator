using WarnoModerator.Core;

var root = Path.Combine(Path.GetTempPath(), "moderator-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
try
{
    var paths = new WarnoPaths(root, root, Path.Combine(root, "Mods"), root, Path.Combine(root, "Runtime"));
    Write(paths.ModDataBaseZip, "game-v1");
    var source = Path.Combine(paths.ModsRoot, "Combined");
    var runtime = Path.Combine(paths.SavedModsRoot, "Combined");
    Write(Path.Combine(source, "Gen", "old"), "original source");
    Write(Path.Combine(runtime, "Gen", "data"), "original runtime");
    Write(Path.Combine(runtime, "Config.ini"), "config");
    var mod = new ModDescriptor("input", Path.Combine(root, "input"), ModKind.WorkshopCompiled, "1", 1, 1, 0, [], new Dictionary<string, string>());
    Write(Path.Combine(mod.RootPath, "Gen", "NDF", "data.ndfbin"), "version1");
    var fingerprints = new ModFingerprintService();
    var before = await fingerprints.ComputeAsync([mod, mod]);
    var health = new CombinationHealth();
    var state = new CombinedModState(1, "Combined", before[0], before[1], await health.RuntimeHashAsync(runtime), health.GameHash(paths));
    Check(CombinationHealth.CanRebuild(false, true, true), "unchanged inputs allow manual rebuild");
    Check(!CombinationHealth.CanRebuild(true, true, true), "busy rebuild disabled");
    Check((await health.CheckAsync(paths, state)).Contains("match"), "intact output recognized");
    Write(Path.Combine(runtime, "Gen", "data"), "damaged");
    Check((await health.CheckAsync(paths, state)).Contains("output changed"), "altered output detected");
    File.Delete(Path.Combine(runtime, "Config.ini"));
    Check((await health.CheckAsync(paths, state)).Contains("missing"), "missing runtime detected");
    Write(Path.Combine(runtime, "Config.ini"), "config");
    Write(paths.ModDataBaseZip, "game-v2");
    Check((await health.CheckAsync(paths, state)).Contains("WARNO"), "game build change detected");
    Check((await health.CheckAsync(paths, state with { RuntimeFingerprint = null })).Contains("Rebuild once"), "legacy records supported");
    Write(Path.Combine(mod.RootPath, "Gen", "NDF", "data.ndfbin"), "version2");
    var after = await fingerprints.ComputeAsync([mod, mod]);
    Check(!CombinedModStateStore.FingerprintMatches(before[0], after[0]), "downloaded patch changes fingerprint");
    try { CombinationHealth.VerifyInputs(before, after); throw new Exception("missed change"); }
    catch (CombineException) { Check(true, "mid-merge source change rejected"); }
    try { await fingerprints.ComputeAsync([mod with { RootPath = Path.Combine(root, "missing") }]); throw new Exception("missed missing input"); }
    catch (CombineException) { Check(true, "failed source check is not unchanged"); }
    var store = new CombinedModStateStore();
    store.Save(source, state);
    Check(store.TryLoad(source) == state, "health fingerprints persist");
    foreach (var file in new[] { "Python/python.exe", "Scripts/CreateNewMod.py", "Scripts/GenerateMod.py" }) Write(Path.Combine(paths.ModsRoot, "Utils", file), "stub");
    var request = new CombineRequest(paths, mod, mod, "Combined", new MergePreview("Combined", mod, mod, [], [], true));
    var runner = new FakeRunner(source, runtime);
    var service = new CombineService(new SourceDeltaAnalyzer(), runner);
    var oldSource = File.ReadAllText(Path.Combine(source, "Gen", "old"));
    var oldRuntime = File.ReadAllText(Path.Combine(runtime, "Gen", "data"));
    try { await service.RebuildAsync(request, finalize: _ => throw new CombineException("final input check failed")); throw new Exception("should fail"); }
    catch (CombineException ex) { Check(ex.Message == "final input check failed", "finalization reached after composition"); }
    Check(File.ReadAllText(Path.Combine(source, "Gen", "old")) == oldSource && File.ReadAllText(Path.Combine(runtime, "Gen", "data")) == oldRuntime, "failed finalization restores both outputs");
    Check(store.TryLoad(source) == state, "failed rebuild restores saved state");
    runner.FailGeneration = true;
    try { await service.RebuildAsync(request); throw new Exception("should fail"); }
    catch (CombineException) { Check(File.ReadAllText(Path.Combine(source, "Gen", "old")) == oldSource, "failed generation restores output"); }
    runner.FailGeneration = false;
    await service.RebuildAsync(request);
    Check(File.ReadAllText(Path.Combine(runtime, "Gen", "NDF", "data.ndfbin")) == "version2", "successful rebuild uses updated input");
    Check(!Directory.EnumerateDirectories(paths.ModsRoot).Any(p => p.Contains("backup-")), "successful rebuild removes backup");
    Console.WriteLine($"{checks} regression checks passed.");
}
finally { Directory.Delete(root, true); }

sealed class FakeRunner(string source, string runtime) : IProcessRunner
{
    public bool FailGeneration { get; set; }
    public Task<int> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        if (arguments.First().EndsWith("CreateNewMod.py")) Directory.CreateDirectory(source);
        else
        {
            if (FailGeneration) return Task.FromResult(1);
            Directory.CreateDirectory(Path.Combine(source, "Gen"));
            Directory.CreateDirectory(Path.Combine(runtime, "Gen"));
            File.WriteAllText(Path.Combine(runtime, "Config.ini"), "[Properties]\nModGenVersion = 1\n[Config]\n");
        }
        return Task.FromResult(0);
    }
}
