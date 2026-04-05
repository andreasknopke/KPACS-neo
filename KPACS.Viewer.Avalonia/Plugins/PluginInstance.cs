using KPACS.SDK;

namespace KPACS.Viewer.Plugins;

/// <summary>
/// Runtime bookkeeping for a single discovered plugin.
/// Tracks the manifest, directory, current state, and live handle.
/// </summary>
public sealed class PluginInstance
{
    public PluginInstance(PluginManifest manifest, string pluginDirectory)
    {
        Manifest = manifest;
        PluginDirectory = pluginDirectory;
    }

    /// <summary>Parsed manifest from <c>plugin.json</c>.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>Absolute path to the directory containing <c>plugin.json</c>.</summary>
    public string PluginDirectory { get; }

    /// <summary>Current lifecycle state.</summary>
    public PluginState State { get; internal set; } = PluginState.Discovered;

    /// <summary>
    /// Live plugin handle (non-null when the plugin is started).
    /// Implements <see cref="IPlugin"/> and optionally one or more capability interfaces.
    /// </summary>
    public IPlugin? Handle { get; internal set; }
}
