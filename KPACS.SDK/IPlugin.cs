namespace KPACS.SDK;

/// <summary>
/// Base interface that every K-PACS plugin must implement
/// (in-process .NET plugins) or that the gRPC adapter implements
/// on behalf of out-of-process plugins.
/// </summary>
public interface IPlugin : IAsyncDisposable
{
    /// <summary>Stable plugin identifier (matches <see cref="PluginManifest.Id"/>).</summary>
    string Id { get; }

    /// <summary>Parsed manifest for this plugin instance.</summary>
    PluginManifest Manifest { get; }

    /// <summary>Current lifecycle state.</summary>
    PluginState State { get; }

    /// <summary>
    /// Called once after the plugin process is started (out-of-process)
    /// or after assembly loading (in-process).
    /// Receives the working directory assigned by the host.
    /// </summary>
    Task InitializeAsync(PluginHostContext context, CancellationToken cancellationToken = default);

    /// <summary>Graceful shutdown. The host will call this before terminating the process.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Lifecycle states of a plugin instance.
/// </summary>
public enum PluginState
{
    /// <summary>Manifest discovered but plugin not yet started.</summary>
    Discovered,

    /// <summary>Process is launching / gRPC channel connecting.</summary>
    Starting,

    /// <summary>Plugin is ready to accept requests.</summary>
    Ready,

    /// <summary>Plugin is currently processing a request.</summary>
    Busy,

    /// <summary>Plugin encountered an error (see logs).</summary>
    Faulted,

    /// <summary>Plugin has been shut down.</summary>
    Stopped,
}

/// <summary>
/// Host-supplied context passed to plugins during initialisation.
/// </summary>
public sealed class PluginHostContext
{
    /// <summary>Writable scratch directory the plugin may use for temp files.</summary>
    public required string ScratchDirectory { get; init; }

    /// <summary>
    /// Directory where the host stores DICOM data.
    /// Plugins receive per-request paths; this is the root for reference only.
    /// </summary>
    public string? DataDirectory { get; init; }

    /// <summary>Host application version string.</summary>
    public string? HostVersion { get; init; }

    /// <summary>Arbitrary key-value pairs the host passes to the plugin.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}
