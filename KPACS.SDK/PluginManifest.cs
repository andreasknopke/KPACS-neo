using System.Text.Json.Serialization;

namespace KPACS.SDK;

/// <summary>
/// Describes a plugin — identity, capabilities, and how to launch it.
/// Serialised as <c>plugin.json</c> in each plugin directory.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Unique, stable plugin identifier (kebab-case, e.g. "totalsegmentator").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string Name { get; init; }

    /// <summary>SemVer version string.</summary>
    public required string Version { get; init; }

    /// <summary>Author or organisation.</summary>
    public string? Author { get; init; }

    /// <summary>Short description (one or two sentences).</summary>
    public string? Description { get; init; }

    /// <summary>SPDX licence identifier (e.g. "Apache-2.0").</summary>
    public string? License { get; init; }

    /// <summary>Declared capabilities — determines which contract interfaces the host queries.</summary>
    public PluginCapability Capabilities { get; init; }

    /// <summary>How to start the plugin process (null for in-process .NET plugins).</summary>
    public PluginRuntime? Runtime { get; init; }

    /// <summary>Optional: segmentation-specific task catalogue.</summary>
    public IReadOnlyList<SegmentationTaskEntry>? SegmentationTasks { get; init; }

    /// <summary>Optional: image-processing operation catalogue.</summary>
    public IReadOnlyList<ImageOperationEntry>? ImageOperations { get; init; }

    /// <summary>Optional: DICOM-analysis capability catalogue.</summary>
    public IReadOnlyList<DicomAnalysisEntry>? DicomAnalyses { get; init; }
}

/// <summary>
/// Tells the plugin host how to launch the out-of-process plugin.
/// </summary>
public sealed class PluginRuntime
{
    /// <summary>Runtime type: "python", "dotnet", "executable".</summary>
    public required string Type { get; init; }

    /// <summary>
    /// The command or executable to run.
    /// For <c>"python"</c> this is <c>"python"</c> or a path to a venv interpreter.
    /// For <c>"dotnet"</c> this is <c>"dotnet"</c>.
    /// For <c>"executable"</c> this is the path to the binary.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Command-line arguments.  The token <c>${port}</c> is replaced with the
    /// gRPC port assigned by the host at launch time.
    /// </summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Working directory relative to the plugin root (default: ".").</summary>
    public string WorkingDirectory { get; init; } = ".";

    /// <summary>Optional requirements file (e.g. "requirements.txt" for pip).</summary>
    public string? RequirementsFile { get; init; }

    /// <summary>Additional environment variables injected before launch.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

// ── Catalogue entries (lightweight descriptions shipped in the manifest) ──

public sealed class SegmentationTaskEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> SupportedModalities { get; init; } = [];
    public int StructureCount { get; init; }
    public bool RequiresLicense { get; init; }
}

public sealed class ImageOperationEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool SupportsVolumetric { get; init; }
}

public sealed class DicomAnalysisEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
