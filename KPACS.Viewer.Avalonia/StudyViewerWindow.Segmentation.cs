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
using KPACS.Viewer.Controls;
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
    private bool _segmentationResultsSectionExpanded = true;
    private string? _selectedSegmentationPluginId;
    private string? _selectedSegmentationTaskId;
    private bool _segmentationRunning;
    private CancellationTokenSource? _segmentationCancellation;
    private readonly HashSet<Guid> _visibleSegmentationMaskIds = [];

    /// <summary>
    /// Predefined overlay colours for segmentation structures (ARGB hex).
    /// Cycles through these for each imported mask.
    /// </summary>
    private static readonly (byte R, byte G, byte B)[] SegmentationOverlayPalette =
    [
        (255, 107,  89),  // Coral red
        ( 66, 189, 255),  // Sky blue
        ( 64, 224, 120),  // Mint green
        (255, 193,  37),  // Gold
        (178, 102, 255),  // Lavender
        (255, 142, 200),  // Pink
        (  0, 210, 211),  // Teal
        (255, 165,  79),  // Tangerine
        (144, 238, 144),  // Light green
        (218, 112, 214),  // Orchid
        (100, 149, 237),  // Cornflower
        (255, 218, 185),  // Peach
        ( 50, 205,  50),  // Lime
        (135, 206, 250),  // Light sky blue
        (255,  99,  71),  // Tomato
        (147, 112, 219),  // Medium purple
    ];

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

        // ── Imported structures list ────────────────────────────

        if (_segmentationMasks.Count > 0)
        {
            int roiBackedCount = _segmentationMasks.Values.Count(mask => HasLinkedVolumeRoi(mask));
            string resultsHint = roiBackedCount > 0
                ? $"{_segmentationMasks.Count} structure(s) available — ROI-backed structures render as 3D ROI outlines across all MPR views."
                : $"{_segmentationMasks.Count} structure(s) available — toggle visibility to overlay on the viewport.";

            (Border resultsSection, StackPanel resultsBody) = CreateAnatomySectionHost(
                "Imported Structures",
                resultsHint,
                _segmentationResultsSectionExpanded,
                expanded => _segmentationResultsSectionExpanded = expanded);

            // Show All / Hide All buttons.
            var showAllButton = CreateAnatomyActionButton(
                "Show all", "#FF1C5A3F", "#FF4FD08B", minWidth: 80, height: 26);
            showAllButton.Click += (_, _) =>
            {
                foreach (SegmentationMask3D mask in _segmentationMasks.Values)
                    _visibleSegmentationMaskIds.Add(mask.Id);

                EnsureSegmentationVolumeRois(_segmentationMasks.Values);
                UpdateSegmentationOverlays();
                RefreshMeasurementPanels();
                RefreshAnatomyPanel();
            };

            var hideAllButton = CreateAnatomyActionButton(
                "Hide all", "#FF5C2431", "#FFEB7D96", minWidth: 80, height: 26);
            hideAllButton.Click += (_, _) =>
            {
                _visibleSegmentationMaskIds.Clear();
                UpdateSegmentationOverlays();
                RefreshMeasurementPanels();
                RefreshAnatomyPanel();
            };

            resultsBody.Children.Add(CreateActionRow(showAllButton, hideAllButton));

            // Individual structure rows with colour swatch + toggle.
            int colorIndex = 0;
            foreach (SegmentationMask3D mask in _segmentationMasks.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                (byte r, byte g, byte b) = SegmentationOverlayPalette[colorIndex % SegmentationOverlayPalette.Length];
                bool isVisible = _visibleSegmentationMaskIds.Contains(mask.Id);

                var colorSwatch = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };

                var nameText = new TextBlock
                {
                    Text = mask.Name,
                    Foreground = new SolidColorBrush(Color.Parse(isVisible ? "#FFE0EAF4" : "#FF6B8DAA")),
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                };

                var toggleCheckBox = new CheckBox
                {
                    IsChecked = isVisible,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0),
                    MinWidth = 0,
                    Padding = new Thickness(0),
                };

                // Capture loop variables for the closure.
                Guid maskId = mask.Id;
                toggleCheckBox.IsCheckedChanged += (_, _) =>
                {
                    if (toggleCheckBox.IsChecked == true)
                    {
                        _visibleSegmentationMaskIds.Add(maskId);
                        EnsureSegmentationVolumeRoi(maskId);
                    }
                    else
                    {
                        _visibleSegmentationMaskIds.Remove(maskId);
                    }

                    UpdateSegmentationOverlays();
                    RefreshMeasurementPanels();

                    // Update the name text dimming without a full panel rebuild.
                    nameText.Foreground = new SolidColorBrush(
                        Color.Parse(_visibleSegmentationMaskIds.Contains(maskId) ? "#FFE0EAF4" : "#FF6B8DAA"));
                };

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                    ColumnSpacing = 4,
                    Margin = new Thickness(0, 1),
                };
                Grid.SetColumn(colorSwatch, 0);
                Grid.SetColumn(toggleCheckBox, 1);
                Grid.SetColumn(nameText, 2);
                row.Children.Add(colorSwatch);
                row.Children.Add(toggleCheckBox);
                row.Children.Add(nameText);

                resultsBody.Children.Add(row);
                colorIndex++;
            }

            body.Children.Add(resultsSection);
        }

        return section;
    }

    // ── Segmentation execution ───────────────────────────────────

    /// <summary>
    /// Push the current set of visible segmentation masks as overlay layers
    /// to every viewport panel whose volume matches a mask's geometry.
    /// Called after visibility toggles, Show/Hide All, and segmentation import.
    /// </summary>
    private void UpdateSegmentationOverlays()
    {
        // Build the overlay list: one entry per visible mask, with a colour from the palette.
        // The colour is assigned in the same deterministic order used by the structure list UI
        // (sorted by Name, then palette index cycles).
        List<(SegmentationMask3D Mask, byte R, byte G, byte B)> visibleMasks = [];
        int colorIndex = 0;
        foreach (SegmentationMask3D mask in _segmentationMasks.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            (byte r, byte g, byte b) = SegmentationOverlayPalette[colorIndex % SegmentationOverlayPalette.Length];
            if (_visibleSegmentationMaskIds.Contains(mask.Id) && !HasLinkedVolumeRoi(mask))
            {
                visibleMasks.Add((mask, r, g, b));
            }
            colorIndex++;
        }

        // Push overlays to every viewport slot that has a bound volume.
        foreach (ViewportSlot slot in _slots)
        {
            SeriesVolume? volume = slot.Volume;
            if (volume is null)
            {
                slot.Panel.SetSegmentationMaskOverlays([]);
                continue;
            }

            // Filter to masks whose geometry matches the volume dimensions + spacing.
            List<DicomViewPanel.SegmentationMaskOverlay> overlays = [];
            foreach ((SegmentationMask3D mask, byte r, byte g, byte b) in visibleMasks)
            {
                if (mask.Geometry.SizeX == volume.SizeX &&
                    mask.Geometry.SizeY == volume.SizeY &&
                    mask.Geometry.SizeZ == volume.SizeZ)
                {
                    SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
                    overlays.Add(new DicomViewPanel.SegmentationMaskOverlay(mask, buffer, r, g, b, 128));
                }
            }

            slot.Panel.SetSegmentationMaskOverlays(overlays);
        }
    }

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

        string? tempNiftiPath = null;

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

            // For local plugins, export the in-memory volume to a temporary
            // NIfTI file so that the out-of-process plugin can read it.
            // Remote plugins already have access to the volume on the server.
            string volumeFilePath = string.Empty;
            bool isRemotePlugin = instance?.Handle is RemotePluginAdapter;

            if (!isRemotePlugin)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    statusText.Text = "Exporting volume for plugin…";
                });

                tempNiftiPath = Path.Combine(outputDir, "input.nii.gz");
                await Task.Run(() => ExportVolumeAsNifti(volume, tempNiftiPath), ct);
                volumeFilePath = tempNiftiPath;
            }

            var request = new SegmentationRequest
            {
                Volume = new VolumeDescriptor
                {
                    FilePath = volumeFilePath,
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
            List<SegmentationMask3D> importedMasks = [];

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
                        importedMasks.Add(mask);
                        imported++;
                    }
                }
            }
            else
            {
                // Local mode: convert NIfTI results to masks on a background
                // thread so the UI stays responsive during heavy I/O + decode.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    statusText.Text = "Importing segmentation masks…";
                });

                if (!string.IsNullOrEmpty(result.MultilabelPath) && File.Exists(result.MultilabelPath))
                {
                    IReadOnlyList<SegmentationMask3D> masks =
                        await Task.Run(() => NiftiMaskConverter.FromMultilabelNiftiAll(
                            result.MultilabelPath, result.Structures, volume, studyUid), ct);

                    foreach (SegmentationMask3D mask in masks)
                    {
                        importedMasks.Add(mask);
                        imported++;
                    }
                }
                else
                {
                    // Individual per-structure NIfTI files.
                    var individualMasks = await Task.Run(() =>
                    {
                        var list = new List<SegmentationMask3D>();
                        foreach (SegmentedStructure structure in result.Structures)
                        {
                            if (string.IsNullOrEmpty(structure.MaskPath) || !File.Exists(structure.MaskPath))
                            {
                                continue;
                            }

                            list.Add(NiftiMaskConverter.FromBinaryNifti(
                                structure.MaskPath,
                                structure.DisplayName ?? structure.Id,
                                volume,
                                studyUid));
                        }

                        return list;
                    }, ct);

                    foreach (SegmentationMask3D mask in individualMasks)
                    {
                        importedMasks.Add(mask);
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

            RegisterImportedSegmentationMasks(importedMasks);

            ApplyDefaultSegmentationVisibility(importedMasks);
            EnsureSegmentationVolumeRois(importedMasks.Where(mask => _visibleSegmentationMaskIds.Contains(mask.Id)), volume);

            UpdateSegmentationOverlays();
            RefreshMeasurementPanels();
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

            // Clean up the temporary NIfTI input file (best-effort).
            if (tempNiftiPath is not null)
            {
                _ = Task.Run(() =>
                {
                    try { File.Delete(tempNiftiPath); }
                    catch { /* best-effort */ }
                });
            }

            await Dispatcher.UIThread.InvokeAsync(() => RefreshAnatomyPanel());
        }
    }

    private void RegisterImportedSegmentationMasks(IEnumerable<SegmentationMask3D> importedMasks)
    {
        foreach (SegmentationMask3D importedMask in importedMasks)
        {
            _segmentationMasks[importedMask.Id] = importedMask;
        }
    }

    private bool TryCreateSegmentationVolumeRoi(
        SegmentationMask3D mask,
        SeriesVolume volume,
        out StudyMeasurement? measurement,
        out SegmentationMask3D? linkedMask)
    {
        measurement = null;
        linkedMask = null;

        if (!SegmentationMaskVolumeRoiConverter.TryCreateVolumeContours(mask, volume, out VolumeRoiContour[] contours) || contours.Length == 0)
        {
            return false;
        }

        int representativeSliceIndex = GetRepresentativeAxialSliceIndex(mask, volume);
        DicomSpatialMetadata metadata = VolumeReslicer.GetSliceSpatialMetadata(volume, SliceOrientation.Axial, representativeSliceIndex);
        measurement = StudyMeasurement.CreateVolumeRoi(metadata.FilePath, metadata, contours, mask.Id);
        linkedMask = mask with
        {
            Metadata = mask.Metadata with
            {
                SourceMeasurementId = measurement.Id.ToString("D"),
            }
        };
        return true;
    }

    private bool HasLinkedVolumeRoi(SegmentationMask3D mask)
    {
        if (!Guid.TryParse(mask.Metadata.SourceMeasurementId, out Guid measurementId))
        {
            return false;
        }

        return _studyMeasurements.Any(measurement => measurement.Id == measurementId && measurement.Kind == MeasurementKind.VolumeRoi);
    }

    private void EnsureSegmentationVolumeRois(IEnumerable<SegmentationMask3D> masks, SeriesVolume? preferredVolume = null)
    {
        bool addedMeasurement = false;
        foreach (SegmentationMask3D mask in masks)
        {
            addedMeasurement |= EnsureSegmentationVolumeRoi(mask.Id, preferredVolume);
        }

        if (addedMeasurement)
        {
            ScheduleMeasurementSessionSave();
        }
    }

    private bool EnsureSegmentationVolumeRoi(Guid maskId, SeriesVolume? preferredVolume = null)
    {
        if (!_segmentationMasks.TryGetValue(maskId, out SegmentationMask3D? mask))
        {
            return false;
        }

        if (HasLinkedVolumeRoi(mask))
        {
            return false;
        }

        SeriesVolume? resolvedVolume = preferredVolume;
        if (resolvedVolume is null && !TryResolveSegmentationMaskVolume(mask, out resolvedVolume))
        {
            return false;
        }

        if (resolvedVolume is null ||
            !TryCreateSegmentationVolumeRoi(mask, resolvedVolume, out StudyMeasurement? measurement, out SegmentationMask3D? linkedMask) ||
            measurement is null ||
            linkedMask is null)
        {
            return false;
        }

        _segmentationMasks[maskId] = linkedMask;
        _studyMeasurements.RemoveAll(existing => existing.Id == measurement.Id);
        _studyMeasurements.Add(measurement);
        return true;
    }

    private bool TryResolveSegmentationMaskVolume(SegmentationMask3D mask, out SeriesVolume? volume)
    {
        volume = _activeSlot?.Volume;
        if (volume is not null && IsMaskCompatibleWithVolume(mask, volume))
        {
            return true;
        }

        volume = _slots
            .Select(slot => slot.Volume)
            .FirstOrDefault(candidate => candidate is not null && IsMaskCompatibleWithVolume(mask, candidate));
        return volume is not null;
    }

    private static bool IsMaskCompatibleWithVolume(SegmentationMask3D mask, SeriesVolume volume)
    {
        return mask.Geometry.SizeX == volume.SizeX
            && mask.Geometry.SizeY == volume.SizeY
            && mask.Geometry.SizeZ == volume.SizeZ
            && string.Equals(mask.SourceFrameOfReferenceUid, volume.FrameOfReferenceUid, StringComparison.Ordinal);
    }

    private static int GetRepresentativeAxialSliceIndex(SegmentationMask3D mask, SeriesVolume volume)
    {
        int sliceIndex = mask.Metadata.Statistics?.BoundsMin.Z ?? 0;
        return Math.Clamp(sliceIndex, 0, Math.Max(0, volume.SizeZ - 1));
    }

    private void ApplyDefaultSegmentationVisibility(IEnumerable<SegmentationMask3D> importedMasks)
    {
        SegmentationMask3D[] imported = importedMasks.ToArray();
        if (imported.Length == 0)
        {
            return;
        }

        foreach (SegmentationMask3D mask in imported)
        {
            _visibleSegmentationMaskIds.Remove(mask.Id);
        }

        SegmentationMask3D defaultVisibleMask = imported
            .OrderBy(mask => mask.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        _visibleSegmentationMaskIds.Add(defaultVisibleMask.Id);
    }

    /// <summary>
    /// Write a <see cref="SeriesVolume"/> as a minimal NIfTI-1 .nii.gz file
    /// so that out-of-process plugins receive a standard neuroimaging input format.
    /// Ported from <c>PluginProxyServiceImpl.ExportVolumeAsNifti</c> in the render server.
    /// </summary>
    private static void ExportVolumeAsNifti(SeriesVolume volume, string outputPath)
    {
        using FileStream fs = File.Create(outputPath);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
        using var bw = new BinaryWriter(gz);

        int nx = volume.SizeX, ny = volume.SizeY, nz = volume.SizeZ;

        // NIfTI-1 header (348 bytes).
        bw.Write(348);              // sizeof_hdr
        bw.Write(new byte[28]);     // data_type (10) + db_name (18)
        bw.Write(0);                // extents
        bw.Write((short)0);         // session_error
        bw.Write((byte)'r');        // regular
        bw.Write((byte)0);          // dim_info

        // dim[0..7]
        bw.Write((short)3);         // ndim
        bw.Write((short)nx);
        bw.Write((short)ny);
        bw.Write((short)nz);
        bw.Write((short)1); bw.Write((short)1); bw.Write((short)1); bw.Write((short)1);

        bw.Write(0f); bw.Write(0f); bw.Write(0f); // intent_p1/2/3
        bw.Write((short)0);         // intent_code
        bw.Write((short)4);         // datatype = INT16
        bw.Write((short)16);        // bitpix
        bw.Write((short)0);         // slice_start

        // pixdim[0..7]
        bw.Write(1f);               // qfac
        bw.Write((float)volume.SpacingX);
        bw.Write((float)volume.SpacingY);
        bw.Write((float)volume.SpacingZ);
        bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(0f);

        bw.Write(352f);             // vox_offset
        bw.Write(1f);               // scl_slope
        bw.Write(0f);               // scl_inter
        bw.Write((short)0);         // slice_end
        bw.Write((byte)0);          // slice_code
        bw.Write((byte)2);          // xyzt_units = NIFTI_UNITS_MM

        bw.Write(0f); bw.Write(0f); // cal_max, cal_min
        bw.Write(0f); bw.Write(0f); // slice_duration, toffset
        bw.Write(0); bw.Write(0);   // glmax, glmin
        bw.Write(new byte[80]);     // descrip
        bw.Write(new byte[24]);     // aux_file

        // Use sform (method 2) to encode the full affine.
        bw.Write((short)0);         // qform_code = 0 (unknown)
        bw.Write((short)1);         // sform_code = 1 (scanner anat)

        // quatern (unused since qform_code = 0)
        bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write(0f); bw.Write(0f); bw.Write(0f);

        // srow_x, srow_y, srow_z (4 floats each = 48 bytes)
        //
        // DICOM uses LPS (Left-Posterior-Superior) patient coordinates;
        // NIfTI expects RAS (Right-Anterior-Superior).  Convert by
        // negating the X and Y rows of the affine matrix.
        Models.Vector3D row = volume.RowDirection;
        Models.Vector3D col = volume.ColumnDirection;
        Models.Vector3D nrm = volume.Normal;
        Models.Vector3D orig = volume.Origin;

        // srow_x  (RAS X = −LPS X)
        bw.Write((float)(-row.X * volume.SpacingX));
        bw.Write((float)(-col.X * volume.SpacingY));
        bw.Write((float)(-nrm.X * volume.SpacingZ));
        bw.Write((float)(-orig.X));
        // srow_y  (RAS Y = −LPS Y)
        bw.Write((float)(-row.Y * volume.SpacingX));
        bw.Write((float)(-col.Y * volume.SpacingY));
        bw.Write((float)(-nrm.Y * volume.SpacingZ));
        bw.Write((float)(-orig.Y));
        // srow_z  (RAS Z = LPS Z — unchanged)
        bw.Write((float)(row.Z * volume.SpacingX));
        bw.Write((float)(col.Z * volume.SpacingY));
        bw.Write((float)(nrm.Z * volume.SpacingZ));
        bw.Write((float)orig.Z);

        bw.Write(new byte[16]);     // intent_name
        // magic: "n+1\0" — written directly (GZipStream does not support Seek).
        bw.Write((byte)'n'); bw.Write((byte)'+'); bw.Write((byte)'1'); bw.Write((byte)0);

        // 4-byte extension pad.
        bw.Write(new byte[4]);

        // Voxel data (INT16, x-fastest).
        for (int z = 0; z < nz; z++)
        {
            int sliceBase = z * ny * nx;
            for (int y = 0; y < ny; y++)
            {
                int rowBase = sliceBase + y * nx;
                for (int x = 0; x < nx; x++)
                {
                    bw.Write(volume.Voxels[rowBase + x]);
                }
            }
        }
    }
}
