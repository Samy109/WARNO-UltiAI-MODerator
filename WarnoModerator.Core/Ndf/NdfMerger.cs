using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace WarnoModerator.Core.Ndf;

public sealed record NdfVariant(string ModName, NdfDatabase Database);

/// <summary>A value that several mods changed differently; the highest-priority mod's value was kept.</summary>
public sealed record NdfConflict(string Path, string Winner, IReadOnlyList<string> Overridden, string WinnerValue);

public sealed record NdfMergeResult(NdfDatabase Database, IReadOnlyList<NdfConflict> Conflicts,
    IReadOnlyList<string> ChangedBy, int ChangedObjects);

/// <summary>
/// Three-way, N-mod merge of compiled databases against the vanilla database. Each mod's delta is what it
/// changed relative to vanilla; deltas that touch different values combine, and where several mods change the
/// same value differently the highest-priority mod wins and a conflict is recorded.
/// </summary>
public sealed class NdfMerger
{
    private const string DescriptorIdField = "DescriptorId";
    private static readonly string[] IdentityFields = ["_ShortDatabaseName", "Name", DescriptorIdField];
    private readonly List<NdfConflict> _conflicts = [];
    // Merging the same input objects again yields the same merged object, so an object several parents share stays one.
    private readonly Dictionary<InstanceKey, NdfObject> _mergedObjects = [];
    // Every input's instance of a private constant, mapped to the merged constant it became.
    private readonly Dictionary<NdfObject, NdfObject> _constants = new(ReferenceEqualityComparer.Instance);
    // Merged constants vanilla has and some mod removed (or replaced): kept only while something still references them.
    private readonly HashSet<NdfObject> _removedConstants = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _changedBy = [];
    private int _changedObjects;

    private NdfMerger() { }

    /// <param name="vanilla">The unmodified database, or null when WARNO ships none (e.g. Catalog.cat).</param>
    /// <param name="variants">Each mod's full database, highest priority first.</param>
    public static NdfMergeResult Merge(NdfDatabase? vanilla, IReadOnlyList<NdfVariant> variants, CancellationToken token = default)
    {
        if (variants.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(variants));
        var merger = new NdfMerger();
        (vanilla, variants) = merger.UnifyDescriptors(vanilla, variants, token);
        var merged = vanilla is null ? merger.MergeWithoutBase(variants, token) : merger.MergeTopLevel(vanilla, variants, token);
        var sources = variants.Select(v => v.Database).Prepend(vanilla).OfType<NdfDatabase>().ToArray();
        var owners = new Dictionary<(string, string), string>();
        foreach (var db in sources)
            foreach (var pair in db.PropertyOwners) owners.TryAdd(pair.Key, pair.Value);
        merged = merger.RestoreMissingExports(merged, vanilla, variants);
        merged = merger.SettleDescriptors(merged, vanilla, variants);
        merged = merger.SettleConstants(merged, sources);
        return new NdfMergeResult(new NdfDatabase(merged, owners), merger._conflicts,
            variants.Select(v => v.ModName).Where(merger._changedBy.Contains).ToArray(), merger._changedObjects);
    }

    /// <summary>
    /// A DescriptorId names one object however many parents reference it, and WARNO refuses to load two objects
    /// with the same id. Merging each use separately lets them diverge, so the content of every non-exported
    /// descriptor is merged once here and written back into vanilla and each variant. The merge that follows then
    /// only decides where each descriptor is used.
    /// </summary>
    private (NdfDatabase? Vanilla, IReadOnlyList<NdfVariant> Variants) UnifyDescriptors(NdfDatabase? vanilla,
        IReadOnlyList<NdfVariant> variants, CancellationToken token)
    {
        // Slot 0 is vanilla; slot i + 1 is variants[i].
        var versions = new Dictionary<NdfValue, NdfObject?[]>();
        var databases = variants.Select(v => v.Database).Prepend(vanilla).ToArray();
        for (var i = 0; i < databases.Length; i++)
        {
            if (databases[i] is not { } database) continue;
            foreach (var nested in NestedObjects(database.TopLevel))
            {
                if (DescriptorId(nested) is not { } id) continue;
                if (!versions.TryGetValue(id, out var slots)) versions[id] = slots = new NdfObject?[databases.Length];
                slots[i] ??= nested;
            }
        }
        // Every database is rewritten, even when the descriptors agree: vanilla and each mod hold their own instance of
        // each descriptor, and the merged database must hold exactly one.
        var descriptors = new Dictionary<NdfValue, NdfObject>();
        var unified = new Dictionary<NdfObject, NdfObject>(ReferenceEqualityComparer.Instance);

        NdfObject Unify(NdfObject value)
        {
            if (unified.TryGetValue(value, out var done)) return done;
            return unified[value] = DescriptorId(value) is { } id ? MergeDescriptor(id) : UnifyFields(value);
        }

        NdfObject UnifyFields(NdfObject value) => new(value.ClassName,
            value.Fields.Select(f => new KeyValuePair<string, NdfValue>(f.Key, UnifyValue(f.Value))).ToArray(), value.Origin);

        NdfValue UnifyValue(NdfValue value) => value switch
        {
            NdfObject o => Unify(o),
            NdfList l => new NdfList(l.Items.Select(UnifyValue).ToArray()),
            NdfMap m => new NdfMap(m.Entries.Select(e => new KeyValuePair<NdfValue, NdfValue>(UnifyValue(e.Key), UnifyValue(e.Value))).ToArray()),
            NdfPair p => new NdfPair(UnifyValue(p.First), UnifyValue(p.Second)),
            _ => value
        };

        NdfObject MergeDescriptor(NdfRaw id)
        {
            if (descriptors.TryGetValue(id, out var done)) return done;
            var slots = versions[id].Select(v => v is null ? null : UnifyFields(v)).ToArray();
            var baseValue = slots[0];
            var path = $"{slots.OfType<NdfObject>().First().ClassName} {DescribeDescriptor(id)}";
            var value = MergeValue(path, baseValue,
                variants.Select((v, i) => (v.ModName, (NdfValue?)(slots[i + 1] ?? baseValue))).ToArray(), out var changed);
            if (changed) _changedObjects++;
            return descriptors[id] = (NdfObject)value!;
        }

        NdfDatabase UnifyDatabase(NdfDatabase database)
        {
            token.ThrowIfCancellationRequested();
            return new NdfDatabase(
                database.TopLevel.Select(t => new NdfTopLevel(t.ExportPath, t.ExportPath is null ? Unify(t.Value) : UnifyFields(t.Value))).ToArray(),
                database.PropertyOwners);
        }

        return (vanilla is null ? null : UnifyDatabase(vanilla),
            variants.Select(v => v with { Database = UnifyDatabase(v.Database) }).ToArray());
    }

    /// <summary>
    /// Compiles every nested descriptor once and keeps every export. Nested descriptors are already unique, so the
    /// only collisions left are exports, such as units two mods cloned with the same id. References to exports go
    /// by path, so the export that keeps vanilla's id (or else comes first) stays and the others get a new id.
    /// </summary>
    private List<NdfTopLevel> SettleDescriptors(List<NdfTopLevel> merged, NdfDatabase? vanilla, IReadOnlyList<NdfVariant> variants)
    {
        var claimed = new HashSet<NdfValue>();
        foreach (var nested in NestedObjects(merged))
            if (DescriptorId(nested) is { } id) claimed.Add(id);
        var vanillaIds = vanilla?.TopLevel.Where(t => t.ExportPath is not null && DescriptorId(t.Value) is not null)
            .Select(t => (t.ExportPath!, (NdfValue)DescriptorId(t.Value)!)).ToHashSet() ?? [];
        var exports = merged.Where(t => t.ExportPath is not null && DescriptorId(t.Value) is not null)
            .OrderBy(t => vanillaIds.Contains((t.ExportPath!, DescriptorId(t.Value)!)) ? 0 : 1);
        var reassigned = new Dictionary<string, NdfObject>(StringComparer.Ordinal);
        foreach (var top in exports)
        {
            var id = DescriptorId(top.Value)!;
            if (claimed.Add(id)) continue;
            var path = top.ExportPath!;
            var replacement = new NdfRaw(id.Type, SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{Convert.ToHexString(id.Data)}"))[..id.Data.Length]);
            claimed.Add(replacement);
            reassigned[path] = new NdfObject(top.Value.ClassName, top.Value.Fields
                .Select(f => f.Key == DescriptorIdField ? new KeyValuePair<string, NdfValue>(f.Key, replacement) : f).ToArray(), top.Value.Origin);
            var owner = variants.FirstOrDefault(v => v.Database.TopLevel.Any(t => t.ExportPath == path))?.ModName ?? "vanilla";
            _conflicts.Add(new NdfConflict(ShortName(path), owner, [],
                $"given the new descriptor {DescribeDescriptor(replacement)} because {DescribeDescriptor(id)} is already used"));
        }
        return merged.Select(t => t.ExportPath is { } path && reassigned.TryGetValue(path, out var value)
            ? new NdfTopLevel(path, value) : t).ToList();
    }

    private static NdfRaw? DescriptorId(NdfObject value) => value.Get(DescriptorIdField) as NdfRaw;

    private static string DescribeDescriptor(NdfRaw id)
    {
        var hex = Convert.ToHexString(id.Data);
        return $"{{{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}}}";
    }

    /// <summary>Every distinct object below the exports, plus the anonymous top-level objects.</summary>
    private static IEnumerable<NdfObject> NestedObjects(IReadOnlyList<NdfTopLevel> topLevel)
    {
        var visited = new HashSet<NdfValue>();
        var stack = new Stack<NdfValue>();
        foreach (var top in topLevel)
        {
            if (top.ExportPath is null) stack.Push(top.Value);
            else foreach (var field in top.Value.Fields) stack.Push(field.Value);
        }
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            switch (current)
            {
                case NdfObject o:
                    if (!visited.Add(o)) break;
                    yield return o;
                    foreach (var f in o.Fields) stack.Push(f.Value);
                    break;
                case NdfList l: foreach (var item in l.Items) stack.Push(item); break;
                case NdfMap m: foreach (var e in m.Entries) { stack.Push(e.Key); stack.Push(e.Value); } break;
                case NdfPair p: stack.Push(p.First); stack.Push(p.Second); break;
            }
        }
    }

    /// <summary>
    /// A private constant (an anonymous top-level object) has no name to match it by, and WARNO compiles separate
    /// constants with equal content, so a constant is the n-th one with its content: vanilla's second
    /// "TSequenceCondition_Phase PhaseType=2" is each mod's second one.
    /// </summary>
    private sealed record ConstantKey(NdfObject Content, int Occurrence);

    /// <summary>Each top-level entry's key: its export path, or its <see cref="ConstantKey"/>.</summary>
    private static List<(object Key, NdfObject Value)> Keys(IReadOnlyList<NdfTopLevel> topLevel)
    {
        var occurrences = new Dictionary<NdfObject, int>();
        return topLevel.Select(t =>
        {
            if (t.ExportPath is not null) return ((object)t.ExportPath, t.Value);
            var n = occurrences.GetValueOrDefault(t.Value);
            occurrences[t.Value] = n + 1;
            return (new ConstantKey(t.Value, n), t.Value);
        }).ToList();
    }

    private List<NdfTopLevel> MergeTopLevel(NdfDatabase vanilla, IReadOnlyList<NdfVariant> variants, CancellationToken token)
    {
        var baseKeys = Keys(vanilla.TopLevel);
        var variantKeys = variants.Select(v => Keys(v.Database.TopLevel)).ToArray();
        var baseByKey = baseKeys.ToDictionary(k => k.Key, k => (NdfValue)k.Value);
        var variantMaps = variantKeys.Select(keys => keys.ToDictionary(k => k.Key, k => (NdfValue)k.Value)).ToArray();
        var order = MergeOrder(baseKeys.Select(k => k.Key).ToList(), variantKeys.Select(keys => keys.Select(k => k.Key).ToList()).ToList());
        var result = new List<NdfTopLevel>(order.Count);
        var constants = new Dictionary<ConstantKey, NdfObject>();
        foreach (var key in order)
        {
            token.ThrowIfCancellationRequested();
            if (key is ConstantKey constantKey)
            {
                // Equal content wherever it comes from; the highest-priority mod's instance (and its debug line) is kept.
                var constant = (NdfObject)(variantMaps.Select(m => m.GetValueOrDefault(key)).FirstOrDefault(v => v is not null)
                    ?? baseByKey[key]);
                constants[constantKey] = constant;
                if (baseByKey.ContainsKey(key) && variantMaps.Any(m => !m.ContainsKey(key))) _removedConstants.Add(constant);
                result.Add(new NdfTopLevel(null, constant));
                continue;
            }
            var path = (string)key;
            var value = MergeValue(ShortName(path), baseByKey.GetValueOrDefault(key),
                variants.Select((v, i) => (v.ModName, variantMaps[i].GetValueOrDefault(key))).ToArray(), out var changed, exported: true);
            if (changed) _changedObjects++;
            if (value is NdfObject obj) result.Add(new NdfTopLevel(path, obj));
            else if (value is not null) throw new NdfFormatException($"Export {path} merged to a non-object value.");
        }
        foreach (var keys in variantKeys.Prepend(baseKeys))
            foreach (var (key, value) in keys)
                if (key is ConstantKey constantKey) _constants[value] = constants[constantKey];
        return result;
    }

    // Without vanilla (asset catalogs), anonymous roots are paired by position and every variant is treated as
    // an addition; differing values resolve by priority.
    private List<NdfTopLevel> MergeWithoutBase(IReadOnlyList<NdfVariant> variants, CancellationToken token)
    {
        var count = variants[0].Database.TopLevel.Count;
        if (variants.Any(v => v.Database.TopLevel.Count != count
                || v.Database.TopLevel.Where((t, i) => t.ExportPath != variants[0].Database.TopLevel[i].ExportPath).Any()))
            throw new NdfFormatException("Databases without a vanilla baseline have different top-level layouts.");
        var result = new List<NdfTopLevel>(count);
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var path = variants[0].Database.TopLevel[i].ExportPath;
            var value = (NdfObject)MergeValue(path is null ? $"#{i}" : ShortName(path), null,
                variants.Select(v => (v.ModName, (NdfValue?)v.Database.TopLevel[i].Value)).ToArray(), out var changed)!;
            if (changed) _changedObjects++;
            result.Add(new NdfTopLevel(path, value));
            // A private constant here is the one at the same position in every mod.
            if (path is null) foreach (var variant in variants) _constants[variant.Database.TopLevel[i].Value] = value;
        }
        return result;
    }

    private static string ShortName(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Merges one value. Variants are (mod, value) pairs, highest priority first; null means absent.</summary>
    /// <param name="exported">The value is an export, which its path identifies even if a mod changes its descriptor.</param>
    private NdfValue? MergeValue(string path, NdfValue? baseValue, IReadOnlyList<(string Mod, NdfValue? Value)> variants,
        out bool changed, bool exported = false)
    {
        var edits = variants.Where(v => !NdfValue.Same(v.Value, baseValue)).ToArray();
        changed = edits.Length > 0;
        if (!changed) return baseValue;
        foreach (var edit in edits) _changedBy.Add(edit.Mod);
        if (edits.All(e => NdfValue.Same(e.Value, edits[0].Value))) return edits[0].Value;

        var candidates = edits.Select(e => e.Value).Append(baseValue).OfType<NdfValue>().ToArray();
        if (edits.All(e => e.Value is not null))
        {
            // Two nested descriptors are different objects; a field-by-field blend would duplicate their ids.
            if (candidates.All(v => v is NdfObject o && o.ClassName == ((NdfObject)candidates[0]).ClassName
                    && (exported || NdfValue.Same(DescriptorId(o), DescriptorId((NdfObject)candidates[0])))))
                return MergeObject(path, baseValue as NdfObject, variants);
            if (candidates.All(v => v is NdfList) && TryMergeList(path, baseValue as NdfList, variants, out var list))
                return list;
            if (candidates.All(v => v is NdfMap))
                return MergeMap(path, baseValue as NdfMap, variants);
            if (candidates.All(v => v is NdfPair))
                return MergePair(path, baseValue as NdfPair, variants);
        }

        var winner = edits[0];
        var overridden = edits.Skip(1).Where(e => !NdfValue.Same(e.Value, winner.Value)).Select(e => e.Mod).Distinct().ToArray();
        _conflicts.Add(new NdfConflict(path, winner.Mod, overridden, winner.Value?.Describe() ?? "removed"));
        return winner.Value;
    }

    private NdfObject MergeObject(string path, NdfObject? baseValue, IReadOnlyList<(string Mod, NdfValue? Value)> variants)
    {
        var key = new InstanceKey([baseValue, .. variants.Select(v => v.Value)]);
        if (_mergedObjects.TryGetValue(key, out var done)) return done;
        return _mergedObjects[key] = BuildMergedObject(path, baseValue, variants);
    }

    private NdfObject BuildMergedObject(string path, NdfObject? baseValue, IReadOnlyList<(string Mod, NdfValue? Value)> variants)
    {
        var className = variants.Select(v => v.Value).OfType<NdfObject>().First().ClassName;
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in variants.Select(v => v.Value as NdfObject).Prepend(baseValue).OfType<NdfObject>())
            foreach (var field in source.Fields)
                if (seen.Add(field.Key)) names.Add(field.Key);
        var fields = new List<KeyValuePair<string, NdfValue>>(names.Count);
        foreach (var name in names)
        {
            var value = MergeValue($"{path} › {name}", baseValue?.Get(name),
                variants.Select(v => (v.Mod, v.Value is NdfObject o ? o.Get(name) : (baseValue?.Get(name)))).ToArray(), out _);
            if (value is not null) fields.Add(new(name, value));
        }
        // The merged object takes the place of vanilla's (or else the first mod's), so it keeps that identity.
        var origin = (baseValue ?? variants.Select(v => v.Value).OfType<NdfObject>().First()).Origin;
        return new NdfObject(className, fields, origin);
    }

    private NdfPair MergePair(string path, NdfPair? baseValue, IReadOnlyList<(string Mod, NdfValue? Value)> variants)
    {
        var first = MergeValue($"{path}.1", baseValue?.First, variants.Select(v => (v.Mod, (NdfValue?)((NdfPair?)v.Value)?.First ?? baseValue?.First)).ToArray(), out _);
        var second = MergeValue($"{path}.2", baseValue?.Second, variants.Select(v => (v.Mod, (NdfValue?)((NdfPair?)v.Value)?.Second ?? baseValue?.Second)).ToArray(), out _);
        return new NdfPair(first ?? throw new NdfFormatException($"{path} lost a pair element."),
            second ?? throw new NdfFormatException($"{path} lost a pair element."));
    }

    private NdfMap MergeMap(string path, NdfMap? baseValue, IReadOnlyList<(string Mod, NdfValue? Value)> variants)
    {
        static List<KeyValuePair<object, NdfValue>> Keyed(NdfMap map) =>
            map.Entries.Select(e => new KeyValuePair<object, NdfValue>(e.Key, e.Value)).ToList();
        var entries = MergeKeyed(path, baseValue is null ? null : Keyed(baseValue),
            variants.Select(v => (v.Mod, v.Value is NdfMap m ? Keyed(m) : null)).ToArray(), key => ((NdfValue)key).Describe());
        return new NdfMap(entries.Select(e => new KeyValuePair<NdfValue, NdfValue>((NdfValue)e.Key, e.Value)).ToArray());
    }

    private bool TryMergeList(string path, NdfList? baseValue, IReadOnlyList<(string Mod, NdfValue? Value)> variants, out NdfList? result)
    {
        result = null;
        var lists = variants.Select(v => v.Value as NdfList).Prepend(baseValue).OfType<NdfList>().ToArray();
        var keyed = lists.Select(KeyList).ToArray();
        if (keyed.All(k => k is not null))
        {
            var entries = MergeKeyed(path, baseValue is null ? null : KeyList(baseValue),
                variants.Select(v => (v.Mod, v.Value is NdfList l ? KeyList(l) : null)).ToArray(), DescribeKey);
            result = new NdfList(entries.Select(e => e.Value).ToArray());
            return true;
        }
        // Same-length lists merge element by element; without a base (asset catalogs) each element is an addition.
        if (lists.All(l => l.Items.Count == lists[0].Items.Count))
        {
            var items = new NdfValue[lists[0].Items.Count];
            for (var i = 0; i < items.Length; i++)
            {
                var index = i;
                items[i] = MergeValue($"{path}[{i}]", baseValue?.Items[i],
                    variants.Select(v => (v.Mod, (v.Value as NdfList)?.Items[index])).ToArray(), out _)
                    ?? throw new NdfFormatException($"{path}[{i}] was removed.");
            }
            result = new NdfList(items);
            return true;
        }
        return false;
    }

    /// <summary>Keys list elements by identity (class plus name-like field, or the value itself); null if ambiguous.</summary>
    private static List<KeyValuePair<object, NdfValue>>? KeyList(NdfList list)
    {
        var keys = new HashSet<object>();
        var result = new List<KeyValuePair<object, NdfValue>>(list.Items.Count);
        foreach (var item in list.Items)
        {
            object? key = item switch
            {
                NdfObject o => IdentityFields.Select(o.Get).FirstOrDefault(v => v is not null) is { } id
                    ? (o.ClassName, id) : (object)o.ClassName,
                NdfText or NdfImport or NdfExportRef or NdfRaw => item,
                _ => null
            };
            if (key is null || !keys.Add(key)) return null;
            result.Add(new(key, item));
        }
        return result;
    }

    private static string DescribeKey(object key) => key switch
    {
        ValueTuple<string, NdfValue> t => $"{t.Item1} {t.Item2.Describe()}",
        string s => s,
        NdfValue v => v.Describe(),
        _ => key.ToString() ?? "?"
    };

    private List<KeyValuePair<object, NdfValue>> MergeKeyed(string path, List<KeyValuePair<object, NdfValue>>? baseEntries,
        IReadOnlyList<(string Mod, List<KeyValuePair<object, NdfValue>>? Entries)> variants, Func<object, string> describe)
    {
        var baseMap = baseEntries?.ToDictionary(e => e.Key, e => e.Value) ?? [];
        var maps = variants.Select(v => v.Entries?.ToDictionary(e => e.Key, e => e.Value)).ToArray();
        var changedOrders = variants.Where(v => v.Entries is not null
                && (baseEntries is null || !v.Entries.Select(e => e.Key).SequenceEqual(baseEntries.Select(e => e.Key))))
            .Select(v => v.Entries!.Select(e => e.Key).ToList()).ToList();
        var order = MergeOrder(baseEntries?.Select(e => e.Key).ToList() ?? [], changedOrders);
        var result = new List<KeyValuePair<object, NdfValue>>(order.Count);
        foreach (var key in order)
        {
            var baseValue = baseMap.GetValueOrDefault(key);
            var value = MergeValue($"{path}[{describe(key)}]", baseValue,
                variants.Select((v, i) => (v.Mod, maps[i] is { } m ? m.GetValueOrDefault(key) : baseValue)).ToArray(), out _);
            if (value is not null) result.Add(new(key, value));
        }
        return result;
    }

    /// <summary>
    /// Combines key orders: the highest-priority order that differs from the base is the skeleton, and keys only
    /// other sequences contain are inserted after their nearest preceding key.
    /// </summary>
    private static List<object> MergeOrder(List<object> baseOrder, IReadOnlyList<List<object>> others)
    {
        var sequences = others.Where(o => !o.SequenceEqual(baseOrder)).Append(baseOrder).ToList();
        var result = new List<object>(sequences[0]);
        var positions = new HashSet<object>(result);
        foreach (var sequence in sequences.Skip(1).Concat(others))
        {
            for (var i = 0; i < sequence.Count; i++)
            {
                if (positions.Contains(sequence[i])) continue;
                var insertAt = 0;
                for (var j = i - 1; j >= 0; j--)
                {
                    var anchor = result.IndexOf(sequence[j]);
                    if (anchor >= 0) { insertAt = anchor + 1; break; }
                }
                result.Insert(insertAt, sequence[i]);
                positions.Add(sequence[i]);
            }
        }
        return result;
    }

    /// <summary>Re-adds any export a merged value references but a mod deleted, so references stay valid.</summary>
    private List<NdfTopLevel> RestoreMissingExports(List<NdfTopLevel> merged, NdfDatabase? vanilla, IReadOnlyList<NdfVariant> variants)
    {
        var present = merged.Where(t => t.ExportPath is not null).Select(t => t.ExportPath!).ToHashSet(StringComparer.Ordinal);
        var names = variants.Select(v => v.ModName).Append("vanilla").ToArray();
        var sources = variants.Select(v => v.Database).Append(vanilla)
            .Select(db => db?.TopLevel.Where(t => t.ExportPath is not null)
                .GroupBy(t => t.ExportPath!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal))
            .ToArray();
        var pending = new Queue<NdfTopLevel>(merged);
        var visited = new HashSet<NdfValue>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0)
        {
            var top = pending.Dequeue();
            foreach (var path in ExportRefs(top.Value, visited))
            {
                if (present.Contains(path)) continue;
                var sourceIndex = Array.FindIndex(sources, s => s?.ContainsKey(path) == true);
                if (sourceIndex < 0) throw new NdfFormatException($"Merged database references {path}, which no input defines.");
                var restored = new NdfTopLevel(path, sources[sourceIndex]![path]);
                var removedBy = variants.Where(v => v.Database.TopLevel.All(t => t.ExportPath != path)).Select(v => v.ModName).ToArray();
                _conflicts.Add(new NdfConflict(ShortName(path), names[sourceIndex], removedBy,
                    "kept because other merged content still references it"));
                merged.Add(restored);
                present.Add(path);
                pending.Enqueue(restored);
            }
        }
        return merged;
    }

    private static IEnumerable<string> ExportRefs(NdfValue value, HashSet<NdfValue> visited)
    {
        var stack = new Stack<NdfValue>();
        stack.Push(value);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            switch (current)
            {
                case NdfExportRef export: yield return export.Path; break;
                case NdfObject o: if (visited.Add(o)) foreach (var f in o.Fields) stack.Push(f.Value); break;
                case NdfList l: foreach (var item in l.Items) stack.Push(item); break;
                case NdfMap m: foreach (var e in m.Entries) { stack.Push(e.Key); stack.Push(e.Value); } break;
                case NdfPair p: stack.Push(p.First); stack.Push(p.Second); break;
            }
        }
    }

    /// <summary>
    /// Merged values still point at vanilla's or a mod's own instance of a private constant; each such reference is
    /// pointed at the merged constant it became (see <see cref="ConstantKey"/>). A constant some mod removed, or that
    /// the inputs used, is dropped once nothing in the merged database references it; one WARNO itself keeps
    /// unreferenced stays.
    /// </summary>
    private List<NdfTopLevel> SettleConstants(List<NdfTopLevel> merged, IReadOnlyList<NdfDatabase> sources)
    {
        var used = new HashSet<NdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var source in sources)
            foreach (var referenced in ReferencedObjects(source.TopLevel))
                if (_constants.TryGetValue(referenced, out var constant)) used.Add(constant);

        var rewritten = new Dictionary<NdfObject, NdfObject>(ReferenceEqualityComparer.Instance);
        NdfObject Rewrite(NdfObject value)
        {
            if (rewritten.TryGetValue(value, out var done)) return done;
            if (_constants.TryGetValue(value, out var constant) && !ReferenceEquals(constant, value))
                return rewritten[value] = Rewrite(constant);
            var changed = false;
            var fields = new KeyValuePair<string, NdfValue>[value.Fields.Count];
            for (var i = 0; i < fields.Length; i++)
            {
                var field = RewriteValue(value.Fields[i].Value);
                changed |= !ReferenceEquals(field, value.Fields[i].Value);
                fields[i] = new(value.Fields[i].Key, field);
            }
            return rewritten[value] = changed ? new NdfObject(value.ClassName, fields, value.Origin) : value;
        }
        NdfValue RewriteValue(NdfValue value) => value switch
        {
            NdfObject o => Rewrite(o),
            NdfList l => Rebuilt(l.Items, items => new NdfList(items), l),
            NdfMap m => Rebuilt(m.Entries.SelectMany(e => new[] { e.Key, e.Value }).ToArray(),
                items => new NdfMap(Enumerable.Range(0, items.Length / 2)
                    .Select(i => new KeyValuePair<NdfValue, NdfValue>(items[2 * i], items[2 * i + 1])).ToArray()), m),
            NdfPair p => Rebuilt([p.First, p.Second], items => new NdfPair(items[0], items[1]), p),
            _ => value
        };
        NdfValue Rebuilt(IReadOnlyList<NdfValue> items, Func<NdfValue[], NdfValue> build, NdfValue original)
        {
            var result = items.Select(RewriteValue).ToArray();
            return result.Where((item, i) => !ReferenceEquals(item, items[i])).Any() ? build(result) : original;
        }

        var settled = merged.Select(t => (Original: t.Value, Top: t with { Value = Rewrite(t.Value) })).ToList();
        var referencedNow = new HashSet<NdfObject>(ReferencedObjects(settled.Select(s => s.Top)), ReferenceEqualityComparer.Instance);
        return settled.Where(s => s.Top.ExportPath is not null || referencedNow.Contains(s.Top.Value)
                || !used.Contains(s.Original) && !_removedConstants.Contains(s.Original))
            .Select(s => s.Top).ToList();
    }

    /// <summary>Every object some field references (as opposed to objects that are only top-level roots).</summary>
    private static IEnumerable<NdfObject> ReferencedObjects(IEnumerable<NdfTopLevel> topLevel)
    {
        var visited = new HashSet<NdfObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<NdfValue>();
        foreach (var top in topLevel)
            if (visited.Add(top.Value)) foreach (var field in top.Value.Fields) stack.Push(field.Value);
        var referenced = new HashSet<NdfObject>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case NdfObject o:
                    referenced.Add(o);
                    if (visited.Add(o)) foreach (var field in o.Fields) stack.Push(field.Value);
                    break;
                case NdfList l: foreach (var item in l.Items) stack.Push(item); break;
                case NdfMap m: foreach (var e in m.Entries) { stack.Push(e.Key); stack.Push(e.Value); } break;
                case NdfPair p: stack.Push(p.First); stack.Push(p.Second); break;
            }
        }
        return referenced;
    }

    /// <summary>A tuple of instances compared by reference.</summary>
    private sealed class InstanceKey(NdfValue?[] items) : IEquatable<InstanceKey>
    {
        private readonly NdfValue?[] _items = items;
        public bool Equals(InstanceKey? other) =>
            other is not null && other._items.Length == _items.Length && _items.Zip(other._items).All(p => ReferenceEquals(p.First, p.Second));
        public override bool Equals(object? obj) => Equals(obj as InstanceKey);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in _items) hash.Add(item is null ? 0 : RuntimeHelpers.GetHashCode(item));
            return hash.ToHashCode();
        }
    }
}
