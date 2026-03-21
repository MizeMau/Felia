using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace F5TTS_Console;

/// <summary>
/// Launches and monitors the local Python F5-TTS FastAPI server as a child process.
/// Kills any process already occupying the port before starting.
/// The server is killed automatically when this object is disposed.
/// </summary>
internal sealed class ServerManager : IDisposable
{
    private const string ServerScript = "f5tts_server.py";
    private const string PythonExe = "python";
    private const string Host = "127.0.0.1";
    private const int Port = 7860;
    private const int StartupTimeoutMs = 120_000;
    private const int PollIntervalMs = 1_500;

    public string BaseUrl { get; } = $"http://{Host}:{Port}";

    private Process? _process;
    private bool _disposed;

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Kill any leftover server from a previous run before binding the port
        KillPortSquatter(Port);

        string scriptPath = ResolveScriptPath();

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"[Server] Starting F5-TTS server: {PythonExe} {scriptPath}");
        Console.ResetColor();

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{scriptPath}\" --host {Host} --port {Port}",
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.OutputDataReceived += (_, e) => PrintPython(e.Data, ConsoleColor.DarkGray);
        _process.ErrorDataReceived += (_, e) => PrintPython(e.Data, ConsoleColor.DarkGray);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForHealthAsync(ct);
    }

    public bool IsRunning => _process is { HasExited: false };

    // -----------------------------------------------------------------------
    // Kill whatever is listening on the port (Windows netstat)
    // -----------------------------------------------------------------------
    private static void KillPortSquatter(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var ns = Process.Start(psi)!;
            string output = ns.StandardOutput.ReadToEnd();
            ns.WaitForExit();

            var pattern = new Regex(
                $@"TCP\s+[\d.]+:{port}\s+[\d.:]+\s+\w+\s+(\d+)",
                RegexOptions.Multiline);

            var pids = pattern.Matches(output)
                              .Select(m => int.Parse(m.Groups[1].Value))
                              .Where(pid => pid > 0)
                              .Distinct()
                              .ToList();

            foreach (int pid in pids)
            {
                try
                {
                    using var victim = Process.GetProcessById(pid);
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[Server] Killing stale process on port {port} " +
                                      $"(PID {pid}: {victim.ProcessName})");
                    Console.ResetColor();
                    victim.Kill(entireProcessTree: true);
                    victim.WaitForExit(3_000);
                }
                catch { /* already gone */ }
            }

            if (pids.Count > 0)
                Thread.Sleep(600); // let the OS release the socket
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[Server] Port check skipped: {ex.Message}");
            Console.ResetColor();
        }
    }

    // -----------------------------------------------------------------------
    // Wait for /health to respond
    // -----------------------------------------------------------------------
    private async Task WaitForHealthAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var healthUrl = $"{BaseUrl}/health";
        var deadline = DateTime.UtcNow.AddMilliseconds(StartupTimeoutMs);

        Console.Write("[Server] Waiting for server to become ready");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
                throw new InvalidOperationException(
                    "[Server] Python process exited unexpectedly. " +
                    "Did you run install.bat?");

            try
            {
                var resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    Console.WriteLine(" ✓");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[Server] F5-TTS server is ready.");
                    Console.ResetColor();
                    return;
                }
            }
            catch { /* not up yet */ }

            Console.Write(".");
            await Task.Delay(PollIntervalMs, ct);
        }

        throw new TimeoutException("[Server] Timed out waiting for F5-TTS server to start.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static string ResolveScriptPath()
    {
        string exeDir = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(exeDir, "TTS", ServerScript),
            Path.Combine(Directory.GetCurrentDirectory(), "TTS", ServerScript),
        ];

        foreach (string c in candidates)
            if (File.Exists(c)) return c;

        var dir = new DirectoryInfo(exeDir);
        while (dir != null)
        {
            string path = Path.Combine(dir.FullName, "TTS", ServerScript);
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Cannot find '{ServerScript}'. Place it next to the executable.");
    }

    private static void PrintPython(string? line, ConsoleColor color)
    {
        if (string.IsNullOrEmpty(line)) return;
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"  [py] {line}");
        Console.ForegroundColor = prev;
    }

    // -----------------------------------------------------------------------
    // IDisposable
    // -----------------------------------------------------------------------
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(3_000); }
            catch { /* best effort */ }
        }

        _process?.Dispose();
    }
}