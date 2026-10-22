using System.Buffers.Binary;
using System.Text;

namespace WarnoModerator.Core.Ndf;

/// <summary>Compiles an <see cref="NdfDatabase"/> back into a zstd-compressed .ndfbin.</summary>
internal sealed class NdfWriter
{
    private readonly NdfDatabase _db;
    private readonly HashSet<NdfObject> _anonymous = [];
    private readonly List<NdfObject> _objects = [];
    private readonly List<List<int>> _inlineChildren = [];
    private readonly Dictionary<string, int> _exportIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<NdfObject, int> _anonymousIndex = [];
    private readonly Dictionary<NdfObject, int> _sharedIndex = [];
    private readonly List<int> _topo = [];
    private readonly IndexedTable<string> _classes = new(), _strings = new(), _tran = new(), _imports = new();
    private readonly IndexedTable<(int Class, string Name)> _props = new();

    private NdfWriter(NdfDatabase db)
    {
        _db = db;
        foreach (var top in db.TopLevel) if (top.ExportPath is null) _anonymous.Add(top.Value);
    }

    public static byte[] Write(NdfDatabase db) => new NdfWriter(db).Compile();

    private byte[] Compile()
    {
        AssignIndices();
        var body = new MemoryStream();
        var tables = new List<(string Name, long Offset, long Size)>();
        void Table(string name, Action<BinaryWriter> write)
        {
            var start = NdfContainer.HeaderSize + body.Length;
            using (var writer = new BinaryWriter(body, Encoding.Latin1, leaveOpen: true)) write(writer);
            tables.Add((name, start, NdfContainer.HeaderSize + body.Length - start));
        }

        // Objects first: encoding them populates the class, property, string and import tables.
        Table("OBJE", WriteObjects);
        Table("TOPO", w => { foreach (var index in _topo) w.Write(index); });
        Table("CHNK", w => { w.Write(0); w.Write(_objects.Count); });
        Table("CLAS", w => { foreach (var name in _classes.Items) WriteString(w, name); });
        Table("PROP", w => { foreach (var (cls, name) in _props.Items) { WriteString(w, name); w.Write(cls); } });
        Table("STRG", w => { foreach (var text in _strings.Items) WriteString(w, text); });
        var importTree = BuildTree(_imports.Items.Select((path, index) => (path, index)));
        var exportTree = BuildTree(_exportIndex.Select(x => (x.Key, x.Value)));
        Table("TRAN", w => { foreach (var segment in _tran.Items) WriteString(w, segment); });
        Table("IMPR", w => WriteTree(w, importTree));
        Table("EXPR", w => WriteTree(w, exportTree));

        var toc = NdfContainer.HeaderSize + body.Length;
        using (var w = new BinaryWriter(body, Encoding.Latin1, leaveOpen: true))
        {
            w.Write("TOC0"u8);
            w.Write(tables.Count);
            foreach (var (name, offset, size) in tables)
            {
                w.Write(Encoding.ASCII.GetBytes(name));
                w.Write(0);
                w.Write((ulong)offset);
                w.Write((ulong)size);
            }
        }
        return NdfContainer.Compress(body.ToArray(), (ulong)toc);
    }

    private static void WriteString(BinaryWriter w, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private int Allocate(NdfObject value)
    {
        _objects.Add(value);
        _inlineChildren.Add([]);
        return _objects.Count - 1;
    }

    private void AssignIndices()
    {
        foreach (var top in _db.TopLevel)
        {
            var index = Allocate(top.Value);
            if (top.ExportPath is null) _anonymousIndex.TryAdd(top.Value, index);
            else if (!_exportIndex.TryAdd(top.ExportPath, index))
                throw new NdfFormatException($"Duplicate export {top.ExportPath}.");
            _topo.Add(index);
            Visit(index);
        }
    }

    // Pre-order layout: each inline object follows its first referrer, as the WARNO compiler emits them.
    private void Visit(int index)
    {
        var children = _inlineChildren[index];
        foreach (var field in _objects[index].Fields) VisitValue(field.Value, children);
    }

    private void VisitValue(NdfValue value, List<int> children)
    {
        switch (value)
        {
            case NdfObject child:
                if (_anonymous.Contains(child)) return;
                var shared = _db.SharedObjects.Contains(child);
                if (shared && _sharedIndex.TryGetValue(child, out var existing)) { children.Add(existing); return; }
                var index = Allocate(child);
                if (shared) _sharedIndex[child] = index;
                children.Add(index);
                Visit(index);
                break;
            case NdfList list: foreach (var item in list.Items) VisitValue(item, children); break;
            case NdfMap map: foreach (var entry in map.Entries) { VisitValue(entry.Key, children); VisitValue(entry.Value, children); } break;
            case NdfPair pair: VisitValue(pair.First, children); VisitValue(pair.Second, children); break;
        }
    }

    private void WriteObjects(BinaryWriter w)
    {
        for (var i = 0; i < _objects.Count; i++)
        {
            var value = _objects[i];
            var cls = _classes.Get(value.ClassName);
            w.Write(cls);
            var cursor = 0;
            foreach (var field in value.Fields)
            {
                var owner = _db.PropertyOwners.TryGetValue((value.ClassName, field.Key), out var declaring)
                    ? _classes.Get(declaring) : cls;
                w.Write(_props.Get((owner, field.Key)));
                WriteValue(w, field.Value, _inlineChildren[i], ref cursor);
            }
            w.Write(0xABABABABu);
        }
    }

    private void WriteValue(BinaryWriter w, NdfValue value, List<int> children, ref int cursor)
    {
        switch (value)
        {
            case NdfRaw raw:
                w.Write(raw.Type);
                if (raw.Type is 0x08 or 0x14) w.Write(raw.Data.Length);
                else if (NdfReader.FixedSizes.GetValueOrDefault(raw.Type, -1) != raw.Data.Length)
                    throw new NdfFormatException($"Invalid payload for tag 0x{raw.Type:X2}.");
                w.Write(raw.Data);
                break;
            case NdfText text:
                w.Write(text.Type);
                w.Write(_strings.Get(text.Text));
                break;
            case NdfImport import:
                w.Write(0x09u); w.Write(0xAAAAAAAAu); w.Write(_imports.Get(import.Path));
                break;
            case NdfExportRef export:
                if (!_exportIndex.TryGetValue(export.Path, out var target))
                    throw new NdfFormatException($"Reference to missing export {export.Path}.");
                WriteObjectRef(w, target);
                break;
            case NdfNull:
                w.Write(0x09u); w.Write(0xBBBBBBBBu); w.Write(uint.MaxValue); w.Write(uint.MaxValue);
                break;
            case NdfObject child:
                WriteObjectRef(w, _anonymous.Contains(child) ? _anonymousIndex[child] : children[cursor++]);
                break;
            case NdfList list:
                w.Write(0x11u); w.Write(list.Items.Count);
                foreach (var item in list.Items) WriteValue(w, item, children, ref cursor);
                break;
            case NdfMap map:
                w.Write(0x12u); w.Write(map.Entries.Count);
                foreach (var entry in map.Entries) { WriteValue(w, entry.Key, children, ref cursor); WriteValue(w, entry.Value, children, ref cursor); }
                break;
            case NdfPair pair:
                w.Write(0x22u);
                WriteValue(w, pair.First, children, ref cursor);
                WriteValue(w, pair.Second, children, ref cursor);
                break;
            default:
                throw new NdfFormatException($"Cannot encode {value.GetType().Name}.");
        }
    }

    private void WriteObjectRef(BinaryWriter w, int target)
    {
        w.Write(0x09u); w.Write(0xBBBBBBBBu); w.Write(target); w.Write(_classes.Get(_objects[target].ClassName));
    }

    private sealed class TreeNode(int segment)
    {
        public int Segment { get; } = segment;
        public int Target { get; set; } = -1;
        public List<TreeNode> Children { get; } = [];
        public Dictionary<string, TreeNode> Lookup { get; } = new(StringComparer.Ordinal);
    }

    private TreeNode? BuildTree(IEnumerable<(string Path, int Target)> paths)
    {
        TreeNode? root = null;
        string? rootName = null;
        foreach (var (path, target) in paths)
        {
            var segments = path.Split('/');
            if (root is null) { rootName = segments[0]; root = new TreeNode(_tran.Get(rootName)); }
            else if (segments[0] != rootName) throw new NdfFormatException($"Path {path} has an unexpected root.");
            var node = root;
            foreach (var segment in segments.Skip(1))
            {
                if (!node.Lookup.TryGetValue(segment, out var child))
                {
                    child = new TreeNode(_tran.Get(segment));
                    node.Lookup[segment] = child;
                    node.Children.Add(child);
                }
                node = child;
            }
            node.Target = target;
        }
        return root;
    }

    private static void WriteTree(BinaryWriter w, TreeNode? root)
    {
        if (root is null) return;
        var buffer = new MemoryStream();
        WriteNode(buffer, root);
        w.Write(buffer.ToArray());
    }

    // Pre-order: header, child offset array, then each child subtree; offsets are back-patched.
    private static void WriteNode(MemoryStream s, TreeNode node)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header, node.Segment);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], node.Target);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], node.Children.Count);
        s.Write(header);
        var offsets = s.Position;
        s.Write(new byte[4 * node.Children.Count]);
        Span<byte> offset = stackalloc byte[4];
        for (var i = 0; i < node.Children.Count; i++)
        {
            var childStart = s.Position;
            WriteNode(s, node.Children[i]);
            var end = s.Position;
            BinaryPrimitives.WriteInt32LittleEndian(offset, (int)(childStart - offsets));
            s.Position = offsets + 4 * i;
            s.Write(offset);
            s.Position = end;
        }
    }

    private sealed class IndexedTable<T> where T : notnull
    {
        private readonly Dictionary<T, int> _index = [];
        public List<T> Items { get; } = [];
        public int Get(T key)
        {
            if (_index.TryGetValue(key, out var value)) return value;
            _index[key] = Items.Count;
            Items.Add(key);
            return Items.Count - 1;
        }
    }
}
