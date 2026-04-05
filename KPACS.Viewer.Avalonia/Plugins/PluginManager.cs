using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Net.Client;
using KPACS.RenderServer.Protos;
using KPACS.SDK;
using KPACS.SDK.Contracts;

namespace KPACS.Viewer.Plugins;

/// <summary>
/// Central registry that discovers, launches, and manages plugin lifecycles.
/// Thread-safe — all public methods may be called from any thread.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ConcurrentDictionary<string, PluginInstance> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _searchPaths = [];
    private readonly string _scratchRoot;
    private readonly string? _dataDirectory;
    private readonly string _hostVersion;

    public PluginManager(string scratchRoot, string? dataDirectory = null, string? hostVersion = null)
    {
        _scratchRoot = scratchRoot;
        _dataDirectory = dataDirectory;
        _hostVersion = hostVersion ?? "0.0.0";
        Directory.CreateDirectory(_scratchRoot);
    }

    /// <summary>All discovered plugins (started or not).</summary>
    public IReadOnlyCollection<PluginInstance> Plugins => [.. _plugins.Values];

    /// <summary>Raised when the plugin collection changes (discovery, start, stop, fault).</summary>
    public event Action? PluginsChanged;

    // ── Discovery ───────────────────────────────────────────────

    /// <summary>
    /// Add a directory to the search path and scan it for <c>plugin.json</c> manifests.
    /// Each immediate subdirectory that contains a <c>plugin.json</c> is treated as a plugin.
    /// </summary>
    public int DiscoverPlugins(string searchPath)
    {
        if (!Directory.Exists(searchPath))
        {
            return 0;
        }

        _searchPaths.Add(searchPath);
        int count = 0;

        foreach (string dir in Directory.GetDirectories(searchPath))
        {
            string manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(manifestPath);
                PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(json, ManifestJsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    continue;
                }

                var instance = new PluginInstance(manifest, dir);
                if (_plugins.TryAdd(manifest.Id, instance))
                {
                    count++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PluginManager] Failed to load manifest from {manifestPath}: {ex.Message}");
            }
        }

        if (count > 0)
        {
            PluginsChanged?.Invoke();
        }

        return count;
    }

    // ── Queries ─────────────────────────────────────────────────

    /// <summary>
    /// Query a remote render server for its available plugins and register
    /// them as <see cref="RemotePluginAdapter"/> instances. This enables
    /// thin clients to invoke plugins running on the server without any
    /// local plugin processes, Python installations, or GPU drivers.
    /// </summary>
    /// <param name="channel">gRPC channel to the render server.</param>
    /// <param name="sessionId">Active render-server session ID.</param>
    /// <param name="volumeId">Volume ID loaded in the session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of remote plugins registered.</returns>
    public async Task<int> RegisterRemotePluginsAsync(
        GrpcChannel channel, string sessionId, string volumeId, CancellationToken ct = default)
    {
        var client = new PluginProxyService.PluginProxyServiceClient(channel);
        ListPluginsResponse response = await client.ListPluginsAsync(new ListPluginsRequest(), cancellationToken: ct);

        int count = 0;
        foreach (PluginSummary summary in response.Plugins)
        {
            // Build a manifest from the server summary.
            var manifest = new PluginManifest
            {
                Id = summary.Id,
                Name = summary.Name,
                Version = summary.Version,
                Author = summary.Author,
                Description = summary.Description,
                License = summary.License,
                Capabilities = (PluginCapability)summary.Capabilities,
            };

            // Create the remote adapter (already in Ready state).
            var adapter = new RemotePluginAdapter(manifest, channel)
            {
                SessionId = sessionId,
                VolumeId = volumeId,
            };

            // Populate the task catalogue from the server.
            if (manifest.Capabilities.HasFlag(PluginCapability.Segmentation))
            {
                try
                {
                    await adapter.RefreshTaskCatalogAsync(ct);
                }
                catch
                {
                    // Best-effort — task list will be populated lazily.
                }
            }

            // Register with a "remote:" prefix to avoid ID collisions
            // with locally discovered plugins.
            string remoteId = $"remote:{summary.Id}";
            var remoteManifest = new PluginManifest
            {
                Id = remoteId,
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                License = manifest.License,
                Capabilities = manifest.Capabilities,
            };

            var instance = new PluginInstance(remoteManifest, pluginDirectory: string.Empty)
            {
                Handle = adapter,
                State = PluginState.Ready,
            };

            if (_plugins.TryAdd(remoteId, instance))
            {
                count++;
            }
        }

        if (count > 0)
        {
            PluginsChanged?.Invoke();
        }

        return count;
    }

    // ── Queries ─────────────────────────────────────────────────

    /// <summary>Find all plugins that declare a given capability.</summary>
    public IReadOnlyList<PluginInstance> GetPlugins(PluginCapability capability)
    {
        return _plugins.Values
            .Where(p => p.Manifest.Capabilities.HasFlag(capability))
            .ToList();
    }

    /// <summary>Look up a plugin by ID.</summary>
    public PluginInstance? GetPlugin(string pluginId)
    {
        _plugins.TryGetValue(pluginId, out PluginInstance? instance);
        return instance;
    }

    // ── Lifecycle ───────────────────────────────────────────────

    /// <summary>
    /// Start a plugin and return a ready-to-use <see cref="IPlugin"/> handle.
    /// For out-of-process plugins this spawns the child process, waits for gRPC readiness,
    /// and returns a <see cref="GrpcPluginAdapter"/>.
    /// </summary>
    public async Task<IPlugin> StartPluginAsync(string pluginId, CancellationToken ct = default)
    {
        if (!_plugins.TryGetValue(pluginId, out PluginInstance? instance))
        {
            throw new InvalidOperationException($"Plugin '{pluginId}' not found.");
        }

        if (instance.Handle is not null && instance.State == PluginState.Ready)
        {
            return instance.Handle;
        }

        // Clean up any previous (faulted or stopped) handle before restarting.
        if (instance.Handle is not null)
        {
            try { await instance.Handle.DisposeAsync(); }
            catch { /* best-effort cleanup */ }
            instance.Handle = null;
        }

        instance.State = PluginState.Starting;
        PluginsChanged?.Invoke();

        try
        {
            string scratchDir = Path.Combine(_scratchRoot, pluginId);
            Directory.CreateDirectory(scratchDir);

            var context = new PluginHostContext
            {
                ScratchDirectory = scratchDir,
                DataDirectory = _dataDirectory,
                HostVersion = _hostVersion,
            };

            IPlugin handle;

            if (instance.Manifest.Runtime is not null &&
                string.Equals(instance.Manifest.Runtime.Type, "dotnet", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(instance.Manifest.Runtime.Command) is false)
            {
                // In-process .NET plugin via isolated AssemblyLoadContext.
                handle = InProcessPluginLoader.Load(instance.Manifest, instance.PluginDirectory);
                await handle.InitializeAsync(context, ct);
            }
            else if (instance.Manifest.Runtime is not null)
            {
                // Out-of-process: spawn child process + gRPC adapter
                var processHost = new ProcessPluginHost(instance.Manifest, instance.PluginDirectory);
                await processHost.StartAsync(ct);

                var adapter = new GrpcPluginAdapter(instance.Manifest, processHost);
                await adapter.InitializeAsync(context, ct);
                handle = adapter;
            }
            else
            {
                throw new NotSupportedException(
                    $"Plugin '{pluginId}' must declare a Runtime section in its manifest " +
                    $"(type: \"python\", \"dotnet\", or \"executable\").");
            }

            instance.Handle = handle;
            instance.State = PluginState.Ready;
            PluginsChanged?.Invoke();
            return handle;
        }
        catch
        {
            instance.State = PluginState.Faulted;
            PluginsChanged?.Invoke();
            throw;
        }
    }

    /// <summary>Stop a running plugin gracefully.</summary>
    public async Task StopPluginAsync(string pluginId, CancellationToken ct = default)
    {
        if (!_plugins.TryGetValue(pluginId, out PluginInstance? instance) || instance.Handle is null)
        {
            return;
        }

        try
        {
            await instance.Handle.ShutdownAsync(ct);
        }
        finally
        {
            await instance.Handle.DisposeAsync();
            instance.Handle = null;
            instance.State = PluginState.Stopped;
            PluginsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Convenience: get a ready <see cref="ISegmentationProvider"/> from a plugin,
    /// starting it if necessary.
    /// </summary>
    public async Task<ISegmentationProvider> GetSegmentationProviderAsync(string pluginId, CancellationToken ct = default)
    {
        IPlugin handle = await StartPluginAsync(pluginId, ct);
        if (handle is ISegmentationProvider provider)
        {
            return provider;
        }

        throw new InvalidOperationException($"Plugin '{pluginId}' does not implement ISegmentationProvider.");
    }

    /// <summary>
    /// Convenience: get a ready <see cref="IImageProcessor"/> from a plugin.
    /// </summary>
    public async Task<IImageProcessor> GetImageProcessorAsync(string pluginId, CancellationToken ct = default)
    {
        IPlugin handle = await StartPluginAsync(pluginId, ct);
        if (handle is IImageProcessor processor)
        {
            return processor;
        }

        throw new InvalidOperationException($"Plugin '{pluginId}' does not implement IImageProcessor.");
    }

    /// <summary>
    /// Convenience: get a ready <see cref="IDicomAnalyzer"/> from a plugin.
    /// </summary>
    public async Task<IDicomAnalyzer> GetDicomAnalyzerAsync(string pluginId, CancellationToken ct = default)
    {
        IPlugin handle = await StartPluginAsync(pluginId, ct);
        if (handle is IDicomAnalyzer analyzer)
        {
            return analyzer;
        }

        throw new InvalidOperationException($"Plugin '{pluginId}' does not implement IDicomAnalyzer.");
    }

    // ── Disposal ────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        foreach (PluginInstance instance in _plugins.Values)
        {
            if (instance.Handle is not null)
            {
                try
                {
                    await instance.Handle.ShutdownAsync(CancellationToken.None);
                }
                catch
                {
                    // Best-effort shutdown — swallow errors during teardown.
                }

                await instance.Handle.DisposeAsync();
                instance.Handle = null;
            }

            instance.State = PluginState.Stopped;
        }

        _plugins.Clear();
    }
}
