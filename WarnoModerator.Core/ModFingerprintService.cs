using System.Security.Cryptography;
using System.Text;

namespace WarnoModerator.Core;

public sealed class ModFingerprintService
{
    private const string StateFileName = ".warno-moderator.json";

    public async Task<IReadOnlyList<SourceModFingerprint>> ComputeAsync(
        IReadOnlyList<ModDescriptor> mods,
        IProgress<CombineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new CombineProgress(0, "Checking source mods"));

        var filesByMod = mods.Select(mod => new
        {
            Mod = mod,
            FileCount = EnumerateFiles(mod.RootPath).Count()
        }).ToArray();
        var totalFiles = filesByMod.Sum(item => item.FileCount);
        var completedFiles = 0;
        var results = new List<SourceModFingerprint>(mods.Count);

        foreach (var item in filesByMod)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 128];

            foreach (var file in EnumerateFiles(item.Mod.RootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(item.Mod.RootPath, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .ToUpperInvariant();
                var header = Encoding.UTF8.GetBytes(relativePath + "\0" + new FileInfo(file).Length + "\0");
                hash.AppendData(header);

                await using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, bytesRead);
                }

                hash.AppendData([0]);
                completedFiles++;
                if (completedFiles == totalFiles || completedFiles % 25 == 0)
                {
                    var percent = totalFiles == 0
                        ? 100
                        : (int)Math.Round(completedFiles * 100d / totalFiles);
                    progress?.Report(new CombineProgress(percent, "Checking source mods"));
                }
            }

            results.Add(new SourceModFingerprint(
                item.Mod.Name,
                Path.GetFullPath(item.Mod.RootPath),
                Convert.ToHexString(hash.GetHashAndReset())));
        }

        progress?.Report(new CombineProgress(100, "Source check complete"));
        return results;
    }

    private static IEnumerable<string> EnumerateFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new CombineException($"Source mod folder is missing: {rootPath}");
        }

        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(StateFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(rootPath, path), StringComparer.OrdinalIgnoreCase);
    }
}
