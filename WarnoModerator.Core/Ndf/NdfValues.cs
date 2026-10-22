using System.Text;

namespace WarnoModerator.Core.Ndf;

/// <summary>
/// Immutable, structurally comparable NDF value. Object references are resolved: exported objects become
/// <see cref="NdfExportRef"/> by name and every other object is inlined as an <see cref="NdfObject"/>.
/// </summary>
public abstract class NdfValue : IEquatable<NdfValue>
{
    private int _hash;

    public bool Equals(NdfValue? other) =>
        ReferenceEquals(this, other) || other is not null && other.GetType() == GetType()
            && other.GetHashCode() == GetHashCode() && EqualsCore(other);

    public override bool Equals(object? obj) => obj is NdfValue value && Equals(value);

    public override int GetHashCode()
    {
        if (_hash == 0) { var hash = ComputeHash(); _hash = hash == 0 ? 1 : hash; }
        return _hash;
    }

    protected abstract bool EqualsCore(NdfValue other);
    protected abstract int ComputeHash();
    public abstract string Describe();

    public static bool Same(NdfValue? left, NdfValue? right) => left is null ? right is null : left.Equals(right);
}

/// <summary>Value stored verbatim: numbers, vectors, hashes, GUIDs, and length-prefixed blobs (0x08, 0x14).</summary>
public sealed class NdfRaw(uint type, byte[] data) : NdfValue
{
    public uint Type { get; } = type;
    public byte[] Data { get; } = data;
    protected override bool EqualsCore(NdfValue other) => other is NdfRaw o && o.Type == Type && o.Data.AsSpan().SequenceEqual(Data);
    protected override int ComputeHash()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.AddBytes(Data);
        return hash.ToHashCode();
    }
    public override string Describe() => Type switch
    {
        0x00 => Data[0] != 0 ? "True" : "False",
        0x02 => BitConverter.ToInt32(Data).ToString(),
        0x03 => BitConverter.ToUInt32(Data).ToString(),
        0x05 => BitConverter.ToSingle(Data).ToString("G6"),
        _ => $"0x{Type:X2}:{Convert.ToHexString(Data)}"
    };
}

/// <summary>String-table value: 0x07 string literal or 0x1C file path.</summary>
public sealed class NdfText(uint type, string text) : NdfValue
{
    public uint Type { get; } = type;
    public string Text { get; } = text;
    protected override bool EqualsCore(NdfValue other) => other is NdfText o && o.Type == Type && o.Text == Text;
    protected override int ComputeHash() => HashCode.Combine(Type, Text);
    public override string Describe() => $"'{Text}'";
}

/// <summary>Null object reference.</summary>
public sealed class NdfNull : NdfValue
{
    public static NdfNull Instance { get; } = new();
    private NdfNull() { }
    protected override bool EqualsCore(NdfValue other) => other is NdfNull;
    protected override int ComputeHash() => 6;
    public override string Describe() => "nil";
}

/// <summary>Reference to an object exported from another database (an IMPR entry such as $/GFX/Weapon/X).</summary>
public sealed class NdfImport(string path) : NdfValue
{
    public string Path { get; } = path;
    protected override bool EqualsCore(NdfValue other) => other is NdfImport o && o.Path == Path;
    protected override int ComputeHash() => HashCode.Combine(1, Path);
    public override string Describe() => Path;
}

/// <summary>Reference to an object exported from the same database.</summary>
public sealed class NdfExportRef(string path) : NdfValue
{
    public string Path { get; } = path;
    protected override bool EqualsCore(NdfValue other) => other is NdfExportRef o && o.Path == Path;
    protected override int ComputeHash() => HashCode.Combine(2, Path);
    public override string Describe() => Path;
}

public sealed class NdfObject(string className, IReadOnlyList<KeyValuePair<string, NdfValue>> fields) : NdfValue
{
    public string ClassName { get; } = className;
    public IReadOnlyList<KeyValuePair<string, NdfValue>> Fields { get; } = fields;

    public NdfValue? Get(string name)
    {
        foreach (var field in Fields) if (field.Key == name) return field.Value;
        return null;
    }

    protected override bool EqualsCore(NdfValue other)
    {
        if (other is not NdfObject o || o.ClassName != ClassName || o.Fields.Count != Fields.Count) return false;
        for (var i = 0; i < Fields.Count; i++)
            if (o.Fields[i].Key != Fields[i].Key || !o.Fields[i].Value.Equals(Fields[i].Value)) return false;
        return true;
    }

    protected override int ComputeHash()
    {
        var hash = new HashCode();
        hash.Add(ClassName);
        foreach (var field in Fields) { hash.Add(field.Key); hash.Add(field.Value.GetHashCode()); }
        return hash.ToHashCode();
    }

    public override string Describe() => ClassName + "(…)";
}

public sealed class NdfList(IReadOnlyList<NdfValue> items) : NdfValue
{
    public IReadOnlyList<NdfValue> Items { get; } = items;
    protected override bool EqualsCore(NdfValue other) => other is NdfList o && o.Items.SequenceEqual(Items);
    protected override int ComputeHash()
    {
        var hash = new HashCode();
        hash.Add(3);
        foreach (var item in Items) hash.Add(item.GetHashCode());
        return hash.ToHashCode();
    }
    public override string Describe() => $"[{Items.Count} items]";
}

public sealed class NdfMap(IReadOnlyList<KeyValuePair<NdfValue, NdfValue>> entries) : NdfValue
{
    public IReadOnlyList<KeyValuePair<NdfValue, NdfValue>> Entries { get; } = entries;
    protected override bool EqualsCore(NdfValue other)
    {
        if (other is not NdfMap o || o.Entries.Count != Entries.Count) return false;
        for (var i = 0; i < Entries.Count; i++)
            if (!o.Entries[i].Key.Equals(Entries[i].Key) || !o.Entries[i].Value.Equals(Entries[i].Value)) return false;
        return true;
    }
    protected override int ComputeHash()
    {
        var hash = new HashCode();
        hash.Add(4);
        foreach (var entry in Entries) { hash.Add(entry.Key.GetHashCode()); hash.Add(entry.Value.GetHashCode()); }
        return hash.ToHashCode();
    }
    public override string Describe() => $"MAP[{Entries.Count} entries]";
}

public sealed class NdfPair(NdfValue first, NdfValue second) : NdfValue
{
    public NdfValue First { get; } = first;
    public NdfValue Second { get; } = second;
    protected override bool EqualsCore(NdfValue other) => other is NdfPair o && o.First.Equals(First) && o.Second.Equals(Second);
    protected override int ComputeHash() => HashCode.Combine(5, First.GetHashCode(), Second.GetHashCode());
    public override string Describe() => $"({First.Describe()}, {Second.Describe()})";
}

/// <summary>A top-level object in TOPO order: exported by name, or anonymous (a shared private constant).</summary>
public sealed record NdfTopLevel(string? ExportPath, NdfObject Value)
{
    public object Key => ExportPath is null ? Value : ExportPath;
}

public sealed class NdfDatabase(IReadOnlyList<NdfTopLevel> topLevel, IReadOnlySet<NdfObject> sharedObjects,
    IReadOnlyDictionary<(string Class, string Property), string> propertyOwners)
{
    public IReadOnlyList<NdfTopLevel> TopLevel { get; } = topLevel;

    /// <summary>Declaring class of inherited properties, keyed by (object class, property).</summary>
    public IReadOnlyDictionary<(string Class, string Property), string> PropertyOwners { get; } = propertyOwners;

    /// <summary>Nested objects the compiler emitted once and referenced from several places.</summary>
    public IReadOnlySet<NdfObject> SharedObjects { get; } = sharedObjects;

    public static NdfDatabase Load(byte[] file) => NdfReader.Read(file);
    public byte[] Save() => NdfWriter.Write(this);

    internal static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}
