using System.Diagnostics;

namespace WarnoModerator.Core;

public interface IProcessRunner
{
    Task<int> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string> log,
        CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) log(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) log("ERROR: " + eventArgs.Data);
        };

        if (!process.Start())
        {
            throw new CombineException($"Could not start {Path.GetFileName(executable)}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            throw;
        }

        return process.ExitCode;
    }
}
