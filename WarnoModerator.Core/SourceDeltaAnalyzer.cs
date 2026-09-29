using System.IO.Compression;
using System.Security.Cryptography;

namespace WarnoModerator.Core;

public sealed class SourceDeltaAnalyzer
{
    private static readonly string[] SourceTrees = ["GameData", "CommonData"];

    public IReadOnlyList<SourceDelta> Analyze(ModDescriptor mod, CancellationToken cancellationToken = default)
    {
        if (mod.Kind != ModKind.EditableSource)
        {
            throw new ArgumentException("Source deltas require an editable mod.", nameof(mod));
        }

        if (!File.Exists(mod.BaseZipPath))
        {
            throw new CombineException($"{mod.Name} is missing base.zip.");
        }

        using var archive = ZipFile.OpenRead(mod.BaseZipPath);
        var entries = archive.Entries
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .ToDictionary(
                x => NormalizeRelativePath(x.FullName),
                StringComparer.OrdinalIgnoreCase);
        var diskFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tree in SourceTrees)
        {
            var treePath = Path.Combine(mod.RootPath, tree);
            if (!Directory.Exists(treePath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(treePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(Path.GetRelativePath(mod.RootPath, file));
                diskFiles[relative] = file;
            }
        }

        var deltas = new List<SourceDelta>();
        foreach (var pair in diskFiles)
        {
            if (!entries.TryGetValue(pair.Key, out var entry))
            {
                deltas.Add(new SourceDelta(pair.Key, DeltaKind.Added, pair.Value));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var diskStream = File.OpenRead(pair.Value);
            using var zipStream = entry.Open();
            if (!HashStream(diskStream, cancellationToken).AsSpan().SequenceEqual(HashStream(zipStream, cancellationToken)))
            {
                deltas.Add(new SourceDelta(pair.Key, DeltaKind.Modified, pair.Value));
            }
        }

        foreach (var entry in entries.Keys)
        {
            if (SourceTrees.Any(tree => entry.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase))
                && !diskFiles.ContainsKey(entry))
            {
                deltas.Add(new SourceDelta(entry, DeltaKind.Deleted, null));
            }
        }

        return deltas.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string ComputeSha256(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(HashStream(stream, cancellationToken));
    }

    private static byte[] HashStream(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return hash.GetHashAndReset();
    }
    private static string NormalizeRelativePath(string path) =>
        path.Replace('/', '\\').TrimStart('\\');
}
