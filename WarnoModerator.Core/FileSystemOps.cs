namespace WarnoModerator.Core;

internal static class FileSystemOps
{
    public static string SafeCombine(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new CombineException($"Unsafe path escaped the output directory: {relativePath}");
        }

        return combined;
    }

    public static void CopyFileAtomic(string source, string destination, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".warno-combiner-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(source, temporary, true);
        try
        {
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void CopyDirectory(string source, string destination, bool overwrite = true)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(SafeCombine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".tag", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = SafeCombine(destination, Path.GetRelativePath(source, file));
            if (overwrite || !File.Exists(target))
            {
                CopyFileAtomic(file, target, overwrite);
            }
        }
    }
}

