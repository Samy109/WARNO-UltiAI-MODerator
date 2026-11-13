using System.Diagnostics;

namespace WarnoModerator.Core;

/// <summary>
/// Finds a Python 3 interpreter for WARNO's SDK scripts. WARNO used to ship one in Mods\Utils\Python;
/// newer builds expect a system install instead (their .bat files call <c>python3</c> from PATH).
/// </summary>
public static class PythonLocator
{
    // GenerateMod.py uses f-strings.
    private static readonly Version MinimumVersion = new(3, 6);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public const string MissingMessage =
        "WARNO's mod tools need Python 3, which was not found on this PC. WARNO no longer bundles it. "
        + "Install Python 3 from python.org (tick \"Add python.exe to PATH\"), then try again.";

    /// <summary>Returns the full path of a working Python 3 interpreter, or throws <see cref="CombineException"/>.</summary>
    public static string Find(string modsRoot, Action<string>? log = null)
    {
        foreach (var (candidate, arguments) in Candidates(modsRoot))
        {
            var probe = Probe(candidate, arguments);
            if (probe is null) continue;
            log?.Invoke($"  Using Python {probe.Value.Version} at {probe.Value.Executable}");
            return probe.Value.Executable;
        }

        throw new CombineException(MissingMessage);
    }

    private static IEnumerable<(string Executable, string[] Arguments)> Candidates(string modsRoot)
    {
        // Older WARNO builds bundled their own interpreter.
        yield return (Path.Combine(modsRoot, "Utils", "Python", "python.exe"), []);

        // The py launcher resolves the newest installed Python 3 even when it is not on PATH.
        foreach (var launcher in OnPath("py.exe")
                     .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe")))
            yield return (launcher, ["-3"]);

        // What WARNO's own .bat files run. The Windows Store alias stubs fail the probe and are skipped.
        foreach (var name in new[] { "python3.exe", "python.exe" })
        foreach (var path in OnPath(name))
            yield return (path, []);

        // python.org installs made without "Add to PATH".
        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        foreach (var directory in SafeDirectories(root, "Python3*").OrderByDescending(VersionOf))
            yield return (Path.Combine(directory, "python.exe"), []);
    }

    private static (string Executable, Version Version)? Probe(string executable, string[] arguments)
    {
        if (!File.Exists(executable)) return null;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("import sys; print('%d.%d' % sys.version_info[:2]); print(sys.executable)");
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProbeTimeout))
            {
                process.Kill(true);
                return null;
            }
            if (process.ExitCode != 0) return null;
            var lines = stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 1 || !Version.TryParse(lines[0], out var version) || version < MinimumVersion) return null;
            // The launcher reports the interpreter it chose; run that directly so the SDK scripts see a plain python.exe.
            return (lines.Length > 1 && File.Exists(lines[1]) ? lines[1] : executable, version);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> OnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(directory => Path.Combine(directory.Trim('"'), fileName))
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeDirectories(string root, string pattern)
    {
        try { return string.IsNullOrEmpty(root) || !Directory.Exists(root) ? [] : Directory.GetDirectories(root, pattern); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return []; }
    }

    // "Python314" -> 314, so 3.14 sorts above 3.9.
    private static int VersionOf(string directory) =>
        int.TryParse(new string(Path.GetFileName(directory).SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
}
