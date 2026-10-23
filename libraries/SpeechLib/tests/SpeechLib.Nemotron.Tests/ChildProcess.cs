using System.Diagnostics;
using System.Text;

namespace SpeechLib.Nemotron.Tests;

/// <summary>What a child process did: how it ended and what it printed.</summary>
/// <param name="ExitCode">The exit code; on Windows a native crash shows as an NTSTATUS such as 0xC0000409 (fail-fast) or 0xC0000005 (access violation).</param>
/// <param name="TimedOut">The process was killed because it ran longer than allowed (a hang).</param>
internal sealed record ChildResult(int ExitCode, bool TimedOut, string Output, string Error)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>A line that says how the process ended, with the exit code in hex (<c>0xC0000409</c>), for assertion messages.</summary>
    public string Describe() => TimedOut ? "timed out (hang), killed" : $"exit code {ExitCode} (0x{unchecked((uint)ExitCode):X8})";

    /// <summary>The end of both streams, for assertion messages.</summary>
    public string Tail(int lines = 25) =>
        $"--- stdout ---\n{string.Join('\n', Output.Split('\n').TakeLast(lines))}\n--- stderr ---\n{string.Join('\n', Error.Split('\n').TakeLast(lines))}";
}

/// <summary>
/// Runs a program in a child process with a time limit. Everything that touches the WebGPU plugin runs this way: a driver or plugin
/// fault ends the process (fail-fast, heap corruption, access violation, a hang) and must not take the test host with it; the test
/// then fails with the exit code.
/// </summary>
internal static class ChildProcess
{
    public static ChildResult Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                info.Environment[key] = value;

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (error) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = !process.WaitForExit(timeout);
        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended between the timeout and the kill
            }
            process.WaitForExit();
        }
        else
        {
            process.WaitForExit();   // flush the redirected streams
        }

        lock (output)
            lock (error)
                return new ChildResult(timedOut ? -1 : process.ExitCode, timedOut, output.ToString(), error.ToString());
    }
}
