// ------------------------------------------------------------------------------------------------
// StudyViewerWindow — Plugin Segmentation Panel
//
// Adds a "Segmentation" section to the anatomy workspace panel that lets
// the user pick a segmentation plugin and task, run the segmentation,
// track progress, and import the resulting masks into the study measurements.
//
// Works transparently in both local and thin-client (remote render-server) mode:
//   • Local mode: PluginManager discovers and runs plugins on the local machine.
//   • Thin-client mode: RegisterRemotePluginsAsync queries the render server
//     for server-side plugins; RemotePluginAdapter forwards execution to the server.
// ------------------------------------------------------------------------------------------------

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Grpc.Net.Client;
using KPACS.SDK;
using KPACS.SDK.Contracts;
using KPACS.SDK.Models;
using KPACS.Viewer.Models;
using KPACS.Viewer.Plugins;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    // ── Segmentation panel state ─────────────────────────────────

    private PluginManager? _pluginManager;
    private bool _pluginDiscoveryStarted;
    private bool _segmentationSectionExpanded = true;
    private string? _selectedSegmentationPluginId;
    private string? _selectedSegmentationTaskId;
    private bool _segmentationRunning;
    private CancellationTokenSource? _segmentationCancellation;

    /// <summary>
    /// Asynchronously register remote (render-server) plugins and refresh the
    /// anatomy panel when done. Called fire-and-forget from
    /// <see cref="BuildSegmentationSection"/> in thin-client mode.
    /// </summary>
    private async Task RegisterRemotePluginsAndRefreshAsync()
    {
        try
        {
            if (_pluginManager is null || _context.RenderServerConnection is null)
            {
                return;
            }

            using var channel = RenderServerGrpcClientFactory.CreateChannel(
                _context.RenderServerConnection.ServerUrl);

            await _pluginManager.RegisterRemotePluginsAsync(
                channel, string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Segmentation] Failed to discover remote plugins: {ex.Message}");
        }

        // Rebuild the panel on the UI thread so newly discovered remote plugins appear.
        await Dispatcher.UIThread.InvokeAsync(() => RefreshAnatomyPanel());
    }

    // ── UI construction ──────────────────────────────────────────

    /// <summary>
    /// Build the "Segmentation" section for the anatomy workspace panel.
    /// Called from <see cref="RefreshAnatomyPanel"/>.
    /// </summary>
    private Control BuildSegmentationSection()
    {
        // Lazily create the plugin manager and run synchronous local discovery.
        if (_pluginManager is null && !_pluginDiscoveryStarted)
        {
            _pluginDiscoveryStarted = true;

            string scratchRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KPACS.Viewer.Avalonia", "plugin-scratch");

            string appDir = (Application.Current as App)?.Paths.ApplicationDirectory
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            _pluginManager = new PluginManager(scratchRoot, dataDirectory: appDir);

            // Synchronous local discovery.
            string localPluginDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
            int localCount = _pluginManager.DiscoverPlugins(localPluginDir);
            System.Diagnostics.Debug.WriteLine(
                $"[Segmentation] Discovered {localCount} local plugin(s) from {localPluginDir}");

            string userPluginDir = Path.Combine(appDir, "Plugins");
            _pluginManager.DiscoverPlugins(userPluginDir);

            // Async remote plugin registration (thin-client mode) — fire-and-forget,
            // refresh the panel when done.
            if (IsRenderServerStudy && _context.RenderServerConnection is not null)
            {
                _ = RegisterRemotePluginsAndRefreshAsync();
            }
        }

        IReadOnlyCollection<PluginInstance> allPlugins = _pluginManager?.Plugins ?? [];
        IReadOnlyList<PluginInstance> segmentationPlugins = allPlugins
            .Where(p => p.Manifest.Capabilities.HasFlag(PluginCapability.Segmentation))
            .ToList();

        string hint = segmentationPlugins.Count == 0
            ? "No segmentation plugins available. Place plugins in the Plugins/ folder next to the viewer, or connect to a render server with installed plugins."
            : _segmentationRunning
                ? "Segmentation is running — please wait for it to complete."
                : "Choose a plugin and task, then run segmentation on the active volume.";

        (Border section, StackPanel body) = CreateAnatomySectionHost(
            "AI Segmentation",
            hint,
            _segmentationSectionExpanded,
            expanded => _segmentationSectionExpanded = expanded);

        if (segmentationPlugins.Count == 0)
        {
            return section;
        }

        // ── Plugin picker ───────────────────────────────────────

        List<string> pluginDisplayNames = segmentationPlugins
            .Select(p => $"{p.Manifest.Name} ({p.Manifest.Version})")
            .ToList();
        List<string> pluginIds = segmentationPlugins
            .Select(p => p.Manifest.Id)
            .ToList();

        int selectedPluginIndex = _selectedSegmentationPluginId is not null
            ? pluginIds.IndexOf(_selectedSegmentationPluginId)
            : 0;
        if (selectedPluginIndex < 0) selectedPluginIndex = 0;

        var pluginCombo = new ComboBox
        {
            ItemsSource = pluginDisplayNames,
            SelectedIndex = selectedPluginIndex,
            MinWidth = 220,
            MaxWidth = 340,
        };
        StyleAnatomyComboBox(pluginCombo);

        _selectedSegmentationPluginId = pluginIds[selectedPluginIndex];

        // ── Task picker (populated from selected plugin) ────────

        PluginInstance selectedPlugin = segmentationPlugins[selectedPluginIndex];
        IReadOnlyList<SegmentationTaskInfo> tasks = [];

        if (selectedPlugin.Handle is ISegmentationProvider provider)
        {
            tasks = provider.AvailableTasks;
        }
        else if (selectedPlugin.Manifest.SegmentationTasks is not null)
        {
            tasks = selectedPlugin.Manifest.SegmentationTasks
                .Select(e => new SegmentationTaskInfo
                {
                    Id = e.Id,
                    Name = e.Name,
                    Description = e.Description,
                    SupportedModalities = e.SupportedModalities,
                    StructureCount = e.StructureCount,
                    RequiresLicense = e.RequiresLicense,
                })
                .ToList();
        }

        List<string> taskDisplayNames = tasks.Count > 0
            ? tasks.Select(t => $"{t.Name} ({t.StructureCount} structures)").ToList()
            : ["(no tasks defined)"];
        List<string> taskIds = tasks.Count > 0
            ? tasks.Select(t => t.Id).ToList()
            : [string.Empty];

        int selectedTaskIndex = _selectedSegmentationTaskId is not null
            ? taskIds.IndexOf(_selectedSegmentationTaskId)
            : 0;
        if (selectedTaskIndex < 0) selectedTaskIndex = 0;

        var taskCombo = new ComboBox
        {
            ItemsSource = taskDisplayNames,
            SelectedIndex = selectedTaskIndex,
            MinWidth = 220,
            MaxWidth = 340,
        };
        StyleAnatomyComboBox(taskCombo);

        if (taskIds.Count > 0)
        {
            _selectedSegmentationTaskId = taskIds[selectedTaskIndex];
        }

        pluginCombo.SelectionChanged += (_, _) =>
        {
            if (pluginCombo.SelectedIndex >= 0 && pluginCombo.SelectedIndex < pluginIds.Count)
            {
                _selectedSegmentationPluginId = pluginIds[pluginCombo.SelectedIndex];
                _selectedSegmentationTaskId = null;
                RefreshAnatomyPanel();
            }
        };

        taskCombo.SelectionChanged += (_, _) =>
        {
            if (taskCombo.SelectedIndex >= 0 && taskCombo.SelectedIndex < taskIds.Count)
            {
                _selectedSegmentationTaskId = taskIds[taskCombo.SelectedIndex];
            }
        };

        // ── Progress bar ─────────────────────────────────────────

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            IsVisible = _segmentationRunning,
            IsIndeterminate = false,
        };

        var statusText = new TextBlock
        {
            Text = _segmentationRunning ? "Preparing…" : string.Empty,
            Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
            FontSize = 10,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = _segmentationRunning,
        };

        // ── Action buttons ──────────────────────────────────────

        var runButton = CreateAnatomyActionButton(
            "Run segmentation", "#FF215E91", "#FF4FA3FF", minWidth: 148, height: 30);
        runButton.IsEnabled = !_segmentationRunning && tasks.Count > 0;
        runButton.Click += async (_, _) =>
        {
            await RunSegmentationAsync(progressBar, statusText);
        };

        var cancelButton = CreateAnatomyActionButton(
            "Cancel", "#FF5C2431", "#FFEB7D96", minWidth: 80, height: 30);
        cancelButton.IsVisible = _segmentationRunning;
        cancelButton.Click += (_, _) =>
        {
            _segmentationCancellation?.Cancel();
        };

        body.Children.Add(CreateAnatomyEditorCard(
            "Plugin & task",
            selectedPlugin.Manifest.Description ?? "Select a segmentation plugin and the analysis task to run.",
            CreateFieldRow("Plugin", pluginCombo),
            CreateFieldRow("Task", taskCombo),
            progressBar,
            statusText,
            CreateActionRow(runButton, cancelButton)));

        return section;
    }

    // ── Segmentation execution ───────────────────────────────────

    private async Task RunSegmentationAsync(ProgressBar progressBar, TextBlock statusText)
    {
        if (_pluginManager is null || _selectedSegmentationPluginId is null || _selectedSegmentationTaskId is null)
        {
            ShowToast("Please select a plugin and task first.", ToastSeverity.Warning);
            return;
        }

        // Find the active volume.
        ViewportSlot? slot = _activeSlot;
        if (slot?.Volume is null || slot.Series is null)
        {
            ShowToast("No volume loaded — load a volumetric series first.", ToastSeverity.Warning);
            return;
        }

        SeriesVolume volume = slot.Volume;

        _segmentationRunning = true;
        _segmentationCancellation = new CancellationTokenSource();
        CancellationToken ct = _segmentationCancellation.Token;

        RefreshAnatomyPanel();

        try
        {
            // Update the UI to show progress.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                progressBar.IsVisible = true;
                statusText.IsVisible = true;
                statusText.Text = "Starting plugin…";
            });

            // For remote plugins, update the session/volume context.
            PluginInstance? instance = _pluginManager.GetPlugin(_selectedSegmentationPluginId);
            if (instance?.Handle is RemotePluginAdapter remoteAdapter && IsRenderServerStudy && slot.Series is not null)
            {
                // Retrieve the render-server session context.
                if (TryGetCachedRemoteRenderBackend(slot.Series, out RemoteRenderBackend? backend) && backend is not null)
                {
                    remoteAdapter.SessionId = backend.SessionId;
                    remoteAdapter.VolumeId = backend.VolumeId;
                }
            }

            ISegmentationProvider provider =
                await _pluginManager.GetSegmentationProviderAsync(_selectedSegmentationPluginId, ct);

            // Build the request.
            string studyUid = _context.StudyDetails.Study.StudyInstanceUid;
            string outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KPACS.Viewer.Avalonia", "seg-output", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);

            var request = new SegmentationRequest
            {
                Volume = new VolumeDescriptor
                {
                    FilePath = string.Empty,  // will be overridden by the plugin host
                    Format = "nifti",
                    Dimensions = [volume.SizeX, volume.SizeY, volume.SizeZ],
                    SpacingMm = [volume.SpacingX, volume.SpacingY, volume.SpacingZ],
                    Modality = slot.Series?.Modality ?? string.Empty,
                    SeriesInstanceUid = volume.SeriesInstanceUid,
                },
                TaskId = _selectedSegmentationTaskId,
                OutputDirectory = outputDir,
                Device = "gpu",
                ProduceMultilabel = true,
            };

            // Progress callback that updates the UI.
            var progress = new Progress<ProgressReport>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    progressBar.Value = p.PercentComplete;
                    statusText.Text = p.StatusMessage ?? $"Step {p.Step}/{p.TotalSteps}";
                });
            });

            SegmentationResult result = await provider.RunAsync(request, progress, ct);

            if (!result.Success)
            {
                ShowToast($"Segmentation failed: {result.ErrorMessage}", ToastSeverity.Error);
                return;
            }

            // Import results as SegmentationMask3D objects.
            int imported = 0;

            if (instance?.Handle is RemotePluginAdapter remoteAdapterForMasks)
            {
                // Remote mode: download masks from the server using tokens.
                foreach (SegmentedStructure structure in result.Structures)
                {
                    if (string.IsNullOrEmpty(structure.MaskPath))
                    {
                        continue;
                    }

                    SegmentationMask3D? mask = await remoteAdapterForMasks.DownloadMaskAsync(
                        structure.MaskPath,
                        structure.DisplayName ?? structure.Id,
                        volume.SeriesInstanceUid,
                        studyUid,
                        ct);

                    if (mask is not null)
                    {
                        _segmentationMasks[mask.Id] = mask;
                        imported++;
                    }
                }
            }
            else
            {
                // Local mode: convert NIfTI results to masks.
                if (!string.IsNullOrEmpty(result.MultilabelPath) && File.Exists(result.MultilabelPath))
                {
                    IReadOnlyList<SegmentationMask3D> masks =
                        NiftiMaskConverter.FromMultilabelNiftiAll(
                            result.MultilabelPath, result.Structures, volume, studyUid);

                    foreach (SegmentationMask3D mask in masks)
                    {
                        _segmentationMasks[mask.Id] = mask;
                        imported++;
                    }
                }
                else
                {
                    // Individual per-structure NIfTI files.
                    foreach (SegmentedStructure structure in result.Structures)
                    {
                        if (string.IsNullOrEmpty(structure.MaskPath) || !File.Exists(structure.MaskPath))
                        {
                            continue;
                        }

                        SegmentationMask3D mask = NiftiMaskConverter.FromBinaryNifti(
                            structure.MaskPath,
                            structure.DisplayName ?? structure.Id,
                            volume,
                            studyUid);

                        _segmentationMasks[mask.Id] = mask;
                        imported++;
                    }
                }

                // Clean up temp output directory (best-effort).
                _ = Task.Run(() =>
                {
                    try { Directory.Delete(outputDir, recursive: true); }
                    catch { /* best-effort */ }
                });
            }

            ShowToast(
                $"Segmentation complete — {imported} structures imported ({result.ElapsedSeconds:F1}s).",
                ToastSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowToast("Segmentation cancelled.", ToastSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowToast($"Segmentation error: {ex.Message}", ToastSeverity.Error);
        }
        finally
        {
            _segmentationRunning = false;
            _segmentationCancellation?.Dispose();
            _segmentationCancellation = null;

            await Dispatcher.UIThread.InvokeAsync(() => RefreshAnatomyPanel());
        }
    }
}
