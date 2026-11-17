using System.IO.Compression;
using System.Security.Cryptography;

namespace WarnoModerator.Core;

/// <summary>
/// Vanilla's source files from WARNO's ModData\base.zip. A compiled mod that ships one of these files byte for byte
/// has not changed it, however many other mods did.
/// </summary>
public sealed class SourceBaseline
{
    private static readonly string[] SourceTrees = ["GameData", "CommonData"];
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    private SourceBaseline(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = entry.FullName.Replace('/', '\\').TrimStart('\\');
            if (!SourceTrees.Any(tree => path.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            using var stream = entry.Open();
            _hashes[path] = Convert.ToHexString(SHA256.HashData(stream));
        }
    }

    /// <summary>Hashes vanilla's source files, or returns null when base.zip is missing or unreadable.</summary>
    public static SourceBaseline? TryOpen(string? zipPath)
    {
        if (zipPath is null || !File.Exists(zipPath)) return null;
        try { return new SourceBaseline(zipPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when vanilla ships this file and <paramref name="file"/> is identical to it.</summary>
    public bool IsVanilla(string relativePath, string file, CancellationToken token) =>
        _hashes.TryGetValue(relativePath.Replace('/', '\\').TrimStart('\\'), out var hash)
        && SourceDeltaAnalyzer.ComputeSha256(file, token) == hash;
}
