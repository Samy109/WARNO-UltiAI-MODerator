using System.Buffers.Binary;
using System.Text;

namespace WarnoModerator.Core.Ndf;

public sealed class NdfFormatException(string message) : Exception(message);

/// <summary>EUG0/CNDF container: a 0x28-byte header followed by an LZ4 (1) or zstd (2) compressed body.</summary>
public static class NdfContainer
{
    public const int HeaderSize = 0x28;
    private const uint Lz4 = 1, Zstd = 2;

    public static bool IsNdfBinary(ReadOnlySpan<byte> data) =>
        data.Length >= 0x2C && data[..4].SequenceEqual("EUG0"u8) && data.Slice(8, 4).SequenceEqual("CNDF"u8);

    /// <summary>Returns the logical file: the header followed by the decompressed body.</summary>
    public static byte[] Decompress(byte[] file, out ulong tocOffset)
    {
        if (!IsNdfBinary(file)) throw new NdfFormatException("Not an EUG0/CNDF database.");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        tocOffset = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(16));
        var bodySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x28));
        var data = new byte[HeaderSize + bodySize];
        file.AsSpan(0, HeaderSize).CopyTo(data);
        var compressed = file.AsSpan(0x2C);
        var body = data.AsSpan(HeaderSize);
        var written = flags switch
        {
            Lz4 => Lz4Block.Decode(compressed, body),
            Zstd => new ZstdSharp.Decompressor().Unwrap(compressed, body),
            _ => throw new NdfFormatException($"Unsupported NDF compression {flags}.")
        };
        if (written != bodySize) throw new NdfFormatException("NDF body size mismatch.");
        return data;
    }

    /// <summary>Wraps a logical body (everything after the header) as a zstd-compressed container.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> body, ulong tocOffset)
    {
        using var compressor = new ZstdSharp.Compressor(3);
        var packed = compressor.Wrap(body);
        var file = new byte[0x2C + packed.Length];
        "EUG0"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 1);
        "CNDF"u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), Zstd);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(16), tocOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(24), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(32), (ulong)(HeaderSize + body.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x28), (uint)body.Length);
        packed.CopyTo(file.AsSpan(0x2C));
        return file;
    }
}

internal static class Lz4Block
{
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0, o = 0;
        while (i < src.Length)
        {
            var token = src[i++];
            var literals = token >> 4;
            if (literals == 15) { byte b; do { b = src[i++]; literals += b; } while (b == 255); }
            src.Slice(i, literals).CopyTo(dst[o..]);
            i += literals; o += literals;
            if (i >= src.Length) break;
            var offset = src[i] | (src[i + 1] << 8);
            i += 2;
            var length = token & 15;
            if (length == 15) { byte b; do { b = src[i++]; length += b; } while (b == 255); }
            length += 4;
            if (offset == 0 || offset > o) throw new NdfFormatException("Corrupt LZ4 block.");
            for (var k = 0; k < length; k++, o++) dst[o] = dst[o - offset];
        }
        return o;
    }
}

/// <summary>Eugen "edat" archive (Data/PC/&lt;version&gt;/*.dat) with its prefix-trie dictionary.</summary>
public sealed class EdatArchive
{
    public sealed record Entry(string Path, long Offset, long Size);
    private readonly string _path;
    public IReadOnlyDictionary<string, Entry> Entries { get; }

    public EdatArchive(string path)
    {
        _path = path;
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[0x28];
        stream.ReadExactly(head);
        if (!head[..4].SequenceEqual("edat"u8)) throw new NdfFormatException($"{path} is not an edat archive.");
        var dictOffset = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        var dictLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        var filesOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(head[16..]);
        var dictionary = new byte[dictLength];
        stream.Position = dictOffset;
        stream.ReadExactly(dictionary);
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        Walk(dictionary, 0, dictLength, "", filesOffset, entries);
        Entries = entries;
    }

    // Record: u32 headerLength (0 = file), u32 span to next sibling (0 = last), then a directory name
    // or a file's u64 offset, u64 size, 16-byte checksum and name. Names are prefixes of their children.
    private static void Walk(byte[] d, int p, int end, string prefix, long filesOffset, Dictionary<string, Entry> entries)
    {
        while (p < end)
        {
            var group = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
            var span = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4));
            var next = span != 0 ? p + span : end;
            if (group == 0)
            {
                var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 8));
                var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 16));
                var name = ReadCString(d, p + 40, out _);
                var full = prefix + name;
                entries[full] = new Entry(full, filesOffset + offset, size);
            }
            else
            {
                var name = ReadCString(d, p + 8, out var after);
                Walk(d, after, next, prefix + name, filesOffset, entries);
            }
            p = next;
        }
    }

    private static string ReadCString(byte[] d, int p, out int after)
    {
        var end = Array.IndexOf(d, (byte)0, p);
        after = end + 1;
        return Encoding.Latin1.GetString(d, p, end - p);
    }

    public byte[] Read(Entry entry)
    {
        using var stream = File.OpenRead(_path);
        stream.Position = entry.Offset;
        var data = new byte[entry.Size];
        stream.ReadExactly(data);
        return data;
    }
}

/// <summary>Vanilla compiled databases for one WARNO build, read from Data/PC/&lt;ModGenVersion&gt;/Glad.dat.</summary>
public sealed class VanillaDatabases
{
    private readonly EdatArchive? _archive;
    public string? ArchivePath { get; }

    private VanillaDatabases(EdatArchive? archive, string? path) { _archive = archive; ArchivePath = path; }

    public static VanillaDatabases Open(string warnoRoot, int? modGenVersion)
    {
        if (modGenVersion is not int version) return new(null, null);
        var path = Path.Combine(warnoRoot, "Data", "PC", version.ToString(), "Glad.dat");
        return File.Exists(path) ? new(new EdatArchive(path), path) : new(null, null);
    }

    public bool Available => _archive is not null;

    /// <summary>Gets the vanilla bytes for a mod-relative path such as Gen\NDF\GFX\Unit.ndfbin.</summary>
    public byte[]? TryRead(string relativePath)
    {
        if (_archive is null) return null;
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("Gen/", StringComparison.OrdinalIgnoreCase)) normalized = normalized[4..];
        return _archive.Entries.TryGetValue(normalized, out var entry) ? _archive.Read(entry) : null;
    }
}
