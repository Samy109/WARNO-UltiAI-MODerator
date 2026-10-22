using System.Buffers.Binary;

namespace WarnoModerator.Core.Ndf;

/// <summary>Decompiles an .ndfbin into an <see cref="NdfDatabase"/>.</summary>
internal sealed class NdfReader
{
    private const uint ObjectEnd = 0xABABABAB, ImportRef = 0xAAAAAAAA, ObjectRef = 0xBBBBBBBB;

    // Payload sizes of fixed-width value tags; verified against every Workshop database.
    internal static readonly IReadOnlyDictionary<uint, int> FixedSizes = new Dictionary<uint, int>
    {
        [0x00] = 1, [0x01] = 1, [0x18] = 2, [0x19] = 2,
        [0x02] = 4, [0x03] = 4, [0x05] = 4, [0x0D] = 4,
        [0x06] = 8, [0x13] = 8, [0x1D] = 8, [0x1F] = 8, [0x21] = 8,
        [0x0B] = 12, [0x0F] = 12,
        [0x0C] = 16, [0x10] = 16, [0x1A] = 16, [0x25] = 16
    };

    private readonly byte[] _d;
    private readonly string[] _classes, _strings, _tran;
    private readonly (string Name, int Class)[] _props;
    private readonly List<(int Class, List<(int Prop, object Value)> Fields)> _raw = [];
    private readonly Dictionary<int, string> _exports, _imports;
    private readonly int[] _topo;
    private readonly HashSet<int> _topoSet;
    private readonly NdfObject?[] _built;
    private readonly bool[] _building;
    private readonly int[] _references;
    private readonly Dictionary<(string, string), string> _owners = [];

    private NdfReader(byte[] file)
    {
        _d = NdfContainer.Decompress(file, out var toc);
        var tables = ReadToc((int)toc);
        _classes = ReadStrings(tables["CLAS"]);
        _strings = ReadStrings(tables["STRG"]);
        _tran = ReadStrings(tables["TRAN"]);
        _props = ReadProps(tables["PROP"]);
        _exports = ReadTree(tables["EXPR"]);
        _imports = ReadTree(tables["IMPR"]);
        var (topoOffset, topoSize) = tables["TOPO"];
        _topo = new int[topoSize / 4];
        for (var i = 0; i < _topo.Length; i++) _topo[i] = (int)U32(topoOffset + 4 * i);
        _topoSet = [.. _topo];
        ReadObjects(tables["OBJE"]);
        _built = new NdfObject?[_raw.Count];
        _building = new bool[_raw.Count];
        _references = new int[_raw.Count];
    }

    public static NdfDatabase Read(byte[] file) => new NdfReader(file).Build();

    private uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p));

    private Dictionary<string, (int Offset, int Size)> ReadToc(int p)
    {
        if (!_d.AsSpan(p, 4).SequenceEqual("TOC0"u8)) throw new NdfFormatException("Missing TOC0.");
        var count = U32(p + 4);
        var result = new Dictionary<string, (int, int)>();
        for (var i = 0; i < count; i++)
        {
            var e = p + 8 + 24 * i;
            result[NdfDatabase.Latin1(_d.AsSpan(e, 4))] =
                ((int)BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan(e + 8)), (int)BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan(e + 16)));
        }
        foreach (var name in new[] { "OBJE", "TOPO", "CHNK", "CLAS", "PROP", "STRG", "TRAN", "IMPR", "EXPR" })
            if (!result.ContainsKey(name)) throw new NdfFormatException($"Missing {name} table.");
        return result;
    }

    private string[] ReadStrings((int Offset, int Size) table)
    {
        var list = new List<string>();
        for (int p = table.Offset, end = table.Offset + table.Size; p < end;)
        {
            var length = (int)U32(p);
            list.Add(NdfDatabase.Latin1(_d.AsSpan(p + 4, length)));
            p += 4 + length;
        }
        return [.. list];
    }

    private (string, int)[] ReadProps((int Offset, int Size) table)
    {
        var list = new List<(string, int)>();
        for (int p = table.Offset, end = table.Offset + table.Size; p < end;)
        {
            var length = (int)U32(p);
            list.Add((NdfDatabase.Latin1(_d.AsSpan(p + 4, length)), (int)U32(p + 4 + length)));
            p += 8 + length;
        }
        return [.. list];
    }

    // Node: u32 TRAN index, u32 target (0xFFFFFFFF = none), u32 child count, then child offsets measured from
    // the start of the offset array.
    private Dictionary<int, string> ReadTree((int Offset, int Size) table)
    {
        var result = new Dictionary<int, string>();
        if (table.Size == 0) return result;
        void Walk(int p, string prefix)
        {
            var path = prefix.Length == 0 ? _tran[U32(p)] : prefix + "/" + _tran[U32(p)];
            var target = U32(p + 4);
            if (target != uint.MaxValue) result[(int)target] = path;
            var children = U32(p + 8);
            for (var i = 0; i < children; i++) Walk(p + 12 + (int)U32(p + 12 + 4 * i), path);
        }
        Walk(table.Offset, "");
        return result;
    }

    private void ReadObjects((int Offset, int Size) table)
    {
        var p = table.Offset;
        var end = table.Offset + table.Size;
        while (p < end)
        {
            var cls = (int)U32(p); p += 4;
            var fields = new List<(int, object)>();
            while (true)
            {
                var prop = U32(p); p += 4;
                if (prop == ObjectEnd) break;
                fields.Add(((int)prop, ReadValue(ref p)));
            }
            _raw.Add((cls, fields));
        }
        if (p != end) throw new NdfFormatException("Object table overran its bounds.");
    }

    // Raw values keep object references as (index) so sharing can be resolved after all objects are read.
    private sealed record RawRef(int Index);
    private sealed record RawList(List<object> Items);
    private sealed record RawMap(List<(object, object)> Entries);
    private sealed record RawPair(object First, object Second);

    private object ReadValue(ref int p)
    {
        var type = U32(p); p += 4;
        if (FixedSizes.TryGetValue(type, out var size))
        {
            var data = _d.AsSpan(p, size).ToArray();
            p += size;
            return new NdfRaw(type, data);
        }
        switch (type)
        {
            case 0x07:
            case 0x1C:
                var index = U32(p); p += 4;
                return new NdfText(type, _strings[index]);
            case 0x08:
            case 0x14:
                var length = (int)U32(p);
                var bytes = _d.AsSpan(p + 4, length).ToArray();
                p += 4 + length;
                return new NdfRaw(type, bytes);
            case 0x09:
                var kind = U32(p);
                if (kind == ImportRef) { var import = (int)U32(p + 4); p += 8; return new NdfImport(_imports.GetValueOrDefault(import) ?? throw new NdfFormatException($"Unnamed import {import}.")); }
                if (kind == ObjectRef)
                {
                    var target = U32(p + 4);
                    p += 12;
                    return target == uint.MaxValue ? NdfNull.Instance : new RawRef((int)target);
                }
                throw new NdfFormatException($"Unknown reference kind 0x{kind:X8}.");
            case 0x11:
                var count = (int)U32(p); p += 4;
                var items = new List<object>(count);
                for (var i = 0; i < count; i++) items.Add(ReadValue(ref p));
                return new RawList(items);
            case 0x12:
                var entries = (int)U32(p); p += 4;
                var map = new List<(object, object)>(entries);
                for (var i = 0; i < entries; i++) { var key = ReadValue(ref p); map.Add((key, ReadValue(ref p))); }
                return new RawMap(map);
            case 0x22:
                var first = ReadValue(ref p);
                return new RawPair(first, ReadValue(ref p));
            default:
                throw new NdfFormatException($"Unknown NDF value tag 0x{type:X2} at offset {p - 4}.");
        }
    }

    private NdfDatabase Build()
    {
        foreach (var (_, fields) in _raw)
            foreach (var (_, value) in fields) CountReferences(value);
        var topLevel = new List<NdfTopLevel>(_topo.Length);
        foreach (var index in _topo) topLevel.Add(new NdfTopLevel(_exports.GetValueOrDefault(index), BuildObject(index)));
        var shared = new HashSet<NdfObject>();
        for (var i = 0; i < _raw.Count; i++)
            if (!_topoSet.Contains(i) && _references[i] > 1) shared.Add(BuildObject(i));
        return new NdfDatabase(topLevel, shared, _owners);
    }

    private void CountReferences(object value)
    {
        switch (value)
        {
            case RawRef r: _references[r.Index]++; break;
            case RawList l: foreach (var item in l.Items) CountReferences(item); break;
            case RawMap m: foreach (var (k, v) in m.Entries) { CountReferences(k); CountReferences(v); } break;
            case RawPair pair: CountReferences(pair.First); CountReferences(pair.Second); break;
        }
    }

    private NdfObject BuildObject(int index)
    {
        if (_built[index] is { } done) return done;
        if (_building[index]) throw new NdfFormatException("Cyclic object references are not supported.");
        _building[index] = true;
        var (cls, raw) = _raw[index];
        var fields = new KeyValuePair<string, NdfValue>[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            var (name, owner) = _props[raw[i].Prop];
            if (owner != cls) _owners[(_classes[cls], name)] = _classes[owner];
            fields[i] = new(name, Convert(raw[i].Value));
        }
        _building[index] = false;
        return _built[index] = new NdfObject(_classes[cls], fields);
    }

    private NdfValue Convert(object value) => value switch
    {
        NdfValue v => v,
        RawRef r => _exports.TryGetValue(r.Index, out var path) ? new NdfExportRef(path) : BuildObject(r.Index),
        RawList l => new NdfList(l.Items.Select(Convert).ToArray()),
        RawMap m => new NdfMap(m.Entries.Select(e => new KeyValuePair<NdfValue, NdfValue>(Convert(e.Item1), Convert(e.Item2))).ToArray()),
        RawPair p => new NdfPair(Convert(p.First), Convert(p.Second)),
        _ => throw new InvalidOperationException()
    };
}
