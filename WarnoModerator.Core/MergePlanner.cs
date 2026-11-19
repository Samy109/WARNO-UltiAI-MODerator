using System.Collections.Concurrent;
using WarnoModerator.Core.Ndf;

namespace WarnoModerator.Core;

/// <summary>A compiled payload taking part in the merge: a Workshop mod, or WARNO's generation of editable mods.</summary>
public sealed record CompiledInput(string Name, string Root, IReadOnlyDictionary<string, string> ConfigKeys);

public sealed class MergePlanner(SourceDeltaAnalyzer deltaAnalyzer)
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <param name="mods">Selected mods in priority order; the first is the primary mod.</param>
    public MergePreview CreatePreview(WarnoPaths paths, IReadOnlyList<ModDescriptor> mods,
        string outputName, bool allowExistingOutput = false, CancellationToken cancellationToken = default)
    {
        ValidateOutputName(outputName);
        ValidateInputs(paths, mods, cancellationToken);
        foreach (var root in new[] { paths.ModsRoot, paths.SavedModsRoot })
        {
            var output = Path.Combine(root, outputName);
            if (mods.Any(m => SamePath(output, m.RootPath)))
                throw new CombineException("The output must not replace an input mod.");
            if (!allowExistingOutput && (Directory.Exists(output) || File.Exists(output)))
                throw new CombineException($"An output named '{outputName}' already exists.");
        }
        if (mods.All(m => m.Kind == ModKind.EditableSource))
            return new MergePreview(outputName, mods, PlanSourceMerge(mods, cancellationToken), []);

        // Editable mods are previewed from their own last generation; the build regenerates them together.
        var inputs = mods.Select(m => new CompiledInput(m.Name,
            m.Kind == ModKind.WorkshopCompiled ? m.RootPath : Path.Combine(paths.SavedModsRoot, m.Name), m.ConfigKeys)).ToArray();
        var vanilla = VanillaDatabases.Open(paths.WarnoRoot, CompiledModGen(mods));
        return CreateCompiledPlan(outputName, mods, inputs, vanilla, null, cancellationToken,
            mods.Any(m => m.Kind == ModKind.EditableSource), sourceBaseline: SourceBaseline.TryOpen(paths.ModDataBaseZip));
    }

    internal static int? CompiledModGen(IEnumerable<ModDescriptor> mods) =>
        mods.FirstOrDefault(m => m.Kind == ModKind.WorkshopCompiled)?.ModGenVersion;

    internal static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static void ValidateInputs(WarnoPaths paths, IReadOnlyList<ModDescriptor> mods, CancellationToken token)
    {
        if (mods.Count < 2) throw new CombineException("Add at least two mods to combine.");
        for (var i = 0; i < mods.Count; i++)
            for (var j = i + 1; j < mods.Count; j++)
                if (SamePath(mods[i].RootPath, mods[j].RootPath)) throw new CombineException($"{mods[i].Name} is selected twice.");
        string? currentBase = null;
        foreach (var mod in mods)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(mod.RootPath)) throw new CombineException($"Source mod folder is missing: {mod.RootPath}");
            if (mod.Kind == ModKind.WorkshopCompiled)
            {
                if (mod.ModGenVersion is null or < 0)
                    throw new CombineException($"{mod.Name} has no readable ModGenVersion. Refresh its Workshop subscription before combining it.");
            }
            else
            {
                currentBase ??= SourceDeltaAnalyzer.ComputeSha256(paths.ModDataBaseZip, token);
                if (SourceDeltaAnalyzer.ComputeSha256(mod.BaseZipPath, token) != currentBase)
                    throw new CombineException($"{mod.Name} is based on an older WARNO version. Run its UpdateMod.bat first.");
            }
        }
        var compiled = mods.Where(m => m.Kind == ModKind.WorkshopCompiled).ToArray();
        var mismatch = compiled.FirstOrDefault(m => m.ModGenVersion != compiled[0].ModGenVersion);
        if (mismatch is not null)
            throw new CombineException($"{compiled[0].Name} uses ModGen {compiled[0].ModGenVersion}, but {mismatch.Name} uses {mismatch.ModGenVersion}.");
    }

    /// <summary>File-level merge of editable sources: for each changed file the highest-priority mod wins.</summary>
    internal IReadOnlyList<MergeDecision> PlanSourceMerge(IReadOnlyList<ModDescriptor> mods, CancellationToken token)
    {
        var deltas = mods.Where(m => m.Kind == ModKind.EditableSource)
            .Select(m => (Mod: m, Changes: deltaAnalyzer.Analyze(m, token).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        var decisions = new List<MergeDecision>();
        foreach (var path in deltas.SelectMany(d => d.Changes.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var providers = deltas.Where(d => d.Changes.ContainsKey(path)).Select(d => (d.Mod, Change: d.Changes[path])).ToArray();
            var (winner, change) = providers[0];
            var identical = providers.Length > 1 && providers.All(p => p.Change.Kind == change.Kind
                && (change.Kind == DeltaKind.Deleted || SameContent(p.Change.SourcePath!, change.SourcePath!, token)));
            var others = providers.Skip(1).Select(p => p.Mod.Name).ToArray();
            var detail = identical ? "All mods make the exact same source change (no conflict)."
                : others.Length > 0 ? $"Several mods modify this source file; {winner.Name} has priority over {string.Join(", ", others)}."
                : change.Kind == DeltaKind.Deleted ? $"Deleted relative to vanilla base.zip by {winner.Name}."
                : $"{change.Kind} source file from {winner.Name}.";
            decisions.Add(new MergeDecision(path,
                change.Kind == DeltaKind.Deleted ? MergeDecisionKind.Delete : others.Length > 0 ? MergeDecisionKind.Override : MergeDecisionKind.Single,
                winner.Name, detail, change.SourcePath, identical, others.Length > 0));
        }
        return decisions;
    }

    /// <param name="inputs">Compiled payloads in priority order (primary first).</param>
    /// <param name="mergeDirectory">Where merged databases are written; null plans without writing (preview).</param>
    /// <param name="generated">WARNO's own generation of the output, when no input already represents it. It takes
    /// part with the lowest priority, so the output keeps the files every generated mod has under its own name.</param>
    internal static MergePreview CreateCompiledPlan(string outputName, IReadOnlyList<ModDescriptor> mods,
        IReadOnlyList<CompiledInput> inputs, VanillaDatabases vanilla, string? mergeDirectory, CancellationToken token,
        bool provisional = false, CompiledInput? generated = null, SourceBaseline? sourceBaseline = null)
    {
        var contributors = generated is null ? inputs : [.. inputs, generated];
        var listings = contributors.Select(input => EnumerateRuntimeFiles(input.Root)
            .Where(x => input != generated || IsOutputOwned(x.RelativePath, outputName)).ToArray()).ToArray();
        var transactionLayout = vanilla.TransactionLayout
            || listings.Any(l => l.Any(x => NdfLayout.IsTransactionDatabase(x.RelativePath)));
        var layoutNotes = new List<string>();
        var files = contributors.Select((input, i) =>
            MapToLayout(input.Name, listings[i], transactionLayout, layoutNotes)).ToArray();
        for (var i = 0; i < inputs.Count; i++)
            if (files[i].Count == 0 && !provisional)
                throw new CombineException($"{inputs[i].Name} has no runtime payload.");

        var paths = files.SelectMany(f => f.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var decisions = new ConcurrentDictionary<string, MergeDecision>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new ConcurrentDictionary<string, IReadOnlyList<MergeDecision>>(StringComparer.OrdinalIgnoreCase);
        var warnings = new ConcurrentBag<string>();
        var payloadSizes = contributors.Select((input, i) => (input.Name, files[i].Count)).ToDictionary(x => x.Name, x => x.Count);
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
        Parallel.ForEach(paths, options, path =>
        {
            var providers = Enumerable.Range(0, contributors.Count).Where(i => files[i].ContainsKey(path))
                .Select(i => (Input: contributors[i], File: files[i][path])).ToArray();
            decisions[path] = Decide(path, providers, payloadSizes, vanilla, sourceBaseline, mergeDirectory, conflicts, warnings, token);
        });

        var tags = contributors.Select(input => EnumerateTagFiles(input.Root)
            .Where(x => input != generated || IsOutputOwned(x.RelativePath, outputName))
            .ToDictionary(x => x.RelativePath, x => x.FullPath, StringComparer.OrdinalIgnoreCase)).ToArray();
        var ordered = paths.Select(p => decisions[p])
            .SelectMany(d => TagFor(d, contributors, tags) is { } tag ? new[] { d, tag } : [d]).ToArray();
        var valueConflicts = paths.Where(conflicts.ContainsKey).SelectMany(p => conflicts[p]).ToArray();
        var notes = new List<string>();
        if (provisional) notes.Add("Provisional preview: editable SDK input will be compiled by WARNO during the build; the final report will reflect the exact compiled files.");
        if (!vanilla.Available)
            notes.Add($"WARNO's vanilla databases (Glad.dat or Transactions.dat) were not found ({vanilla.Searched}), so overlapping databases fall back to whole-file priority.");
        var merged = ordered.Count(d => d.Kind == MergeDecisionKind.Merged && !d.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        if (merged > 0)
            notes.Add($"{merged:N0} database(s) were changed by several mods and are delta merged against vanilla; {valueConflicts.Length:N0} value conflict(s) resolved by priority ({string.Join(" > ", inputs.Select(i => i.Name))}).");
        notes.AddRange(layoutNotes);
        notes.AddRange(warnings.Order(StringComparer.Ordinal));
        return new MergePreview(outputName, mods, ordered, notes, provisional, valueConflicts);
    }

    /// <summary>
    /// Puts one mod's databases in the installed build's layout. On a .txn build, a mod published before the November
    /// 2026 update ships only .ndfbin databases; WARNO looks for them under their .txn names and, finding none, fails
    /// with "No writable component found" (for example 'ZZ:/NDF/Localisation/&lt;Mod&gt;/DicoDef.txn'). Their content
    /// is the same container, so they are renamed. A mod that already ships .txn databases was rebuilt after the
    /// update, and any .ndfbin it still carries is a stale leftover WARNO no longer reads, so those are left out.
    /// </summary>
    private static Dictionary<string, string> MapToLayout(string inputName,
        IReadOnlyList<(string RelativePath, string FullPath)> listing, bool transactionLayout, List<string> notes)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rebuilt = listing.Any(x => NdfLayout.IsTransactionDatabase(x.RelativePath));
        int converted = 0, stale = 0;
        foreach (var (relativePath, fullPath) in listing)
        {
            if (!transactionLayout || !NdfLayout.IsLegacyDatabase(relativePath)) map[relativePath] = fullPath;
            else if (rebuilt) stale++;
            else { map[NdfLayout.ToTransactionPath(relativePath)] = fullPath; converted++; }
        }
        if (converted > 0)
            notes.Add($"{inputName} was published before WARNO's November 2026 database format change; its {converted:N0} database(s) were renamed to the new .txn layout so WARNO can load them.");
        if (stale > 0)
            notes.Add($"{inputName} still carries {stale:N0} .ndfbin database(s) from before WARNO's format change; WARNO no longer reads them, so they were left out.");
        return map;
    }

    private static readonly string[] FileManifests = ["Gen\\DeclaredFiles.txt", "Gen\\UsedFiles.txt"];

    // A generated mod's packs, dictionaries and their database entries sit under its name; the asset catalog and
    // file lists register them, so those are merged with the inputs' rather than replaced.
    private static bool IsOutputOwned(string path, string outputName) =>
        FileManifests.Contains(path, StringComparer.OrdinalIgnoreCase)
        || path.Equals("Gen\\ResourceFile\\Catalog.cat", StringComparison.OrdinalIgnoreCase)
        || path.Split('\\').Any(segment => segment.Equals(outputName, StringComparison.OrdinalIgnoreCase)
            || segment.Equals(outputName + ".ndfbin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(outputName + ".txn", StringComparison.OrdinalIgnoreCase));

    private static bool IsGeneratorByproduct(string path) =>
        path.StartsWith("Gen\\Intermediate\\", StringComparison.OrdinalIgnoreCase)
        || path.Equals("Gen\\GenerationReport.txt", StringComparison.OrdinalIgnoreCase)
        || path.Equals("Gen\\DecorSetAssets.ndf", StringComparison.OrdinalIgnoreCase);

    private static MergeDecision Decide(string path, (CompiledInput Input, string File)[] providers,
        IReadOnlyDictionary<string, int> payloadSizes, VanillaDatabases vanilla, SourceBaseline? sourceBaseline,
        string? mergeDirectory, ConcurrentDictionary<string, IReadOnlyList<MergeDecision>> conflicts,
        ConcurrentBag<string> warnings, CancellationToken token)
    {
        var (primary, primaryFile) = providers[0];
        if (providers.Length == 1)
            return new MergeDecision(path, MergeDecisionKind.Single, primary.Name, $"Only {primary.Name} provides this file.", primaryFile);
        if (providers.Skip(1).All(p => SameContent(p.File, primaryFile, token)))
            return new MergeDecision(path, MergeDecisionKind.Single, primary.Name, "Identical in every mod that provides it (no conflict).",
                primaryFile, Identical: true, Overlap: true);

        var others = string.Join(", ", providers.Skip(1).Select(p => p.Input.Name));
        var overrideDecision = new MergeDecision(path, MergeDecisionKind.Override, primary.Name,
            $"Atomic file: {primary.Name} has priority over {others}.", primaryFile, Overlap: true);
        if (FileManifests.Contains(path, StringComparer.OrdinalIgnoreCase))
            return MergeManifest(path, providers, mergeDirectory);
        if (IsGeneratorByproduct(path))
        {
            // Build caches and reports describe a mod's own asset generation; keep them with the largest payload.
            var largest = providers.MaxBy(p => payloadSizes[p.Input.Name]);
            return new MergeDecision(path, MergeDecisionKind.Override, largest.Input.Name,
                $"Generator output taken from {largest.Input.Name}, the mod with the largest compiled payload.", largest.File, Overlap: true);
        }
        if (!IsNdfBinary(primaryFile))
        {
            // A mod that ships vanilla's own source file has not changed it, so it must neither win nor conflict.
            var changed = sourceBaseline is null ? providers
                : providers.Where(p => !sourceBaseline.IsVanilla(path, p.File, token)).ToArray();
            if (changed.Length == 0 || changed.Length == providers.Length) return overrideDecision;
            var winner = changed[0];
            return changed.Length == 1
                ? new MergeDecision(path, MergeDecisionKind.Single, winner.Input.Name,
                    $"Only {winner.Input.Name} changes this file from vanilla; the others ship it unchanged.", winner.File, Overlap: true)
                : new MergeDecision(path, MergeDecisionKind.Override, winner.Input.Name,
                    $"Atomic file: {winner.Input.Name} has priority over {string.Join(", ", changed.Skip(1).Select(p => p.Input.Name))}.",
                    winner.File, Overlap: true);
        }

        var baseBytes = vanilla.TryRead(path);
        NdfDatabase? baseDb = null;
        try
        {
            baseDb = baseBytes is null ? null : NdfDatabase.Load(baseBytes);
            if (baseDb is null && !path.EndsWith("Catalog.cat", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{path} has no vanilla counterpart; {primary.Name}'s copy is used over {others}.");
                return overrideDecision;
            }
            var variants = providers.Select(p => new NdfVariant(p.Input.Name, NdfDatabase.Load(File.ReadAllBytes(p.File)))).ToArray();
            var result = NdfMerger.Merge(baseDb, variants, token);
            if (result.ChangedBy.Count == 0)
                return new MergeDecision(path, MergeDecisionKind.Single, primary.Name, "No mod changes this database from vanilla.",
                    primaryFile, Identical: true, Overlap: true);
            if (result.ChangedBy.Count == 1 && result.Conflicts.Count == 0 && baseDb is not null)
            {
                var only = providers.First(p => p.Input.Name == result.ChangedBy[0]);
                return new MergeDecision(path, MergeDecisionKind.Single, only.Input.Name,
                    $"Only {only.Input.Name} changes this database; the others ship it unchanged from vanilla.", only.File, Overlap: true);
            }

            string? output = null;
            if (mergeDirectory is not null)
            {
                output = FileSystemOps.SafeCombine(mergeDirectory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllBytes(output, result.Database.Save());
            }
            conflicts[path] = result.Conflicts.Select(c => new MergeDecision($"{path} › {c.Path}", MergeDecisionKind.ValueConflict,
                c.Winner, c.Overridden.Count > 0
                    ? $"{c.Winner} has priority over {string.Join(", ", c.Overridden)}: {c.WinnerValue}"
                    : $"{c.Winner}: {c.WinnerValue}")).ToArray();
            var detail = $"Delta merged: {result.ChangedObjects:N0} object(s) changed by {string.Join(", ", result.ChangedBy)}"
                + (result.Conflicts.Count > 0 ? $"; {result.Conflicts.Count:N0} value conflict(s) resolved by priority." : "; no conflicts.");
            return new MergeDecision(path, MergeDecisionKind.Merged, string.Join(" + ", result.ChangedBy), detail, output, Overlap: true);
        }
        catch (NdfFormatException ex)
        {
            // Whole-file fallback: a mod that ships vanilla's copy has changed nothing, so it must not outrank a mod that did.
            var changed = ChangedFromVanilla(providers, baseBytes, baseDb);
            var winner = changed[0];
            warnings.Add($"{path} could not be delta merged ({ex.Message}); {winner.Input.Name}'s copy is used"
                + (changed.Length < providers.Length ? ", the others ship vanilla's copy." : $" over {others}."));
            return changed.Length == 1 && providers.Length > 1
                ? new MergeDecision(path, MergeDecisionKind.Single, winner.Input.Name,
                    $"Only {winner.Input.Name} changes this database; the others ship it unchanged from vanilla.", winner.File, Overlap: true)
                : new MergeDecision(path, MergeDecisionKind.Override, winner.Input.Name,
                    $"Atomic file: {winner.Input.Name} has priority over {string.Join(", ", changed.Skip(1).Select(p => p.Input.Name))}.", winner.File, Overlap: true);
        }
    }

    /// <summary>Providers whose database differs from vanilla's, in priority order; all of them when vanilla is unknown or none differ.</summary>
    private static (CompiledInput Input, string File)[] ChangedFromVanilla((CompiledInput Input, string File)[] providers,
        byte[]? baseBytes, NdfDatabase? baseDb)
    {
        if (baseBytes is null) return providers;
        var changed = providers.Where(p => !IsUnchanged(p.File, baseBytes, baseDb)).ToArray();
        return changed.Length == 0 ? providers : changed;
    }

    private static bool IsUnchanged(string file, byte[] baseBytes, NdfDatabase? baseDb)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.AsSpan().SequenceEqual(baseBytes)) return true;
        if (baseDb is null) return false;
        try
        {
            var db = NdfDatabase.Load(bytes);
            return db.TopLevel.Count == baseDb.TopLevel.Count && db.TopLevel.Zip(baseDb.TopLevel)
                .All(t => t.First.ExportPath == t.Second.ExportPath && NdfValue.Same(t.First.Value, t.Second.Value));
        }
        catch (NdfFormatException) { return false; }
    }

    /// <summary>
    /// Since WARNO's November 2026 database format change, each generated file ships with a <c>.tag</c> companion
    /// (DicoDef.txn.tag, Mesh_All.spk.tag, ...), and WARNO only mounts a database whose tag is present: without it,
    /// loading stops with "No writable component found: 'ZZ:/NDF/Localisation/&lt;Mod&gt;/DicoDef.txn'". A tag
    /// follows its file: it comes from the mod whose copy was selected, or for a delta-merged database from the
    /// highest-priority mod that changed it.
    /// </summary>
    private static MergeDecision? TagFor(MergeDecision decision, IReadOnlyList<CompiledInput> contributors,
        IReadOnlyList<Dictionary<string, string>> tags)
    {
        if (decision.SourcePath is null && decision.Kind != MergeDecisionKind.Merged) return null;
        var tagPath = decision.RelativePath + ".tag";
        var winners = decision.Winner.Split(" + ");
        var providers = Enumerable.Range(0, contributors.Count).Where(i => tags[i].ContainsKey(tagPath)).ToArray();
        if (providers.Length == 0) return null;
        var chosen = providers.FirstOrDefault(i => winners.Contains(contributors[i].Name), providers[0]);
        var name = contributors[chosen].Name;
        return new MergeDecision(tagPath, MergeDecisionKind.Single, name,
            $"Generation tag of {decision.RelativePath}, taken from {name}.", tags[chosen][tagPath], Overlap: decision.Overlap);
    }

    /// <summary>Generated file lists (DeclaredFiles.txt, UsedFiles.txt) become the union of every mod's lines.</summary>
    private static MergeDecision MergeManifest(string path, (CompiledInput Input, string File)[] providers, string? mergeDirectory)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, file) in providers)
            foreach (var line in File.ReadAllLines(file))
                if (line.Length > 0 && seen.Add(line)) lines.Add(line);
        string? output = null;
        if (mergeDirectory is not null)
        {
            output = FileSystemOps.SafeCombine(mergeDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, string.Join("\n", lines) + "\n");
        }
        return new MergeDecision(path, MergeDecisionKind.Merged, string.Join(" + ", providers.Select(p => p.Input.Name)),
            $"Union of {lines.Count:N0} entries listed by every mod.", output, Overlap: true);
    }

    private static bool IsNdfBinary(string path)
    {
        Span<byte> header = stackalloc byte[0x2C];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == header.Length && NdfContainer.IsNdfBinary(header);
    }

    private static bool SameContent(string left, string right, CancellationToken token) =>
        new FileInfo(left).Length == new FileInfo(right).Length
        && SourceDeltaAnalyzer.ComputeSha256(left, token) == SourceDeltaAnalyzer.ComputeSha256(right, token);

    public static void ValidateOutputName(string outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName))
        {
            throw new CombineException("Enter an output name.");
        }

        if (!string.Equals(outputName, outputName.Trim(), StringComparison.Ordinal)
            || outputName is "." or ".."
            || outputName.EndsWith('.')
            || outputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || outputName.Contains('%')
            || outputName.Length > 120
            || ReservedWindowsNames.Contains(outputName.Split('.')[0]))
        {
            throw new CombineException("The output name is not a valid Windows folder name.");
        }
    }

    internal static IEnumerable<(string RelativePath, string FullPath)> EnumerateRuntimeFiles(string root)
    {
        foreach (var child in new[] { "Gen", "GameData", "DatasMap", "DecorsSets", "Maps", "Scenarios" })
        {
            var directory = Path.Combine(root, child);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".tag", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return (Path.GetRelativePath(root, file).Replace('/', '\\'), file);
            }
        }
    }

    /// <summary>The .tag companions of a mod's generated files; they are planned with their file (see <see cref="TagFor"/>).</summary>
    internal static IEnumerable<(string RelativePath, string FullPath)> EnumerateTagFiles(string root)
    {
        var gen = Path.Combine(root, "Gen");
        if (!Directory.Exists(gen)) yield break;
        foreach (var file in Directory.EnumerateFiles(gen, "*.tag", SearchOption.AllDirectories))
            yield return (Path.GetRelativePath(root, file).Replace('/', '\\'), file);
    }
}
