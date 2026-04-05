using System.Diagnostics;
using System.Runtime.InteropServices;

using KPACS.SDK;

namespace KPACS.Viewer.Plugins;

/// <summary>
/// Manages the child process for an out-of-process plugin.
/// Starts the process, captures its stdout for the gRPC port announcement,
/// monitors health, and terminates on disposal.
/// </summary>
internal sealed class ProcessPluginHost : IAsyncDisposable
{
    /// <summary>
    /// The plugin writes this prefix to stdout once its gRPC server is listening.
    /// Format: <c>KPACS_PLUGIN_PORT=12345</c>
    /// </summary>
    private const string PortAnnouncement = "KPACS_PLUGIN_PORT=";

    private readonly PluginManifest _manifest;
    private readonly string _pluginDirectory;
    private Process? _process;
    private int _port;

    public ProcessPluginHost(PluginManifest manifest, string pluginDirectory)
    {
        _manifest = manifest;
        _pluginDirectory = pluginDirectory;
    }

    /// <summary>The gRPC port the child process is listening on.</summary>
    public int Port => _port;

    /// <summary>Whether the child process is still alive.</summary>
    public bool IsRunning => _process is not null && !_process.HasExited;

    /// <summary>
    /// Launch the plugin process.
    /// Blocks (async) until the process writes its port announcement to stdout,
    /// or throws on timeout / early exit.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_manifest.Runtime is null)
        {
            throw new InvalidOperationException("Plugin manifest does not declare a Runtime section.");
        }

        PluginRuntime runtime = _manifest.Runtime;
        string workingDir = Path.GetFullPath(
            Path.Combine(_pluginDirectory, runtime.WorkingDirectory));

        // Resolve the command (handles "python" → actual interpreter on this OS).
        (string resolvedCommand, string[] extraArgs) = ResolveCommand(runtime.Type, runtime.Command);

        // Auto-install Python dependencies before launching.
        if (string.Equals(runtime.Type, "python", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(runtime.RequirementsFile))
        {
            string reqPath = Path.GetFullPath(Path.Combine(_pluginDirectory, runtime.RequirementsFile));
            if (File.Exists(reqPath))
            {
                await InstallPipRequirementsAsync(resolvedCommand, extraArgs, reqPath, workingDir, ct);
            }
        }

        // Auto-generate gRPC stubs if generate_proto.py exists and *_pb2.py files are missing.
        if (string.Equals(runtime.Type, "python", StringComparison.OrdinalIgnoreCase))
        {
            await GenerateProtoStubsIfNeededAsync(resolvedCommand, extraArgs, workingDir, ct);
        }

        // Build argument list, replacing ${port} token with "0" (plugin picks a free port).
        List<string> args = [
            .. extraArgs,
            .. runtime.Args
                .Select(a => a.Replace("${port}", "0", StringComparison.OrdinalIgnoreCase)),
        ];

        var psi = new ProcessStartInfo
        {
            FileName = resolvedCommand,
            Arguments = string.Join(' ', args.Select(QuoteArg)),
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (runtime.EnvironmentVariables is not null)
        {
            foreach ((string key, string value) in runtime.EnvironmentVariables)
            {
                psi.EnvironmentVariables[key] = value;
            }
        }

        _process = new Process { StartInfo = psi };

        try
        {
            _process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            string hint = string.Equals(runtime.Type, "python", StringComparison.OrdinalIgnoreCase)
                ? $"The plugin '{_manifest.Id}' requires Python 3 but no working Python interpreter " +
                  $"was found on this system (tried: {resolvedCommand}). " +
                  "Please install Python 3.10+ from https://www.python.org/downloads/ and ensure " +
                  "it is on your PATH, then restart the viewer."
                : $"Could not start plugin '{_manifest.Id}': the command '{resolvedCommand}' was not found. " +
                  $"Ensure the required runtime is installed and on your PATH.";

            throw new InvalidOperationException(hint, ex);
        }

        // Read stdout lines until we get the port announcement or the process dies.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                string? line = await _process.StandardOutput.ReadLineAsync(timeoutCts.Token);
                if (line is null)
                {
                    // Process closed stdout — likely crashed.
                    string stderr = await _process.StandardError.ReadToEndAsync(ct);
                    throw new InvalidOperationException(
                        $"Plugin '{_manifest.Id}' process exited before announcing its port. " +
                        $"stderr: {Truncate(stderr, 500)}");
                }

                Debug.WriteLine($"[Plugin:{_manifest.Id}] {line}");

                if (line.StartsWith(PortAnnouncement, StringComparison.Ordinal))
                {
                    string portStr = line[PortAnnouncement.Length..].Trim();
                    if (int.TryParse(portStr, out int port) && port > 0)
                    {
                        _port = port;
                        // Continue reading stdout in background to keep the pipe drained.
                        _ = DrainStreamAsync(_process.StandardOutput);
                        _ = DrainStreamAsync(_process.StandardError);
                        return;
                    }
                }
            }

            throw new TimeoutException(
                $"Plugin '{_manifest.Id}' did not announce its gRPC port within the timeout.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Plugin '{_manifest.Id}' did not announce its gRPC port within 120 seconds.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Best-effort termination.
            }
        }

        _process.Dispose();
        _process = null;
    }

    private static async Task DrainStreamAsync(System.IO.StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
                // Discard — just keep the pipe from blocking.
            }
        }
        catch
        {
            // Process exited — expected.
        }
    }

    private static string QuoteArg(string arg)
    {
        return arg.Contains(' ') ? $"\"{arg}\"" : arg;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    // ── Pip dependency installation ──────────────────────────────

    /// <summary>
    /// Run <c>pip install -r requirements.txt</c> (or equivalent) to ensure
    /// the plugin's Python dependencies are present before launch.
    /// </summary>
    private static async Task InstallPipRequirementsAsync(
        string pythonCommand, string[] pythonExtraArgs, string requirementsPath,
        string workingDir, CancellationToken ct)
    {
        // Build: python [-3] -m pip install --quiet -r requirements.txt
        List<string> pipArgs = [.. pythonExtraArgs, "-m", "pip", "install", "--quiet", "-r", requirementsPath];

        var psi = new ProcessStartInfo
        {
            FileName = pythonCommand,
            Arguments = string.Join(' ', pipArgs.Select(QuoteArg)),
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Debug.WriteLine($"[ProcessPluginHost] Installing pip requirements: {psi.FileName} {psi.Arguments}");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pip install process.");

        // Drain stdout/stderr so the process doesn't block.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            Debug.WriteLine($"[ProcessPluginHost] pip install failed (exit {proc.ExitCode}): {Truncate(stderr, 500)}");
            throw new InvalidOperationException(
                $"Failed to install plugin Python dependencies (pip exit code {proc.ExitCode}). " +
                $"{Truncate(stderr, 400)}");
        }

        Debug.WriteLine($"[ProcessPluginHost] pip install succeeded. {Truncate(stdout, 200)}");
    }

    /// <summary>
    /// If the plugin directory contains <c>generate_proto.py</c> and no
    /// <c>*_pb2.py</c> files exist, run the generator so that gRPC stubs
    /// are available before the plugin process starts.
    /// </summary>
    private static async Task GenerateProtoStubsIfNeededAsync(
        string pythonCommand, string[] pythonExtraArgs, string workingDir, CancellationToken ct)
    {
        string generatorScript = Path.Combine(workingDir, "generate_proto.py");
        if (!File.Exists(generatorScript))
        {
            return;
        }

        // Check whether *_pb2.py files already exist.
        bool stubsExist = Directory.EnumerateFiles(workingDir, "*_pb2.py").Any()
                       || Directory.EnumerateFiles(workingDir, "*_pb2_grpc.py").Any();
        if (stubsExist)
        {
            return;
        }

        Debug.WriteLine($"[ProcessPluginHost] Generating gRPC stubs via {generatorScript}");

        List<string> args = [.. pythonExtraArgs, generatorScript];
        var psi = new ProcessStartInfo
        {
            FileName = pythonCommand,
            Arguments = string.Join(' ', args.Select(QuoteArg)),
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start proto generation process.");

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            Debug.WriteLine($"[ProcessPluginHost] Proto generation failed (exit {proc.ExitCode}): {Truncate(stderr, 500)}");
            throw new InvalidOperationException(
                $"Failed to generate gRPC stubs for plugin (exit code {proc.ExitCode}). " +
                $"{Truncate(stderr, 400)}");
        }

        Debug.WriteLine($"[ProcessPluginHost] Proto generation succeeded. {Truncate(stdout, 200)}");
    }

    // ── Python / command resolution ──────────────────────────────

    /// <summary>
    /// Resolve the runtime command to an actual executable path.
    /// For <c>"python"</c> runtimes this probes multiple well-known candidates
    /// so we work on systems where only <c>py</c>, <c>python3</c>, or a Store
    /// stub is installed.
    /// </summary>
    /// <returns>
    /// A tuple of (executable, extraArgs). <c>extraArgs</c> is non-empty when
    /// the launcher needs a version flag (e.g. <c>py -3</c>).
    /// </returns>
    private static (string Command, string[] ExtraArgs) ResolveCommand(string runtimeType, string declaredCommand)
    {
        if (string.Equals(runtimeType, "python", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePythonCommand(declaredCommand);
        }

        // Other runtime types: use the command as-is.
        return (declaredCommand, []);
    }

    /// <summary>
    /// Find a working Python 3 interpreter on this machine.
    /// Tries PATH-based commands first, then well-known installation directories
    /// (conda, miniconda, pyenv, standard python.org locations).
    /// Each candidate is tested with <c>--version</c> to confirm it is actually usable.
    /// </summary>
    private static (string Command, string[] ExtraArgs) ResolvePythonCommand(string declaredCommand)
    {
        // Phase 1: PATH-based commands.
        List<(string cmd, string[] extra)> candidates = [(declaredCommand, [])];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add(("python3", []));
            candidates.Add(("py", ["-3"]));
        }
        else
        {
            candidates.Add(("python3", []));
        }

        foreach ((string cmd, string[] extra) in candidates)
        {
            if (ProbePython(cmd, extra))
            {
                Debug.WriteLine($"[ProcessPluginHost] Resolved Python via PATH: {cmd} {string.Join(' ', extra)}");
                return (cmd, extra);
            }
        }

        // Phase 2: Well-known installation directories.
        List<string> probePaths = [];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Conda / Miniconda / Anaconda — user-level installs
            probePaths.Add(Path.Combine(home, "miniconda3", "python.exe"));
            probePaths.Add(Path.Combine(home, "anaconda3", "python.exe"));
            probePaths.Add(Path.Combine(home, "Miniconda3", "python.exe"));
            probePaths.Add(Path.Combine(home, "Anaconda3", "python.exe"));

            // Conda / Miniconda — common system-wide locations
            probePaths.Add(@"C:\Miniconda3\python.exe");
            probePaths.Add(@"C:\Anaconda3\python.exe");
            probePaths.Add(@"C:\ProgramData\miniconda3\python.exe");
            probePaths.Add(@"C:\ProgramData\anaconda3\python.exe");

            // pyenv-win
            probePaths.Add(Path.Combine(home, ".pyenv", "pyenv-win", "shims", "python.exe"));

            // Standard python.org installs (Python 3.10–3.14)
            for (int minor = 14; minor >= 10; minor--)
            {
                probePaths.Add(Path.Combine(localAppData, "Programs", "Python", $"Python3{minor}", "python.exe"));
                probePaths.Add($@"C:\Python3{minor}\python.exe");
            }
        }
        else
        {
            // Linux / macOS
            probePaths.Add(Path.Combine(home, "miniconda3", "bin", "python3"));
            probePaths.Add(Path.Combine(home, "anaconda3", "bin", "python3"));
            probePaths.Add(Path.Combine(home, ".pyenv", "shims", "python3"));
            probePaths.Add("/usr/local/bin/python3");
            probePaths.Add("/opt/homebrew/bin/python3");
        }

        foreach (string path in probePaths)
        {
            if (File.Exists(path) && ProbePython(path, []))
            {
                Debug.WriteLine($"[ProcessPluginHost] Resolved Python at known path: {path}");
                return (path, []);
            }
        }

        // None found — return the declared command so the caller gets a clear
        // "file not found" error that includes the expected name.
        Debug.WriteLine(
            "[ProcessPluginHost] No working Python interpreter found. " +
            $"Tried {candidates.Count} PATH commands and {probePaths.Count} known paths.");

        return (declaredCommand, []);
    }

    /// <summary>
    /// Quick probe: can we run <c>&lt;cmd&gt; [extra] --version</c> successfully?
    /// </summary>
    private static bool ProbePython(string command, string[] extraArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(' ', [.. extraArgs, "--version"]),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            // File not found, access denied, etc.
            return false;
        }
    }
}
