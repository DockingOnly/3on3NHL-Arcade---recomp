using System.Diagnostics;
using System.Text;

namespace Nhl3on3Launcher;

static class ProcessRunner
{
    /// <summary>Runs a console program hidden, passing each output line to <paramref name="onLine"/>.
    /// Cancelling kills the whole process tree. The build runs below normal priority, which its
    /// child processes (ninja, clang) inherit, so the PC stays responsive.</summary>
    public static async Task<int> RunAsync(string fileName, IEnumerable<string> args, string workingDir,
        IDictionary<string, string>? environment, Action<string> onLine, CancellationToken cancel,
        bool belowNormal = true)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (environment != null)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in environment) psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.Start();
        if (belowNormal)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
        process.WaitForExit();  // flush the redirected streams
        return process.ExitCode;
    }

    /// <summary>Runs a program and returns its whole output.</summary>
    public static async Task<(int ExitCode, string Output)> CaptureAsync(string fileName, string arguments,
        CancellationToken cancel = default, IDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv) psi.Environment[k] = v;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancel);
        _ = process.StandardError.ReadToEndAsync(cancel);
        await process.WaitForExitAsync(cancel);
        return (process.ExitCode, await stdout);
    }
}
