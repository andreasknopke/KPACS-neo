using System.Diagnostics;

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

        // Build argument list, replacing ${port} token with "0" (plugin picks a free port).
        List<string> args = runtime.Args
            .Select(a => a.Replace("${port}", "0", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var psi = new ProcessStartInfo
        {
            FileName = runtime.Command,
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
        _process.Start();

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
}
