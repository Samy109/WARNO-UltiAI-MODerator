using System.Security.Cryptography;
using System.Text;

namespace WarnoModerator.Core;

public sealed class ModFingerprintService
{

    public async Task<IReadOnlyList<SourceModFingerprint>> ComputeAsync(
        IReadOnlyList<ModDescriptor> mods,
        IProgress<CombineProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string stageLabel = "Checking source mods")
    {
        progress?.Report(new CombineProgress(0, stageLabel));

        var filesByMod = mods.Select(mod => new
        {
            Mod = mod,
            Files = EnumerateFiles(mod.RootPath).Select(file => { cancellationToken.ThrowIfCancellationRequested(); return file; }).ToArray()
        }).ToArray();
        var totalFiles = filesByMod.Sum(item => item.Files.Length);
        var completedFiles = 0;
        var tasks = filesByMod.Select(item => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 512];

            foreach (var file in item.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(item.Mod.RootPath, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .ToUpperInvariant();
                var header = Encoding.UTF8.GetBytes(relativePath + "\0" + new FileInfo(file).Length + "\0");
                hash.AppendData(header);

                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1024 * 512,
                    FileOptions.SequentialScan);
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, bytesRead);
                }

                hash.AppendData([0]);
                var done = Interlocked.Increment(ref completedFiles);
                if (done == totalFiles || done % 25 == 0)
                {
                    var percent = totalFiles == 0
                        ? 100
                        : (int)Math.Round(done * 100d / totalFiles);
                    progress?.Report(new CombineProgress(percent, stageLabel));
                }
            }

            return new SourceModFingerprint(
                item.Mod.Name,
                Path.GetFullPath(item.Mod.RootPath),
                Convert.ToHexString(hash.GetHashAndReset()));
        }, cancellationToken)).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        progress?.Report(new CombineProgress(100, stageLabel));
        return results;
    }

    private static IEnumerable<string> EnumerateFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new CombineException($"Source mod folder is missing: {rootPath}");
        }

        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(CombinedModStateStore.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(rootPath, path), StringComparer.OrdinalIgnoreCase);
    }
}
