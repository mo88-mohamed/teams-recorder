using System.Diagnostics;

namespace TeamsObsRecorder.Platform;

internal static class Shell
{
    public static string? Which(string program)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, program))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Runs a program and returns (exit code, stdout). Exit code -1 means it timed out and was killed.</summary>
    public static (int ExitCode, string Output) Run(string program, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, "");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult());
    }
}
