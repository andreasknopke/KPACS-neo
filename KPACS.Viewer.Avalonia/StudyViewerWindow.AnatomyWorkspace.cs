using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using System.Text.Json;
using System.Globalization;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly List<string> _customAnatomyRegions = [];
    private readonly Dictionary<string, List<string>> _customAnatomyStructuresByRegion = new(StringComparer.OrdinalIgnoreCase);
    private Point _anatomyPanelOffset;
    private bool _anatomyPanelPinned;
    private bool _anatomyPanelVisible;
    private IPointer? _anatomyPanelDragPointer;
    private Point _anatomyPanelDragStart;
    private Point _anatomyPanelDragStartOffset;
    private string _selectedAnatomyCatalogRegion = "Neuro";
    private string _selectedAnatomyCatalogStructure = string.Empty;
    private string _selectedActivePackStructureId = string.Empty;
    private bool _useLegacyAnatomyPriors = true;
    private bool _developerAnatomyModelProjectionEnabled;
    private bool _reportDebugEnabled;
    private bool _anatomySelectedRoiSectionExpanded = true;
    private bool _anatomyCatalogSectionExpanded = true;
    private bool _anatomyDeveloperSectionExpanded = false;
    private bool _anatomyPackEditorSectionExpanded = true;
    private bool _anatomyLegacySectionExpanded = false;

    private void RefreshAnatomyPanel(bool forceVisible = false)
    {
        if (forceVisible)
        {
            _anatomyPanelVisible = true;
        }

        if (!_anatomyPanelVisible || AnatomyPanel is null)
        {
            HideAnatomyPanel();
            return;
        }

        AnatomyPanel.IsVisible = true;
        AnatomyPanelPinButton.IsChecked = _anatomyPanelPinned;
        ApplyAnatomyPanelOffset();

        StudyMeasurement? selectedRoi = GetSelectedVolumeRoiMeasurement();
        AnatomyPanelSummaryText.Text = selectedRoi is null
            ? $"{_volumeRoiAnatomyPriors.Count} legacy learned models"
            : $"Selected ROI ready · {_volumeRoiAnatomyPriors.Count} legacy learned models";
        AnatomyPanelHintText.Text = selectedRoi is null
            ? "Select a 3D ROI to assign it to an anatomy structure. Manage custom regions/structures and rename or delete legacy learned models below."
            : "The selected 3D ROI stays a normal finding in reporting. Define or assign its anatomy here. Legacy learning only happens via explicit Learn actions.";

        AnatomyPanelContent.Children.Clear();
        AnatomyPanelContent.Children.Add(BuildSegmentationSection());
        AnatomyPanelContent.Children.Add(BuildSelectedRoiAssignmentSection(selectedRoi));
        AnatomyPanelContent.Children.Add(BuildCatalogManagementSection());
        AnatomyPanelContent.Children.Add(BuildDeveloperWorkflowSection(selectedRoi));
        AnatomyPanelContent.Children.Add(BuildActivePackEditorSection());
        AnatomyPanelContent.Children.Add(BuildLearnedModelsSection(selectedRoi));
    }

    private void HideAnatomyPanel()
    {
        if (AnatomyPanel is null)
        {
            return;
        }

        AnatomyPanel.IsVisible = false;
        AnatomyPanelSummaryText.Text = string.Empty;
        AnatomyPanelHintText.Text = string.Empty;
        AnatomyPanelContent.Children.Clear();
    }

    private Control BuildSelectedRoiAssignmentSection(StudyMeasurement? selectedRoi)
    {
        (Border section, StackPanel body) = CreateAnatomySectionHost(
            "Selected 3D ROI",
            selectedRoi is null
                ? "Select a 3D ROI in the viewer to assign it to an anatomy structure."
                : BuildSelectedMeasurementLabel(selectedRoi),
            _anatomySelectedRoiSectionExpanded,
            expanded => _anatomySelectedRoiSectionExpanded = expanded);

        if (selectedRoi is null)
        {
            return section;
        }

        string currentRegion = _reportRegionOverrides.TryGetValue(selectedRoi.Id, out string? manualRegion) && !string.IsNullOrWhiteSpace(manualRegion)
            ? manualRegion.Trim()
            : ResolveMeasurementRegion(selectedRoi, FindSlotForMeasurement(selectedRoi) ?? _activeSlot).Label;
        string currentStructure = _reportAnatomyOverrides.TryGetValue(selectedRoi.Id, out string? manualStructure) && !string.IsNullOrWhiteSpace(manualStructure)
            ? manualStructure.Trim()
            : ResolveMeasurementAnatomy(selectedRoi, FindSlotForMeasurement(selectedRoi) ?? _activeSlot).Label;

        List<string> regionOptions = GetAllAnatomyRegionOptions();
        if (!regionOptions.Contains(currentRegion, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currentRegion))
        {
            regionOptions.Add(currentRegion);
        }

        var regionCombo = new ComboBox
        {
            ItemsSource = regionOptions,
            SelectedItem = string.IsNullOrWhiteSpace(currentRegion) ? regionOptions.FirstOrDefault() : currentRegion,
            MinWidth = 180,
            MaxWidth = 260,
        };
        StyleAnatomyComboBox(regionCombo);

        string selectedRegion = regionCombo.SelectedItem as string ?? regionOptions.FirstOrDefault() ?? "Neuro";
        List<string> structures = GetAnatomyStructureOptionsForRegion(selectedRegion);
        if (!structures.Contains(currentStructure, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currentStructure))
        {
            structures.Add(currentStructure);
        }

        var structureCombo = new ComboBox
        {
            ItemsSource = structures,
            SelectedItem = string.IsNullOrWhiteSpace(currentStructure) ? structures.FirstOrDefault() : currentStructure,
            MinWidth = 180,
            MaxWidth = 260,
        };
        StyleAnatomyComboBox(structureCombo);

        var customNameBox = new TextBox
        {
            Watermark = "New or custom structure name",
            Text = string.IsNullOrWhiteSpace(currentStructure) || string.Equals(currentStructure, "Unassigned", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : currentStructure,
            MinWidth = 180,
            MaxWidth = 260,
        };
        StyleAnatomyTextBox(customNameBox);

        regionCombo.SelectionChanged += (_, _) =>
        {
            string nextRegion = regionCombo.SelectedItem as string ?? "Neuro";
            List<string> options = GetAnatomyStructureOptionsForRegion(nextRegion);
            structureCombo.ItemsSource = options;
            structureCombo.SelectedItem = options.FirstOrDefault();
        };

        var assignButton = CreateAnatomyActionButton("Assign anatomy", "#FF215E91", "#FF4FA3FF", minWidth: 136, height: 30);
        assignButton.Click += async (_, _) =>
        {
            string regionLabel = regionCombo.SelectedItem as string ?? "Neuro";
            string anatomyLabel = !string.IsNullOrWhiteSpace(customNameBox.Text)
                ? customNameBox.Text.Trim()
                : structureCombo.SelectedItem as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(anatomyLabel) || string.Equals(anatomyLabel, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("Choose or enter an anatomy structure first.", ToastSeverity.Warning);
                return;
            }

            await AssignMeasurementToAnatomyAsync(selectedRoi.Id, regionLabel, anatomyLabel);
        };

        body.Children.Add(CreateAnatomyEditorCard(
            "Assignment",
            "Choose the target region and structure, then optionally override the label with a custom anatomy name.",
            CreateFieldRow("Region", regionCombo),
            CreateFieldRow("Structure", structureCombo),
            CreateFieldRow("Custom name", customNameBox),
            CreateActionRow(assignButton)));
        return section;
    }

    private Control BuildCatalogManagementSection()
    {
        List<string> regions = GetAllAnatomyRegionOptions();
        if (!regions.Contains(_selectedAnatomyCatalogRegion, StringComparer.OrdinalIgnoreCase))
        {
            _selectedAnatomyCatalogRegion = regions.FirstOrDefault() ?? "Neuro";
        }

        List<string> structureOptions = GetAnatomyStructureOptionsForRegion(_selectedAnatomyCatalogRegion);
        if (!string.IsNullOrWhiteSpace(_selectedAnatomyCatalogStructure) &&
            !structureOptions.Contains(_selectedAnatomyCatalogStructure, StringComparer.OrdinalIgnoreCase))
        {
            _selectedAnatomyCatalogStructure = string.Empty;
        }

        (Border section, StackPanel body) = CreateAnatomySectionHost(
            "Catalog",
            "Define extra regions and structures here. Reporting will use this catalog for display, but anatomy assignment happens in this module.",
            _anatomyCatalogSectionExpanded,
            expanded => _anatomyCatalogSectionExpanded = expanded);

        var regionCombo = new ComboBox
        {
            ItemsSource = regions,
            SelectedItem = _selectedAnatomyCatalogRegion,
            MinWidth = 180,
            MaxWidth = 260,
        };
        StyleAnatomyComboBox(regionCombo);

        var regionNameBox = new TextBox
        {
            Watermark = "Region name",
            Text = _selectedAnatomyCatalogRegion,
            MinWidth = 180,
            MaxWidth = 260,
        };
        StyleAnatomyTextBox(regionNameBox);

        var structureCombo = new ComboBox
        {
            ItemsSource = structureOptions,
            SelectedItem = string.IsNullOrWhiteSpace(_selectedAnatomyCatalogStructure) ? structureOptions.FirstOrDefault() : _selectedAnatomyCatalogStructure,
            MinWidth = 180,
            MaxWidth = 260,
        };
        StyleAnatomyComboBox(structureCombo);

        var structureNameBox = new TextBox
        {
            Watermark = "Structure name",
            Text = structureCombo.SelectedItem as string ?? string.Empty,
            MinWidth = 180,
            MaxWidth = 260,
        };
        StyleAnatomyTextBox(structureNameBox);

        regionCombo.SelectionChanged += (_, _) =>
        {
            _selectedAnatomyCatalogRegion = regionCombo.SelectedItem as string ?? "Neuro";
            regionNameBox.Text = _selectedAnatomyCatalogRegion;
            List<string> options = GetAnatomyStructureOptionsForRegion(_selectedAnatomyCatalogRegion);
            structureCombo.ItemsSource = options;
            structureCombo.SelectedItem = options.FirstOrDefault();
            structureNameBox.Text = structureCombo.SelectedItem as string ?? string.Empty;
        };

        structureCombo.SelectionChanged += (_, _) =>
        {
            _selectedAnatomyCatalogStructure = structureCombo.SelectedItem as string ?? string.Empty;
            structureNameBox.Text = _selectedAnatomyCatalogStructure;
        };

        var addRegionButton = CreateAnatomyActionButton("Add region", "#FF1C5A3F", "#FF4FD08B", minWidth: 108);
        addRegionButton.Click += (_, _) => AddCatalogRegion(regionNameBox.Text);
        var renameRegionButton = CreateAnatomyActionButton("Rename region", minWidth: 120);
        renameRegionButton.Click += async (_, _) => await RenameCatalogRegionAsync(_selectedAnatomyCatalogRegion, regionNameBox.Text);
        var deleteRegionButton = CreateAnatomyActionButton("Delete region", "#FF5C2431", "#FFEB7D96", minWidth: 118);
        deleteRegionButton.Click += (_, _) => DeleteCatalogRegion(_selectedAnatomyCatalogRegion);

        var addStructureButton = CreateAnatomyActionButton("Add structure", "#FF1C5A3F", "#FF4FD08B", minWidth: 118);
        addStructureButton.Click += (_, _) => AddCatalogStructure(_selectedAnatomyCatalogRegion, structureNameBox.Text);
        var renameStructureButton = CreateAnatomyActionButton("Rename structure", minWidth: 132);
        renameStructureButton.Click += async (_, _) => await RenameCatalogStructureAsync(_selectedAnatomyCatalogRegion, _selectedAnatomyCatalogStructure, structureNameBox.Text);
        var deleteStructureButton = CreateAnatomyActionButton("Delete structure", "#FF5C2431", "#FFEB7D96", minWidth: 128);
        deleteStructureButton.Click += (_, _) => DeleteCatalogStructure(_selectedAnatomyCatalogRegion, _selectedAnatomyCatalogStructure);

        body.Children.Add(CreateAnatomyEditorCard(
            "Regions",
            "Keep the top-level body map clean. Region changes affect downstream anatomy selection lists.",
            CreateFieldRow("Selected", regionCombo),
            CreateFieldRow("Name", regionNameBox),
            CreateActionRow(addRegionButton, renameRegionButton, deleteRegionButton)));
        body.Children.Add(CreateAnatomyEditorCard(
            "Structures",
            $"Manage structures inside {_selectedAnatomyCatalogRegion}. These labels become available when assigning anatomy to ROIs.",
            CreateFieldRow("Selected", structureCombo),
            CreateFieldRow("Name", structureNameBox),
            CreateActionRow(addStructureButton, renameStructureButton, deleteStructureButton)));
        return section;
    }

    private Control BuildLearnedModelsSection(StudyMeasurement? selectedRoi)
    {
        (Border section, StackPanel body) = CreateAnatomySectionHost(
            "Legacy learned priors",
            _volumeRoiAnatomyPriors.Count == 0
                ? "No legacy priors stored yet. Assign a 3D ROI above to create the first legacy learning snapshot."
                : "These are old-model ROI priors from the database. Keep them as migration material, use them carefully, and prefer the new pack workflow for future knowledge.",
            _anatomyLegacySectionExpanded,
            expanded => _anatomyLegacySectionExpanded = expanded);

        if (_volumeRoiAnatomyPriors.Count == 0)
        {
            return section;
        }

        foreach (VolumeRoiAnatomyPriorRecord prior in _volumeRoiAnatomyPriors.OrderByDescending(candidate => candidate.UseCount).ThenBy(candidate => candidate.AnatomyLabel))
        {
            body.Children.Add(BuildLearnedModelCard(prior, selectedRoi));
        }

        return section;
    }

    private Control BuildDeveloperWorkflowSection(StudyMeasurement? selectedRoi)
    {
        string selectedRoiAnatomyLabel = GetSelectedRoiAssignedAnatomyLabel(selectedRoi);
        AnatomyStructureDefinition? existingAssignedStructure = ResolveExistingStructureForAssignedRoi(selectedRoi);
        string activePackLabel = _activeCraniumKnowledgePack is null
            ? "No active Cranium pack loaded"
            : $"{_activeCraniumKnowledgePack.DisplayName} · {_activeCraniumKnowledgePack.PackVersion}";
        string activePackPathLabel = string.IsNullOrWhiteSpace(_activeCraniumKnowledgePackPath)
            ? "No file path"
            : _activeCraniumKnowledgePackPath;
        string legacyMode = _useLegacyAnatomyPriors ? "enabled" : "disabled";

        (Border section, StackPanel body) = CreateAnatomySectionHost(
            "Developer workflow",
            $"Active pack: {activePackLabel}. Legacy ROI priors are currently {legacyMode}. Use this area to manage packs, export migration snapshots, and feed selected ROIs into the new developer workflow.",
            _anatomyDeveloperSectionExpanded,
            expanded => _anatomyDeveloperSectionExpanded = expanded);

        body.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, new[]
            {
                "Recommended workflow:",
                "1. Start with a clean c-CT case and define the shared spatial structure once.",
                "2. Select a 3D ROI and assign region/structure above.",
                "3. Feed the ROI into the active pack to build the common spatial frame.",
                "4. Open a matched c-MR case later and merge that ROI into the same structure.",
                "5. Save the active pack after each meaningful structure update.",
                "6. Export an ROI draft only when you want an external review or migration artifact.",
                "7. Keep old DB priors only as migration evidence, not as the long-term anatomy model.",
            }),
            Foreground = new SolidColorBrush(Color.Parse("#FFD8E5F0")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = "Bridge rule: compartments, position ranges and relations are shared across c-CT/c-MRT; only modality-specific material profiles are learned separately.",
            Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = $"Active pack path: {activePackPathLabel}",
            Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = _activeCraniumKnowledgePack is null
                ? "Structures in active pack: 0"
                : $"Structures in active pack: {_activeCraniumKnowledgePack.Structures.Count}",
            Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
            FontSize = 10,
        });

        var reloadPacksButton = CreateAnatomyActionButton("Reload packs", minWidth: 108);
        reloadPacksButton.Click += async (_, _) => await ReloadAnatomyKnowledgePacksAsync();

        var savePackButton = CreateAnatomyActionButton("Save active pack", "#FF215E91", "#FF4FA3FF", minWidth: 128);
        savePackButton.IsEnabled = _activeCraniumKnowledgePack is not null;
        savePackButton.Click += async (_, _) => await SaveActiveAnatomyPackAsync();

        var feedRoiButton = CreateAnatomyActionButton("Feed as new structure", "#FF1C5A3F", "#FF4FD08B", minWidth: 178);
        feedRoiButton.IsEnabled = selectedRoi is not null && _activeCraniumKnowledgePack is not null;
        feedRoiButton.Click += async (_, _) => await FeedSelectedRoiIntoActivePackAsync(selectedRoi);

        var applyToSelectedStructureButton = CreateAnatomyActionButton("Merge into existing structure", minWidth: 202);
        applyToSelectedStructureButton.IsEnabled = selectedRoi is not null && _activeCraniumKnowledgePack is not null && existingAssignedStructure is not null;
        applyToSelectedStructureButton.Click += async (_, _) => await ApplySelectedRoiToSelectedPackStructureAsync(selectedRoi);

        var exportPackButton = CreateAnatomyActionButton("Export active pack", minWidth: 132);
        exportPackButton.IsEnabled = _activeCraniumKnowledgePack is not null;
        exportPackButton.Click += async (_, _) => await ExportActiveAnatomyPackAsync();

        var exportLegacyButton = CreateAnatomyActionButton("Export legacy priors", minWidth: 140);
        exportLegacyButton.IsEnabled = _volumeRoiAnatomyPriors.Count > 0;
        exportLegacyButton.Click += async (_, _) => await ExportLegacyPriorsSnapshotAsync();

        var toggleLegacyButton = CreateAnatomyActionButton(_useLegacyAnatomyPriors ? "Disable legacy priors" : "Enable legacy priors", minWidth: 156);
        toggleLegacyButton.Click += (_, _) => ToggleLegacyPriorUsage();

        var exportRoiDraftButton = CreateAnatomyActionButton("Export selected ROI draft", minWidth: 172);
        exportRoiDraftButton.IsEnabled = selectedRoi is not null;
        exportRoiDraftButton.Click += async (_, _) => await ExportSelectedRoiDraftAsync(selectedRoi);

        var toggleProjectionButton = CreateAnatomyActionButton(_developerAnatomyModelProjectionEnabled ? "Hide projected pack structure" : "Project selected pack structure", "#FF234C73", "#FF73C2FF", minWidth: 194);
        ToolTip.SetTip(toggleProjectionButton, "Project the currently selected active-pack structure into all loaded volume series using the pack definition itself.");
        toggleProjectionButton.Click += (_, _) => ToggleDeveloperAnatomyModelProjection();

        var reportDebugCheckBox = CreateAnatomyInlineCheckBox(
            "report debug",
            _reportDebugEnabled,
            "Show anatomy matching debug details inside report hints.");
        reportDebugCheckBox.IsCheckedChanged += (_, _) => ToggleReportDebug(reportDebugCheckBox.IsChecked == true);

        body.Children.Add(CreateAnatomyEditorCard(
            "Pack operations",
            "Reload, save, export and bridge old ROI knowledge into the active anatomy pack.",
            CreateActionRow(reloadPacksButton, savePackButton, feedRoiButton, applyToSelectedStructureButton, exportPackButton, exportLegacyButton, toggleLegacyButton, exportRoiDraftButton, toggleProjectionButton),
            reportDebugCheckBox,
            CreateAnatomyNoteText(_volumeRoiAnatomyPriors.Count == 0
                ? "Legacy priors in DB: 0"
                : $"Legacy priors in DB: {_volumeRoiAnatomyPriors.Count}"),
            CreateAnatomyNoteText(string.IsNullOrWhiteSpace(selectedRoiAnatomyLabel)
                ? "Current ROI target: assign a concrete anatomy structure first."
                : existingAssignedStructure is null
                    ? $"Current ROI target: {selectedRoiAnatomyLabel} is not yet in the active pack → use Feed."
                    : $"Current ROI target: {selectedRoiAnatomyLabel} already exists in the active pack → use Merge."),
            CreateAnatomyStatusText(BuildDeveloperAnatomyProjectionStatusText(), _developerAnatomyModelProjectionEnabled)));

        return section;
    }

    private Control BuildActivePackEditorSection()
    {
        if (_activeCraniumKnowledgePack is null)
        {
            (Border emptySection, _) = CreateAnatomySectionHost(
                "Active pack editor",
                "No active Cranium pack loaded. Reload or create a pack first.",
                _anatomyPackEditorSectionExpanded,
                expanded => _anatomyPackEditorSectionExpanded = expanded);
            return emptySection;
        }

        StudyMeasurement? selectedRoi = GetSelectedVolumeRoiMeasurement();
        AnatomyStructureDefinition? selectedStructure = GetSelectedActivePackStructure();
        List<AnatomyStructureDefinition> orderedStructures = _activeCraniumKnowledgePack.Structures
            .OrderBy(candidate => candidate.DisplayName)
            .ThenBy(candidate => candidate.Id)
            .ToList();
        if (selectedStructure is null && orderedStructures.Count > 0)
        {
            selectedStructure = orderedStructures[0];
            _selectedActivePackStructureId = selectedStructure.Id;
        }

        (Border section, StackPanel body) = CreateAnatomySectionHost(
            "Active pack editor",
            "Edit the active Cranium pack directly here. This is the developer-facing source of truth for the new anatomy model.",
            _anatomyPackEditorSectionExpanded,
            expanded => _anatomyPackEditorSectionExpanded = expanded);

        List<string> structureOptions = orderedStructures
            .Select(candidate => $"{candidate.DisplayName} ({candidate.Id})")
            .ToList();
        string? selectedStructureOption = selectedStructure is null
            ? null
            : $"{selectedStructure.DisplayName} ({selectedStructure.Id})";

        var structureSelector = new ComboBox
        {
            ItemsSource = structureOptions,
            SelectedItem = selectedStructureOption,
            MinWidth = 220,
            MaxWidth = 320,
        };
        StyleAnatomyComboBox(structureSelector);

        var createButton = CreateAnatomyActionButton("New empty structure", "#FF1C5A3F", "#FF4FD08B", minWidth: 140);
        createButton.Click += (_, _) =>
        {
            AnatomyStructureDefinition created = new()
            {
                Id = $"structure_{Guid.NewGuid():N}"[..18],
                DisplayName = "New Structure",
                AllowedCompartments = ["intracranial_space"],
            };
            _activeCraniumKnowledgePack.Structures.Add(created);
            _selectedActivePackStructureId = created.Id;
            RefreshAnatomyProjectionUi(forceVisible: true);
            ShowToast("Created a new empty structure in the active pack.", ToastSeverity.Success);
        };

        var deleteButton = CreateAnatomyActionButton("Delete structure", "#FF5C2431", "#FFEB7D96", minWidth: 120);
        deleteButton.IsEnabled = selectedStructure is not null;
        deleteButton.Click += async (_, _) => await DeleteSelectedActivePackStructureAsync(selectedStructure);

        var projectModelsButton = CreateAnatomyActionButton(_developerAnatomyModelProjectionEnabled ? "Hide projected pack structure" : "Project selected pack structure", "#FF234C73", "#FF73C2FF", minWidth: 194);
        ToolTip.SetTip(projectModelsButton, "Project the currently selected active-pack structure into all loaded volume series using the pack definition itself.");
        projectModelsButton.Click += (_, _) => ToggleDeveloperAnatomyModelProjection();

        body.Children.Add(CreateAnatomyEditorCard(
            "Structure selection",
            "Choose the active structure, create a new one, or project the selected structure into loaded volumes for visual validation.",
            CreateFieldRow("Structure", structureSelector),
            CreateActionRow(createButton, deleteButton, projectModelsButton),
            CreateAnatomyStatusText(BuildDeveloperAnatomyProjectionStatusText(), _developerAnatomyModelProjectionEnabled)));

        if (selectedStructure is null)
        {
            body.Children.Add(new TextBlock
            {
                Text = "The active pack currently has no structures. Create one or feed a selected ROI into the pack.",
                Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
            });
            return section;
        }

        structureSelector.SelectionChanged += (_, _) =>
        {
            string? selectedOption = structureSelector.SelectedItem as string;
            AnatomyStructureDefinition? matchedStructure = orderedStructures.FirstOrDefault(candidate =>
                string.Equals($"{candidate.DisplayName} ({candidate.Id})", selectedOption, StringComparison.Ordinal));
            _selectedActivePackStructureId = matchedStructure?.Id ?? string.Empty;
            RefreshAnatomyProjectionUi(forceVisible: true);
        };

        var idBox = new TextBox { Text = selectedStructure.Id, MinWidth = 220, MaxWidth = 320 };
        var nameBox = new TextBox { Text = selectedStructure.DisplayName, MinWidth = 220, MaxWidth = 320 };
        var compartmentsBox = new TextBox { Text = JoinList(selectedStructure.AllowedCompartments), MinWidth = 220, MaxWidth = 420 };
        var modalitiesBox = new TextBox { Text = JoinList(selectedStructure.SupportedModalities), MinWidth = 220, MaxWidth = 420 };
        var ctClassBox = new TextBox { Text = JoinList(selectedStructure.ExpectedMaterial.CtClass), MinWidth = 220, MaxWidth = 420 };
        var mrT1Box = new TextBox { Text = JoinList(selectedStructure.ExpectedMaterial.MrT1), MinWidth = 220, MaxWidth = 420 };
        var mrT2Box = new TextBox { Text = JoinList(selectedStructure.ExpectedMaterial.MrT2), MinWidth = 220, MaxWidth = 420 };
        var mrFlairBox = new TextBox { Text = JoinList(selectedStructure.ExpectedMaterial.MrFlair), MinWidth = 220, MaxWidth = 420 };
        var leftRightRangeBox = new TextBox { Text = FormatRange(selectedStructure.ExpectedPosition.LeftRight), MinWidth = 120, MaxWidth = 180 };
        var anteriorPosteriorRangeBox = new TextBox { Text = FormatRange(selectedStructure.ExpectedPosition.AnteriorPosterior), MinWidth = 120, MaxWidth = 180 };
        var cranialCaudalRangeBox = new TextBox { Text = FormatRange(selectedStructure.ExpectedPosition.CranialCaudal), MinWidth = 120, MaxWidth = 180 };
        var midlineRangeBox = new TextBox { Text = FormatRange(selectedStructure.ExpectedPosition.DistanceToMidline), MinWidth = 120, MaxWidth = 180 };
        var skullBaseRangeBox = new TextBox { Text = FormatRange(selectedStructure.ExpectedPosition.DistanceToSkullBase), MinWidth = 120, MaxWidth = 180 };
        var vertexRangeBox = new TextBox { Text = FormatRange(selectedStructure.ExpectedPosition.DistanceToVertex), MinWidth = 120, MaxWidth = 180 };
        var requiredRelationsBox = new TextBox
        {
            Text = FormatRelations(selectedStructure.RequiredRelations),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 220,
            MaxWidth = 520,
            MinHeight = 52,
        };
        var forbiddenRelationsBox = new TextBox
        {
            Text = FormatRelations(selectedStructure.ForbiddenRelations),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 220,
            MaxWidth = 520,
            MinHeight = 52,
        };

        foreach (TextBox box in new[]
        {
            idBox, nameBox, compartmentsBox, modalitiesBox, ctClassBox, mrT1Box, mrT2Box, mrFlairBox,
            leftRightRangeBox, anteriorPosteriorRangeBox, cranialCaudalRangeBox, midlineRangeBox, skullBaseRangeBox, vertexRangeBox,
            requiredRelationsBox, forbiddenRelationsBox
        })
        {
            StyleAnatomyTextBox(box);
        }

        var saveButton = CreateAnatomyActionButton("Apply structure edits", "#FF215E91", "#FF4FA3FF", minWidth: 150, height: 30);
        saveButton.Click += async (_, _) =>
        {
            string structureId = idBox.Text?.Trim() ?? string.Empty;
            string displayName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(structureId) || string.IsNullOrWhiteSpace(displayName))
            {
                ShowToast("Structure id and display name are required.", ToastSeverity.Warning);
                return;
            }

            selectedStructure.Id = structureId;
            selectedStructure.DisplayName = displayName;
            selectedStructure.AllowedCompartments = ParseList(compartmentsBox.Text);
            selectedStructure.SupportedModalities = ParseList(modalitiesBox.Text);
            selectedStructure.ExpectedMaterial.CtClass = ParseList(ctClassBox.Text);
            selectedStructure.ExpectedMaterial.MrT1 = ParseList(mrT1Box.Text);
            selectedStructure.ExpectedMaterial.MrT2 = ParseList(mrT2Box.Text);
            selectedStructure.ExpectedMaterial.MrFlair = ParseList(mrFlairBox.Text);
            ApplyRangeText(selectedStructure.ExpectedPosition.LeftRight, leftRightRangeBox.Text);
            ApplyRangeText(selectedStructure.ExpectedPosition.AnteriorPosterior, anteriorPosteriorRangeBox.Text);
            ApplyRangeText(selectedStructure.ExpectedPosition.CranialCaudal, cranialCaudalRangeBox.Text);
            ApplyRangeText(selectedStructure.ExpectedPosition.DistanceToMidline, midlineRangeBox.Text);
            ApplyRangeText(selectedStructure.ExpectedPosition.DistanceToSkullBase, skullBaseRangeBox.Text);
            ApplyRangeText(selectedStructure.ExpectedPosition.DistanceToVertex, vertexRangeBox.Text);
            selectedStructure.RequiredRelations = ParseRelations(requiredRelationsBox.Text);
            selectedStructure.ForbiddenRelations = ParseRelations(forbiddenRelationsBox.Text);
            selectedStructure.Normalize();
            _selectedActivePackStructureId = selectedStructure.Id;
            await SaveActiveAnatomyPackAsync();
            RefreshAnatomyProjectionUi(forceVisible: true);
            ShowToast($"Saved structure {selectedStructure.DisplayName} in the active pack.", ToastSeverity.Success);
        };

        body.Children.Add(CreateAnatomyEditorCard(
            "Identity & scope",
            "Core identifiers and the broad anatomical scope of the structure.",
            CreateFieldRow("Id", idBox),
            CreateFieldRow("Name", nameBox),
            CreateFieldRow("Compartments", compartmentsBox),
            CreateFieldRow("Modalities", modalitiesBox)));
        body.Children.Add(CreateAnatomyEditorCard(
            "Material profile",
            "Define modality-specific appearance hints for CT and MR sequences.",
            CreateFieldRow("CT class", ctClassBox),
            CreateFieldRow("MR T1", mrT1Box),
            CreateFieldRow("MR T2", mrT2Box),
            CreateFieldRow("MR FLAIR", mrFlairBox)));
        body.Children.Add(CreateAnatomyEditorCard(
            "Spatial envelope",
            "Expected semantic extent of the structure relative to canonical head axes.",
            CreateFieldRow("LR range", leftRightRangeBox),
            CreateFieldRow("AP range", anteriorPosteriorRangeBox),
            CreateFieldRow("CC range", cranialCaudalRangeBox),
            CreateFieldRow("Midline", midlineRangeBox),
            CreateFieldRow("Skull base", skullBaseRangeBox),
            CreateFieldRow("Vertex", vertexRangeBox)));
        body.Children.Add(CreateAnatomyEditorCard(
            "Relations",
            "Hard and soft constraints to neighboring structures.",
            CreateFieldRow("Required", requiredRelationsBox),
            CreateFieldRow("Forbidden", forbiddenRelationsBox),
            CreateAnatomyNoteText("Relations format: type:target:strength — one per line or comma separated. Example: cranial_to:brainstem:hard"),
            CreateActionRow(saveButton)));

        return section;
    }

    private Control BuildLearnedModelCard(VolumeRoiAnatomyPriorRecord prior, StudyMeasurement? selectedRoi)
    {
        List<string> regionOptions = GetAllAnatomyRegionOptions();
        if (!regionOptions.Contains(prior.RegionLabel, StringComparer.OrdinalIgnoreCase))
        {
            regionOptions.Add(prior.RegionLabel);
        }

        var regionCombo = new ComboBox
        {
            ItemsSource = regionOptions,
            SelectedItem = prior.RegionLabel,
            MinWidth = 150,
            MaxWidth = 220,
        };
        StyleAnatomyComboBox(regionCombo);

        var anatomyBox = new TextBox
        {
            Text = prior.AnatomyLabel,
            MinWidth = 150,
            MaxWidth = 220,
        };
        StyleAnatomyTextBox(anatomyBox);

        var saveButton = CreateAnatomyActionButton("Save labels", minWidth: 98);
        saveButton.Click += async (_, _) =>
        {
            string regionLabel = regionCombo.SelectedItem as string ?? prior.RegionLabel;
            string anatomyLabel = anatomyBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(anatomyLabel))
            {
                ShowToast("Anatomy label cannot be empty.", ToastSeverity.Warning);
                return;
            }

            await UpdateLearnedModelLabelsAsync(prior, regionLabel, anatomyLabel);
        };

        var deleteButton = CreateAnatomyActionButton("Delete", "#FF5C2431", "#FFEB7D96", minWidth: 78);
        deleteButton.Click += async (_, _) => await DeleteLearnedModelAsync(prior);

        var relearnButton = CreateAnatomyActionButton("Learn selected ROI", "#FF1C5A3F", "#FF4FD08B", minWidth: 134);
        relearnButton.IsEnabled = selectedRoi is not null;
        relearnButton.Click += async (_, _) =>
        {
            if (selectedRoi is null)
            {
                return;
            }

            string regionLabel = regionCombo.SelectedItem as string ?? prior.RegionLabel;
            string anatomyLabel = anatomyBox.Text?.Trim() ?? prior.AnatomyLabel;
            await AssignMeasurementToAnatomyAsync(selectedRoi.Id, regionLabel, anatomyLabel, persistLegacyPrior: true);
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#181F2630")),
            BorderBrush = new SolidColorBrush(Color.Parse("#406E8CA3")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{prior.AnatomyLabel} · {prior.RegionLabel}",
                        Foreground = new SolidColorBrush(Color.Parse("#FFF2F6FA")),
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                    },
                    new TextBlock
                    {
                        Text = $"{prior.Modality} · {prior.SeriesDescription} · used {prior.UseCount}×",
                        Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CreateFieldRow("Region", regionCombo),
                    CreateFieldRow("Structure", anatomyBox),
                    new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { saveButton, deleteButton, relearnButton } },
                }
            }
        };
    }

    private static (Border Section, StackPanel Body) CreateAnatomySectionHost(string title, string subtitle, bool isExpanded, Action<bool> expandedChanged)
    {
        var body = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var indicatorText = new TextBlock
        {
            Text = isExpanded ? "▾" : "▸",
            Foreground = new SolidColorBrush(Color.Parse("#FF8BB8E8")),
            FontSize = 12,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var headerText = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    Foreground = new SolidColorBrush(Color.Parse("#FFF2F6FA")),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 11,
                },
                new TextBlock
                {
                    Text = subtitle,
                    Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                },
            }
        };

        var expander = new Expander
        {
            IsExpanded = isExpanded,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("22,*"),
                ColumnSpacing = 6,
                Children =
                {
                    indicatorText,
                }
            },
            Content = body,
        };
        Grid.SetColumn(headerText, 1);
        ((Grid)expander.Header!).Children.Add(headerText);

        expander.Expanded += (_, _) =>
        {
            indicatorText.Text = "▾";
            expandedChanged(true);
        };
        expander.Collapsed += (_, _) =>
        {
            indicatorText.Text = "▸";
            expandedChanged(false);
        };

        return (
            new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1B16212D")),
                BorderBrush = new SolidColorBrush(Color.Parse("#4C6B8AA5")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 10),
                Child = expander,
            },
            body);
    }

    private static Control CreateFieldRow(string label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("108,*"),
            ColumnSpacing = 12,
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = editor is TextBox { AcceptsReturn: true } ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = editor is TextBox { AcceptsReturn: true } ? new Thickness(0, 8, 0, 0) : default,
            Foreground = new SolidColorBrush(Color.Parse("#FFD0DFEC")),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
        });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private static Border CreateAnatomyEditorCard(string title, string subtitle, params Control[] content)
    {
        var body = new StackPanel { Spacing = 8 };
        foreach (Control child in content)
        {
            body.Children.Add(child);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#162B3B4D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#305E7C94")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = new SolidColorBrush(Color.Parse("#FFF4F9FD")),
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        Foreground = new SolidColorBrush(Color.Parse("#FF97B1C7")),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    body,
                }
            }
        };
    }

    private static WrapPanel CreateActionRow(params Control[] controls)
    {
        var panel = new WrapPanel
        {
            ItemSpacing = 10,
            LineSpacing = 8,
            Margin = new Thickness(0, 2, 0, 0),
        };

        foreach (Control control in controls)
        {
            panel.Children.Add(control);
        }

        return panel;
    }

    private static TextBlock CreateAnatomyNoteText(string text) =>
        new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        };

    private static TextBlock CreateAnatomyStatusText(string text, bool accent) =>
        new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse(accent ? "#FFC9F7D9" : "#FF9DB3C7")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        };

    private static CheckBox CreateAnatomyInlineCheckBox(string text, bool isChecked, string? toolTip = null)
    {
        var checkBox = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = new SolidColorBrush(Color.Parse("#FFD7E7F4")),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };

        if (!string.IsNullOrWhiteSpace(toolTip))
        {
            ToolTip.SetTip(checkBox, toolTip);
        }

        return checkBox;
    }

    private void ToggleReportDebug(bool enabled)
    {
        if (_reportDebugEnabled == enabled)
        {
            return;
        }

        _reportDebugEnabled = enabled;

        if (_reportPanelVisible || _reportPanelPinned)
        {
            RefreshReportPanel(forceVisible: _reportPanelPinned);
        }

        if (_anatomyPanelVisible || _anatomyPanelPinned)
        {
            RefreshAnatomyPanel(forceVisible: _anatomyPanelPinned);
        }
    }

    private static Button CreateAnatomyActionButton(string content, string backgroundHex = "#FF203749", string borderHex = "#FF5B89B3", double minWidth = 100, double height = 28)
    {
        return new Button
        {
            Content = content,
            MinWidth = minWidth,
            Height = height,
            Padding = new Thickness(12, 6),
            Background = new SolidColorBrush(Color.Parse(backgroundHex)),
            BorderBrush = new SolidColorBrush(Color.Parse(borderHex)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.Parse("#FFF5FAFF")),
        };
    }

    private void StyleAnatomyComboBox(ComboBox comboBox)
    {
        ApplyReportComboBoxStyling(comboBox);
        comboBox.MinHeight = 34;
        comboBox.Padding = new Thickness(10, 6);
        comboBox.Background = new SolidColorBrush(Color.Parse("#FF202C38"));
        comboBox.BorderBrush = new SolidColorBrush(Color.Parse("#FF5A7994"));
        comboBox.Foreground = new SolidColorBrush(Color.Parse("#FFF2F6FA"));
    }

    private static void StyleAnatomyTextBox(TextBox textBox)
    {
        textBox.MinHeight = Math.Max(textBox.MinHeight, textBox.AcceptsReturn ? 52 : 34);
        textBox.Padding = textBox.AcceptsReturn ? new Thickness(10, 8) : new Thickness(10, 6);
        textBox.Background = new SolidColorBrush(Color.Parse("#FF202C38"));
        textBox.BorderBrush = new SolidColorBrush(Color.Parse("#FF5A7994"));
        textBox.Foreground = new SolidColorBrush(Color.Parse("#FFF2F6FA"));
    }

    private List<string> GetAllAnatomyRegionOptions()
    {
        List<string> regions = [.. s_reportRegionOptions.Where(option => !string.Equals(option, "Unassigned", StringComparison.OrdinalIgnoreCase))];
        foreach (string region in _customAnatomyRegions)
        {
            if (!regions.Contains(region, StringComparer.OrdinalIgnoreCase))
            {
                regions.Add(region);
            }
        }

        if (!regions.Contains("Unassigned", StringComparer.OrdinalIgnoreCase))
        {
            regions.Add("Unassigned");
        }

        return regions;
    }

    private List<string> GetAnatomyStructureOptionsForRegion(string? regionLabel)
    {
        string normalizedRegion = regionLabel?.Trim() ?? string.Empty;
        List<string> options = normalizedRegion switch
        {
            "Neuro" => [.. s_neuroAnatomyOptions],
            "Upper thorax" or "Lower thorax" => [.. s_thoraxAnatomyOptions],
            "Upper abdomen" or "Lower abdomen" => [.. s_abdomenAnatomyOptions],
            "Pelvis" => [.. s_pelvisAnatomyOptions],
            "Shoulder" => MergeAnatomyOptions(s_shoulderAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
            "Knee" => MergeAnatomyOptions(s_kneeAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
            "Ankle" => MergeAnatomyOptions(s_ankleAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
            _ => MergeAnatomyOptions(s_thoraxAnatomyOptions, s_abdomenAnatomyOptions, s_pelvisAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
        };

        if (_customAnatomyStructuresByRegion.TryGetValue(normalizedRegion, out List<string>? customStructures))
        {
            foreach (string structure in customStructures)
            {
                if (!options.Contains(structure, StringComparer.OrdinalIgnoreCase))
                {
                    options.Add(structure);
                }
            }
        }

        if (!options.Contains("Unassigned", StringComparer.OrdinalIgnoreCase))
        {
            options.Add("Unassigned");
        }

        return options;
    }

    private async Task AssignMeasurementToAnatomyAsync(Guid measurementId, string regionLabel, string anatomyLabel, bool persistLegacyPrior = false)
    {
        EnsureCatalogRegion(regionLabel);
        EnsureCatalogStructure(regionLabel, anatomyLabel);
        _reportRegionOverrides[measurementId] = regionLabel.Trim();
        _reportAnatomyOverrides[measurementId] = anatomyLabel.Trim();
        _reportReviewStates[measurementId] = "Confirmed";
        SaveViewerSettings();
        RefreshReportPanel(forceVisible: _reportPanelVisible || _reportPanelPinned);
        if (persistLegacyPrior)
        {
            await PersistVolumeRoiAnatomyPriorAsync(measurementId, anatomyLabel.Trim());
            ShowToast($"Assigned ROI to {anatomyLabel.Trim()} ({regionLabel.Trim()}) and updated the legacy learned model.", ToastSeverity.Success);
        }
        else
        {
            ShowToast($"Assigned ROI to {anatomyLabel.Trim()} ({regionLabel.Trim()}).", ToastSeverity.Success);
        }

        RefreshAnatomyPanel(forceVisible: true);
    }

    private void EnsureCatalogRegion(string regionLabel)
    {
        string label = regionLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(label) || s_reportRegionOptions.Contains(label, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_customAnatomyRegions.Contains(label, StringComparer.OrdinalIgnoreCase))
        {
            _customAnatomyRegions.Add(label);
        }
    }

    private void EnsureCatalogStructure(string regionLabel, string anatomyLabel)
    {
        string region = regionLabel?.Trim() ?? string.Empty;
        string anatomy = anatomyLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(anatomy) || string.Equals(anatomy, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_customAnatomyStructuresByRegion.TryGetValue(region, out List<string>? structures))
        {
            structures = [];
            _customAnatomyStructuresByRegion[region] = structures;
        }

        if (!structures.Contains(anatomy, StringComparer.OrdinalIgnoreCase) &&
            !GetAnatomyStructureOptionsForRegion(region).Contains(anatomy, StringComparer.OrdinalIgnoreCase))
        {
            structures.Add(anatomy);
        }
    }

    private void AddCatalogRegion(string? value)
    {
        string region = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region))
        {
            ShowToast("Enter a region name first.", ToastSeverity.Warning);
            return;
        }

        EnsureCatalogRegion(region);
        _selectedAnatomyCatalogRegion = region;
        SaveViewerSettings();
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Region {region} added.", ToastSeverity.Success);
    }

    private async Task RenameCatalogRegionAsync(string existingRegion, string? updatedValue)
    {
        string from = existingRegion?.Trim() ?? string.Empty;
        string to = updatedValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        EnsureCatalogRegion(to);
        if (_customAnatomyStructuresByRegion.Remove(from, out List<string>? structures))
        {
            _customAnatomyStructuresByRegion[to] = structures;
        }

        _customAnatomyRegions.RemoveAll(region => string.Equals(region, from, StringComparison.OrdinalIgnoreCase));
        if (!s_reportRegionOptions.Contains(to, StringComparer.OrdinalIgnoreCase) && !_customAnatomyRegions.Contains(to, StringComparer.OrdinalIgnoreCase))
        {
            _customAnatomyRegions.Add(to);
        }

        foreach (Guid measurementId in _reportRegionOverrides.Keys.ToList())
        {
            if (string.Equals(_reportRegionOverrides[measurementId], from, StringComparison.OrdinalIgnoreCase))
            {
                _reportRegionOverrides[measurementId] = to;
            }
        }

        foreach (VolumeRoiAnatomyPriorRecord prior in _volumeRoiAnatomyPriors.Where(prior => string.Equals(prior.RegionLabel, from, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            await app.Repository.UpdateVolumeRoiAnatomyPriorLabelsAsync(prior.PriorKey, to, prior.AnatomyLabel, _priorLookupCancellation.Token);
            int index = _volumeRoiAnatomyPriors.FindIndex(candidate => candidate.PriorKey == prior.PriorKey);
            if (index >= 0)
            {
                _volumeRoiAnatomyPriors[index] = prior with { RegionLabel = to, UpdatedAtUtc = DateTime.UtcNow };
            }
        }

        _selectedAnatomyCatalogRegion = to;
        SaveViewerSettings();
        RefreshReportPanel(forceVisible: _reportPanelVisible || _reportPanelPinned);
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Region {from} renamed to {to}.", ToastSeverity.Success);
    }

    private void DeleteCatalogRegion(string regionLabel)
    {
        string region = regionLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region))
        {
            return;
        }

        if (_volumeRoiAnatomyPriors.Any(prior => string.Equals(prior.RegionLabel, region, StringComparison.OrdinalIgnoreCase)) ||
            _reportRegionOverrides.Values.Any(value => string.Equals(value, region, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Region is still used by learned models or active findings and cannot be deleted yet.", ToastSeverity.Warning);
            return;
        }

        _customAnatomyRegions.RemoveAll(value => string.Equals(value, region, StringComparison.OrdinalIgnoreCase));
        _customAnatomyStructuresByRegion.Remove(region);
        _selectedAnatomyCatalogRegion = GetAllAnatomyRegionOptions().FirstOrDefault() ?? "Neuro";
        SaveViewerSettings();
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Region {region} deleted from the anatomy catalog.", ToastSeverity.Success);
    }

    private void AddCatalogStructure(string regionLabel, string? value)
    {
        string region = regionLabel?.Trim() ?? string.Empty;
        string structure = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(structure))
        {
            ShowToast("Choose a region and enter a structure name first.", ToastSeverity.Warning);
            return;
        }

        EnsureCatalogRegion(region);
        EnsureCatalogStructure(region, structure);
        _selectedAnatomyCatalogStructure = structure;
        SaveViewerSettings();
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Structure {structure} added to {region}.", ToastSeverity.Success);
    }

    private async Task RenameCatalogStructureAsync(string regionLabel, string existingStructure, string? updatedValue)
    {
        string region = regionLabel?.Trim() ?? string.Empty;
        string from = existingStructure?.Trim() ?? string.Empty;
        string to = updatedValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        EnsureCatalogStructure(region, to);
        if (_customAnatomyStructuresByRegion.TryGetValue(region, out List<string>? structures))
        {
            structures.RemoveAll(value => string.Equals(value, from, StringComparison.OrdinalIgnoreCase));
            if (!structures.Contains(to, StringComparer.OrdinalIgnoreCase) && !GetAnatomyStructureOptionsForRegion(region).Contains(to, StringComparer.OrdinalIgnoreCase))
            {
                structures.Add(to);
            }
        }

        foreach (Guid measurementId in _reportAnatomyOverrides.Keys.ToList())
        {
            if (string.Equals(_reportAnatomyOverrides[measurementId], from, StringComparison.OrdinalIgnoreCase))
            {
                _reportAnatomyOverrides[measurementId] = to;
            }
        }

        foreach (VolumeRoiAnatomyPriorRecord prior in _volumeRoiAnatomyPriors.Where(prior => string.Equals(prior.AnatomyLabel, from, StringComparison.OrdinalIgnoreCase) && string.Equals(prior.RegionLabel, region, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            await app.Repository.UpdateVolumeRoiAnatomyPriorLabelsAsync(prior.PriorKey, region, to, _priorLookupCancellation.Token);
            int index = _volumeRoiAnatomyPriors.FindIndex(candidate => candidate.PriorKey == prior.PriorKey);
            if (index >= 0)
            {
                _volumeRoiAnatomyPriors[index] = prior with { AnatomyLabel = to, UpdatedAtUtc = DateTime.UtcNow };
            }
        }

        _selectedAnatomyCatalogStructure = to;
        SaveViewerSettings();
        RefreshReportPanel(forceVisible: _reportPanelVisible || _reportPanelPinned);
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Structure {from} renamed to {to}.", ToastSeverity.Success);
    }

    private void DeleteCatalogStructure(string regionLabel, string structureLabel)
    {
        string region = regionLabel?.Trim() ?? string.Empty;
        string structure = structureLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(structure))
        {
            return;
        }

        if (_volumeRoiAnatomyPriors.Any(prior => string.Equals(prior.RegionLabel, region, StringComparison.OrdinalIgnoreCase) && string.Equals(prior.AnatomyLabel, structure, StringComparison.OrdinalIgnoreCase)) ||
            _reportAnatomyOverrides.Values.Any(value => string.Equals(value, structure, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Structure is still used by learned models or active findings and cannot be deleted yet.", ToastSeverity.Warning);
            return;
        }

        if (_customAnatomyStructuresByRegion.TryGetValue(region, out List<string>? structures))
        {
            structures.RemoveAll(value => string.Equals(value, structure, StringComparison.OrdinalIgnoreCase));
            if (structures.Count == 0)
            {
                _customAnatomyStructuresByRegion.Remove(region);
            }
        }

        _selectedAnatomyCatalogStructure = string.Empty;
        SaveViewerSettings();
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Structure {structure} deleted from {region}.", ToastSeverity.Success);
    }

    private async Task UpdateLearnedModelLabelsAsync(VolumeRoiAnatomyPriorRecord prior, string regionLabel, string anatomyLabel)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        EnsureCatalogRegion(regionLabel);
        EnsureCatalogStructure(regionLabel, anatomyLabel);
        await app.Repository.UpdateVolumeRoiAnatomyPriorLabelsAsync(prior.PriorKey, regionLabel, anatomyLabel, _priorLookupCancellation.Token);
        int index = _volumeRoiAnatomyPriors.FindIndex(candidate => candidate.PriorKey == prior.PriorKey);
        if (index >= 0)
        {
            _volumeRoiAnatomyPriors[index] = prior with { RegionLabel = regionLabel.Trim(), AnatomyLabel = anatomyLabel.Trim(), UpdatedAtUtc = DateTime.UtcNow };
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: _reportPanelVisible || _reportPanelPinned);
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Updated learned model to {anatomyLabel.Trim()} ({regionLabel.Trim()}).", ToastSeverity.Success);
    }

    private async Task DeleteLearnedModelAsync(VolumeRoiAnatomyPriorRecord prior)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        await app.Repository.DeleteVolumeRoiAnatomyPriorAsync(prior.PriorKey, _priorLookupCancellation.Token);
        _volumeRoiAnatomyPriors.RemoveAll(candidate => candidate.PriorKey == prior.PriorKey);
        RefreshReportPanel(forceVisible: _reportPanelVisible || _reportPanelPinned);
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Deleted learned model {prior.AnatomyLabel} ({prior.RegionLabel}).", ToastSeverity.Success);
    }

    private StudyMeasurement? GetSelectedVolumeRoiMeasurement() =>
        _selectedMeasurementId is Guid measurementId
            ? _studyMeasurements.FirstOrDefault(candidate => candidate.Id == measurementId && candidate.Kind == MeasurementKind.VolumeRoi)
            : null;

    private string BuildSelectedMeasurementLabel(StudyMeasurement measurement)
    {
        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        string seriesLabel = slot?.Series is null ? "No loaded series" : $"{slot.Series.Modality} · Series {slot.Series.SeriesNumber}";
        return $"{seriesLabel} · Measurement {measurement.Id.ToString("D")[..8]}";
    }

    private async Task ReloadAnatomyKnowledgePacksAsync()
    {
        await LoadAnatomyKnowledgePacksAsync();
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast(_activeCraniumKnowledgePack is null
            ? "No Cranium anatomy pack is currently active."
            : $"Reloaded anatomy packs. Active pack: {_activeCraniumKnowledgePack.DisplayName}.", ToastSeverity.Success);
    }

    private async Task ExportActiveAnatomyPackAsync()
    {
        if (_activeCraniumKnowledgePack is null || string.IsNullOrWhiteSpace(_anatomyKnowledgePackDirectory))
        {
            ShowToast("No active anatomy pack available for export.", ToastSeverity.Warning);
            return;
        }

        string exportDirectory = Path.Combine(_anatomyKnowledgePackDirectory, "exports");
        Directory.CreateDirectory(exportDirectory);
        string safePackId = string.Concat(_activeCraniumKnowledgePack.PackId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        string exportPath = Path.Combine(exportDirectory, $"{safePackId}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await _anatomyKnowledgePackService.SaveToFileAsync(_activeCraniumKnowledgePack, exportPath);
        ShowToast($"Active pack exported to {exportPath}.", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private async Task SaveActiveAnatomyPackAsync()
    {
        if (_activeCraniumKnowledgePack is null)
        {
            ShowToast("No active pack available.", ToastSeverity.Warning);
            return;
        }

        string? targetPath = _activeCraniumKnowledgePackPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = _defaultCraniumKnowledgePackPath;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            ShowToast("No target path available for the active pack.", ToastSeverity.Warning);
            return;
        }

        _activeCraniumKnowledgePack.Normalize();
        await _anatomyKnowledgePackService.SaveToFileAsync(_activeCraniumKnowledgePack, targetPath, _priorLookupCancellation.Token);
        _activeCraniumKnowledgePackPath = targetPath;
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Active pack saved to {targetPath}.", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private AnatomyStructureDefinition? GetSelectedActivePackStructure()
    {
        if (_activeCraniumKnowledgePack is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_selectedActivePackStructureId))
        {
            AnatomyStructureDefinition? existing = _activeCraniumKnowledgePack.Structures.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, _selectedActivePackStructureId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }
        }

        return _activeCraniumKnowledgePack.Structures.FirstOrDefault();
    }

    private async Task DeleteSelectedActivePackStructureAsync(AnatomyStructureDefinition? selectedStructure)
    {
        if (_activeCraniumKnowledgePack is null || selectedStructure is null)
        {
            return;
        }

        _activeCraniumKnowledgePack.Structures.Remove(selectedStructure);
        _selectedActivePackStructureId = _activeCraniumKnowledgePack.Structures.FirstOrDefault()?.Id ?? string.Empty;
        await SaveActiveAnatomyPackAsync();
        RefreshAnatomyProjectionUi(forceVisible: true);
        ShowToast($"Deleted structure {selectedStructure.DisplayName} from the active pack.", ToastSeverity.Success);
    }

    private void RefreshAnatomyProjectionUi(bool forceVisible = false)
    {
        if (_developerAnatomyModelProjectionEnabled)
        {
            RefreshMeasurementPanels();
        }

        RefreshAnatomyPanel(forceVisible);
    }

    private async Task ExportLegacyPriorsSnapshotAsync()
    {
        if (_volumeRoiAnatomyPriors.Count == 0 || string.IsNullOrWhiteSpace(_anatomyKnowledgePackDirectory))
        {
            ShowToast("No legacy priors available for export.", ToastSeverity.Warning);
            return;
        }

        string exportDirectory = Path.Combine(_anatomyKnowledgePackDirectory, "legacy-snapshots");
        Directory.CreateDirectory(exportDirectory);
        string exportPath = Path.Combine(exportDirectory, $"legacy-volume-roi-priors-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        string json = JsonSerializer.Serialize(_volumeRoiAnatomyPriors, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(exportPath, json, _priorLookupCancellation.Token);
        ShowToast($"Legacy priors exported to {exportPath}.", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private void ToggleLegacyPriorUsage()
    {
        _useLegacyAnatomyPriors = !_useLegacyAnatomyPriors;
        RefreshReportPanel(forceVisible: _reportPanelVisible || _reportPanelPinned);
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast(_useLegacyAnatomyPriors
            ? "Legacy ROI priors enabled for matching."
            : "Legacy ROI priors disabled. Matching now relies on the new knowledge-pack path only.", ToastSeverity.Info, TimeSpan.FromSeconds(5));
    }

    private async Task ExportSelectedRoiDraftAsync(StudyMeasurement? selectedRoi)
    {
        if (selectedRoi is null)
        {
            ShowToast("Select a 3D ROI first.", ToastSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_anatomyKnowledgePackDirectory))
        {
            ShowToast("No anatomy pack directory available.", ToastSeverity.Warning);
            return;
        }

        ViewportSlot? slot = FindSlotForMeasurement(selectedRoi) ?? _activeSlot;
        if (!TryBuildMeasurementPriorProbe(selectedRoi, slot, out VolumeRoiPriorProbe probe))
        {
            ShowToast("Could not build an ROI draft from the selected measurement.", ToastSeverity.Warning);
            return;
        }

        string regionLabel = _reportRegionOverrides.TryGetValue(selectedRoi.Id, out string? regionOverride) && !string.IsNullOrWhiteSpace(regionOverride)
            ? regionOverride.Trim()
            : ResolveMeasurementRegion(selectedRoi, slot).Label;
        string anatomyLabel = _reportAnatomyOverrides.TryGetValue(selectedRoi.Id, out string? anatomyOverride) && !string.IsNullOrWhiteSpace(anatomyOverride)
            ? anatomyOverride.Trim()
            : ResolveMeasurementAnatomy(selectedRoi, slot).Label;

        string draftDirectory = Path.Combine(_anatomyKnowledgePackDirectory, "roi-drafts");
        Directory.CreateDirectory(draftDirectory);
        string draftPath = Path.Combine(draftDirectory, $"roi-draft-{selectedRoi.Id:N}-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        var draft = new
        {
            exportedAtLocal = DateTime.Now,
            activePackId = _activeCraniumKnowledgePack?.PackId,
            activePackVersion = _activeCraniumKnowledgePack?.PackVersion,
            studyInstanceUid = _context.StudyDetails.Study.StudyInstanceUid,
            seriesInstanceUid = slot?.Series?.SeriesInstanceUid ?? string.Empty,
            measurementId = selectedRoi.Id,
            modality = probe.Modality,
            regionLabel,
            anatomyLabel,
            normalizedProbe = new
            {
                probe.NormalizedCenterX,
                probe.NormalizedCenterY,
                probe.NormalizedCenterZ,
                probe.NormalizedSizeX,
                probe.NormalizedSizeY,
                probe.NormalizedSizeZ,
                probe.EstimatedVolumeCubicMillimeters,
                probe.BodyPartExamined,
                probe.StudyDescription,
                probe.SeriesDescription,
                probe.StructureSignature,
            }
        };

        string json = JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(draftPath, json, _priorLookupCancellation.Token);
        ShowToast($"ROI draft exported to {draftPath}.", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private async Task FeedSelectedRoiIntoActivePackAsync(StudyMeasurement? selectedRoi)
    {
        if (selectedRoi is null)
        {
            ShowToast("Select a 3D ROI first.", ToastSeverity.Warning);
            return;
        }

        if (_activeCraniumKnowledgePack is null)
        {
            ShowToast("No active Cranium pack available.", ToastSeverity.Warning);
            return;
        }

        ViewportSlot? slot = FindSlotForMeasurement(selectedRoi) ?? _activeSlot;
        if (!TryBuildMeasurementPriorProbe(selectedRoi, slot, out VolumeRoiPriorProbe probe))
        {
            ShowToast("Could not build a probe from the selected ROI.", ToastSeverity.Warning);
            return;
        }

        string regionLabel = _reportRegionOverrides.TryGetValue(selectedRoi.Id, out string? regionOverride) && !string.IsNullOrWhiteSpace(regionOverride)
            ? regionOverride.Trim()
            : ResolveMeasurementRegion(selectedRoi, slot).Label;
        string anatomyLabel = _reportAnatomyOverrides.TryGetValue(selectedRoi.Id, out string? anatomyOverride) && !string.IsNullOrWhiteSpace(anatomyOverride)
            ? anatomyOverride.Trim()
            : ResolveMeasurementAnatomy(selectedRoi, slot).Label;

        if (!string.Equals(regionLabel, "Neuro", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("The active Cranium pack currently expects Neuro-region ROIs. Assign the ROI to Neuro first.", ToastSeverity.Warning, TimeSpan.FromSeconds(6));
            return;
        }

        if (string.IsNullOrWhiteSpace(anatomyLabel) || string.Equals(anatomyLabel, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(anatomyLabel, "Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("Assign a concrete anatomy structure before feeding the ROI into the pack.", ToastSeverity.Warning, TimeSpan.FromSeconds(6));
            return;
        }

        AnatomyStructureDefinition? existingStructure = ResolveKnowledgePackStructure(_activeCraniumKnowledgePack, anatomyLabel);
        if (existingStructure is not null)
        {
            _selectedActivePackStructureId = existingStructure.Id;
            RefreshAnatomyProjectionUi(forceVisible: true);
            ShowToast($"Structure {existingStructure.DisplayName} already exists in the active pack. Use Merge instead.", ToastSeverity.Warning, TimeSpan.FromSeconds(6));
            return;
        }

        AnatomyStructureDefinition structure = CreatePackStructureDefinition(anatomyLabel, probe);

        UpdatePackStructureDefinitionFromProbe(structure, anatomyLabel, probe, preserveIdentity: false);

        if (!_activeCraniumKnowledgePack.Structures.Contains(structure))
        {
            _activeCraniumKnowledgePack.Structures.Add(structure);
        }

        AddOrUpdateValidationCase(_activeCraniumKnowledgePack, anatomyLabel);
        _activeCraniumKnowledgePack.Normalize();
        _selectedActivePackStructureId = structure.Id;
        await SaveActiveAnatomyPackAsync();
        RefreshAnatomyProjectionUi(forceVisible: true);
        ShowToast($"ROI merged into active pack structure {structure.DisplayName}. Shared spatial model updated; {probe.Modality} profile synchronized.", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private async Task ApplySelectedRoiToSelectedPackStructureAsync(StudyMeasurement? selectedRoi)
    {
        if (selectedRoi is null)
        {
            ShowToast("Select a 3D ROI first.", ToastSeverity.Warning);
            return;
        }

        if (_activeCraniumKnowledgePack is null)
        {
            ShowToast("No active Cranium pack available.", ToastSeverity.Warning);
            return;
        }

        AnatomyStructureDefinition? selectedStructure = ResolveExistingStructureForAssignedRoi(selectedRoi);
        if (selectedStructure is null)
        {
            ShowToast("The assigned anatomy structure does not yet exist in the active pack. Use Feed first.", ToastSeverity.Warning, TimeSpan.FromSeconds(6));
            return;
        }

        ViewportSlot? slot = FindSlotForMeasurement(selectedRoi) ?? _activeSlot;
        if (!TryBuildMeasurementPriorProbe(selectedRoi, slot, out VolumeRoiPriorProbe probe))
        {
            ShowToast("Could not build a probe from the selected ROI.", ToastSeverity.Warning);
            return;
        }

        UpdatePackStructureDefinitionFromProbe(selectedStructure, selectedStructure.DisplayName, probe, preserveIdentity: true);
        AddOrUpdateValidationCase(_activeCraniumKnowledgePack, selectedStructure.DisplayName);
        _activeCraniumKnowledgePack.Normalize();
        _selectedActivePackStructureId = selectedStructure.Id;
        await SaveActiveAnatomyPackAsync();
        RefreshAnatomyProjectionUi(forceVisible: true);
        ShowToast($"ROI merged into selected structure {selectedStructure.DisplayName}. {probe.Modality} profile updated without duplicating the definition.", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private AnatomyStructureDefinition? ResolveExistingStructureForAssignedRoi(StudyMeasurement? selectedRoi)
    {
        if (_activeCraniumKnowledgePack is null)
        {
            return null;
        }

        string anatomyLabel = GetSelectedRoiAssignedAnatomyLabel(selectedRoi);
        if (string.IsNullOrWhiteSpace(anatomyLabel))
        {
            return null;
        }

        return ResolveKnowledgePackStructure(_activeCraniumKnowledgePack, anatomyLabel);
    }

    private string GetSelectedRoiAssignedAnatomyLabel(StudyMeasurement? selectedRoi)
    {
        if (selectedRoi is null)
        {
            return string.Empty;
        }

        ViewportSlot? slot = FindSlotForMeasurement(selectedRoi) ?? _activeSlot;
        string anatomyLabel = _reportAnatomyOverrides.TryGetValue(selectedRoi.Id, out string? anatomyOverride) && !string.IsNullOrWhiteSpace(anatomyOverride)
            ? anatomyOverride.Trim()
            : ResolveMeasurementAnatomy(selectedRoi, slot).Label;

        if (string.IsNullOrWhiteSpace(anatomyLabel)
            || string.Equals(anatomyLabel, "Auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(anatomyLabel, "Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return anatomyLabel;
    }

    private string GetSelectedRoiAssignedRegionLabel(StudyMeasurement? selectedRoi)
    {
        if (selectedRoi is null)
        {
            return string.Empty;
        }

        ViewportSlot? slot = FindSlotForMeasurement(selectedRoi) ?? _activeSlot;
        string regionLabel = _reportRegionOverrides.TryGetValue(selectedRoi.Id, out string? regionOverride) && !string.IsNullOrWhiteSpace(regionOverride)
            ? regionOverride.Trim()
            : ResolveMeasurementRegion(selectedRoi, slot).Label;

        if (string.IsNullOrWhiteSpace(regionLabel)
            || string.Equals(regionLabel, "Auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(regionLabel, "Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return regionLabel;
    }

    private void ToggleDeveloperAnatomyModelProjection()
    {
        if (_developerAnatomyModelProjectionEnabled)
        {
            _developerAnatomyModelProjectionEnabled = false;
            RefreshMeasurementPanels();
            RefreshAnatomyPanel(forceVisible: true);
            ShowToast("Projected pack-structure overlay disabled.", ToastSeverity.Info);
            return;
        }

        AnatomyStructureDefinition? selectedStructure = GetSelectedActivePackStructure();
        if (selectedStructure is null)
        {
            ShowToast("Select a structure in the active pack first.", ToastSeverity.Warning);
            return;
        }

        int projectedSeriesCount = _slots.Count(slot => slot.Volume is not null && GetActiveDeveloperAnatomyOverlayModels(slot).Count > 0);
        if (projectedSeriesCount == 0)
        {
            ShowToast($"The selected pack structure {selectedStructure.DisplayName} cannot yet be projected into the loaded series. Add or widen its position ranges first.", ToastSeverity.Warning, TimeSpan.FromSeconds(6));
            return;
        }

        _developerAnatomyModelProjectionEnabled = true;
        RefreshMeasurementPanels();
        RefreshAnatomyPanel(forceVisible: true);
        ShowToast($"Projected pack structure {selectedStructure.DisplayName} into {projectedSeriesCount} loaded volume series.", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private IReadOnlyList<AnatomyDeveloperOverlayModel> GetDeveloperAnatomyOverlaysForSlot(ViewportSlot slot) =>
        !_developerAnatomyModelProjectionEnabled || slot.Volume is null
            ? []
            : GetActiveDeveloperAnatomyOverlayModels(slot);

    private IReadOnlyList<AnatomyDeveloperOverlayModel> GetActiveDeveloperAnatomyOverlayModels(ViewportSlot slot)
    {
        if (slot.Volume is null)
        {
            return [];
        }

        AnatomyStructureDefinition? selectedStructure = GetSelectedActivePackStructure();
        if (selectedStructure is null || !TryBuildPackStructureOverlayModel(selectedStructure, slot.Volume, out AnatomyDeveloperOverlayModel overlay))
        {
            return [];
        }

        return [overlay];
    }

    private string BuildDeveloperAnatomyProjectionStatusText()
    {
        AnatomyStructureDefinition? selectedStructure = GetSelectedActivePackStructure();
        if (!_developerAnatomyModelProjectionEnabled)
        {
            return selectedStructure is null
                ? "Projection mode is off. Select a structure in the active pack to project its current pack definition into any loaded plane/series."
                : $"Projection mode is off. When enabled, the selected pack structure {selectedStructure.DisplayName} is projected into every loaded volume viewport in its current plane.";
        }

        if (selectedStructure is null)
        {
            return "Projection mode is on, but it currently needs a selected structure in the active pack editor.";
        }

        int projectedSeriesCount = _slots.Count(slot => slot.Volume is not null && GetActiveDeveloperAnatomyOverlayModels(slot).Count > 0);
        return projectedSeriesCount == 0
            ? $"Projection mode is on, but the selected pack structure {selectedStructure.DisplayName} does not yet have enough positional definition to project into the loaded series."
            : $"Projection mode is on. Showing the selected pack structure {selectedStructure.DisplayName} in {projectedSeriesCount} loaded volume series and current planes.";
    }

    private bool TryBuildPackStructureOverlayModel(AnatomyStructureDefinition structure, SeriesVolume volume, out AnatomyDeveloperOverlayModel overlay)
    {
        overlay = null!;
        SpatialBounds? bounds = GetSpatialBounds(volume);
        if (bounds is null ||
            !TryResolvePackStructureSemanticEnvelope(structure, out double centerLr, out double centerAp, out double centerCc, out double sizeLr, out double sizeAp, out double sizeCc))
        {
            return false;
        }

        SpatialVector3D patientCenter = ProjectSemanticPackPointToPatient(volume, centerLr, centerAp, centerCc);
        (double patientSizeX, double patientSizeY, double patientSizeZ) = ProjectSemanticPackExtentsToPatientSize(volume, centerLr, centerAp, centerCc, sizeLr, sizeAp, sizeCc);

        double rangeX = Math.Max(bounds.Value.MaxX - bounds.Value.MinX, double.Epsilon);
        double rangeY = Math.Max(bounds.Value.MaxY - bounds.Value.MinY, double.Epsilon);
        double rangeZ = Math.Max(bounds.Value.MaxZ - bounds.Value.MinZ, double.Epsilon);

        overlay = new AnatomyDeveloperOverlayModel(
            structure.DisplayName,
            string.Join(", ", GetEffectiveAllowedCompartments(structure)),
            "PACK",
            "Active pack structure",
            Normalize(patientCenter.X, bounds.Value.MinX, bounds.Value.MaxX),
            Normalize(patientCenter.Y, bounds.Value.MinY, bounds.Value.MaxY),
            Normalize(patientCenter.Z, bounds.Value.MinZ, bounds.Value.MaxZ),
            Math.Clamp(patientSizeX / rangeX, 0.04, 1.0),
            Math.Clamp(patientSizeY / rangeY, 0.04, 1.0),
            Math.Clamp(patientSizeZ / rangeZ, 0.04, 1.0),
            0,
            1,
            DateTime.UtcNow);
        return true;
    }

    private bool TryResolvePackStructureSemanticEnvelope(
        AnatomyStructureDefinition structure,
        out double centerLr,
        out double centerAp,
        out double centerCc,
        out double sizeLr,
        out double sizeAp,
        out double sizeCc)
    {
        centerLr = ResolvePackSignedLeftRightCenter(structure);
        centerAp = ResolvePackSignedAnteriorPosteriorCenter(structure);
        centerCc = ResolvePackCranialCaudalCenter(structure);
        sizeLr = ResolvePackAxisSpan(structure.ExpectedPosition.LeftRight, defaultSpan: structure.ExpectedPosition.DistanceToMidline.Max is not null ? Math.Max(0.10, structure.ExpectedPosition.DistanceToMidline.Max.Value * 0.9) : 0.18);
        sizeAp = ResolvePackAxisSpan(structure.ExpectedPosition.AnteriorPosterior, defaultSpan: HasRelation(structure, "inside", "posterior_fossa") ? 0.22 : 0.20);
        sizeCc = ResolvePackAxisSpan(structure.ExpectedPosition.CranialCaudal, defaultSpan: 0.18);

        bool hasDirectPosition = HasUsableRange(structure.ExpectedPosition.LeftRight)
            || HasUsableRange(structure.ExpectedPosition.AnteriorPosterior)
            || HasUsableRange(structure.ExpectedPosition.CranialCaudal)
            || HasUsableRange(structure.ExpectedPosition.DistanceToMidline)
            || HasUsableRange(structure.ExpectedPosition.DistanceToSkullBase)
            || HasUsableRange(structure.ExpectedPosition.DistanceToVertex)
            || structure.RequiredRelations.Count > 0;
        if (!hasDirectPosition)
        {
            return false;
        }

        centerLr = Math.Clamp(centerLr, -1.0, 1.0);
        centerAp = Math.Clamp(centerAp, -1.0, 1.0);
        centerCc = Math.Clamp(centerCc, 0.0, 1.0);
        sizeLr = Math.Clamp(sizeLr, 0.05, 1.0);
        sizeAp = Math.Clamp(sizeAp, 0.05, 1.0);
        sizeCc = Math.Clamp(sizeCc, 0.05, 1.0);
        return true;
    }

    private static bool HasUsableRange(AnatomyNumericRange range) => range.Min is not null || range.Max is not null;

    private static double ResolvePackSignedLeftRightCenter(AnatomyStructureDefinition structure)
    {
        if (HasUsableRange(structure.ExpectedPosition.LeftRight))
        {
            return ResolveRangeCenter(structure.ExpectedPosition.LeftRight, 0);
        }

        double distance = ResolveRangeCenter(structure.ExpectedPosition.DistanceToMidline,
            HasRelation(structure, "far_from", "midline") ? 0.24 : HasRelation(structure, "near", "midline") ? 0.05 : 0.0);

        if (HasRelation(structure, "left_of", "midline"))
        {
            return -Math.Max(0.04, distance);
        }

        if (HasRelation(structure, "right_of", "midline"))
        {
            return Math.Max(0.04, distance);
        }

        return 0;
    }

    private static double ResolvePackSignedAnteriorPosteriorCenter(AnatomyStructureDefinition structure)
    {
        if (HasUsableRange(structure.ExpectedPosition.AnteriorPosterior))
        {
            return ResolveRangeCenter(structure.ExpectedPosition.AnteriorPosterior, 0);
        }

        if (HasRelation(structure, "inside", "posterior_fossa"))
        {
            return -0.42;
        }

        return 0;
    }

    private static double ResolvePackCranialCaudalCenter(AnatomyStructureDefinition structure)
    {
        if (HasUsableRange(structure.ExpectedPosition.CranialCaudal))
        {
            return ResolveRangeCenter(structure.ExpectedPosition.CranialCaudal, 0.5);
        }

        if (HasUsableRange(structure.ExpectedPosition.DistanceToSkullBase))
        {
            return ResolveRangeCenter(structure.ExpectedPosition.DistanceToSkullBase, 0.5);
        }

        if (HasUsableRange(structure.ExpectedPosition.DistanceToVertex))
        {
            return 1.0 - ResolveRangeCenter(structure.ExpectedPosition.DistanceToVertex, 0.5);
        }

        if (HasRelation(structure, "near", "skull_base_plane"))
        {
            return 0.14;
        }

        if (HasRelation(structure, "cranial_to", "brainstem"))
        {
            return 0.66;
        }

        if (HasRelation(structure, "away_from", "skull_base_plane"))
        {
            return 0.56;
        }

        return 0.5;
    }

    private static double ResolveRangeCenter(AnatomyNumericRange range, double fallback)
    {
        if (range.Min is double min && range.Max is double max)
        {
            return (min + max) * 0.5;
        }

        if (range.Min is double onlyMin)
        {
            return onlyMin;
        }

        if (range.Max is double onlyMax)
        {
            return onlyMax;
        }

        return fallback;
    }

    private static double ResolvePackAxisSpan(AnatomyNumericRange range, double defaultSpan)
    {
        if (range.Min is double min && range.Max is double max)
        {
            return Math.Max(0.05, Math.Abs(max - min));
        }

        return defaultSpan;
    }

    private static bool HasRelation(AnatomyStructureDefinition structure, string type, string target) =>
        GetEffectiveRequiredRelations(structure).Any(relation =>
            string.Equals(relation.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(relation.Target, target, StringComparison.OrdinalIgnoreCase));

    private SpatialVector3D ProjectSemanticPackPointToPatient(SeriesVolume volume, double signedLeftRight, double signedAnteriorPosterior, double cranialCaudal)
    {
        double voxelZ = Math.Clamp(cranialCaudal, 0, 1) * Math.Max(1, volume.SizeZ - 1);
        HeadAxisCorrectionModel model = GetOrCreateHeadAxisCorrectionModel(volume);
        if (model.IsReliable)
        {
            double expectedCenterX = model.CenterlineInterceptX + (model.CenterlineSlopeX * voxelZ);
            double expectedCenterY = model.CenterlineInterceptY + (model.CenterlineSlopeY * voxelZ);
            double lrProjection = signedLeftRight * model.LrHalfExtent * Math.Sign(model.LrSign == 0 ? 1 : model.LrSign);
            double apProjection = signedAnteriorPosterior * model.ApHalfExtent * Math.Sign(model.ApSign == 0 ? 1 : model.ApSign);
            double offsetX = (lrProjection * model.LrAxisX) + (apProjection * model.ApAxisX);
            double offsetY = (lrProjection * model.LrAxisY) + (apProjection * model.ApAxisY);
            return volume.VoxelToPatient(expectedCenterX + offsetX, expectedCenterY + offsetY, voxelZ);
        }

        double voxelX = (1.0 - Math.Clamp(signedLeftRight, -1.0, 1.0)) * 0.5 * Math.Max(1, volume.SizeX - 1);
        double voxelY = (1.0 - Math.Clamp(signedAnteriorPosterior, -1.0, 1.0)) * 0.5 * Math.Max(1, volume.SizeY - 1);
        return volume.VoxelToPatient(voxelX, voxelY, voxelZ);
    }

    private (double SizeX, double SizeY, double SizeZ) ProjectSemanticPackExtentsToPatientSize(SeriesVolume volume, double centerLr, double centerAp, double centerCc, double sizeLr, double sizeAp, double sizeCc)
    {
        SpatialVector3D center = ProjectSemanticPackPointToPatient(volume, centerLr, centerAp, centerCc);
        double halfLr = sizeLr * 0.5;
        double halfAp = sizeAp * 0.5;
        double halfCc = sizeCc * 0.5;

        SpatialVector3D[] samples =
        [
            ProjectSemanticPackPointToPatient(volume, centerLr - halfLr, centerAp, centerCc),
            ProjectSemanticPackPointToPatient(volume, centerLr + halfLr, centerAp, centerCc),
            ProjectSemanticPackPointToPatient(volume, centerLr, centerAp - halfAp, centerCc),
            ProjectSemanticPackPointToPatient(volume, centerLr, centerAp + halfAp, centerCc),
            ProjectSemanticPackPointToPatient(volume, centerLr, centerAp, centerCc - halfCc),
            ProjectSemanticPackPointToPatient(volume, centerLr, centerAp, centerCc + halfCc),
        ];

        double halfX = samples.Max(point => Math.Abs(point.X - center.X));
        double halfY = samples.Max(point => Math.Abs(point.Y - center.Y));
        double halfZ = samples.Max(point => Math.Abs(point.Z - center.Z));
        return (Math.Max(2.0, halfX * 2.0), Math.Max(2.0, halfY * 2.0), Math.Max(2.0, halfZ * 2.0));
    }

    private static AnatomyStructureDefinition CreatePackStructureDefinition(string anatomyLabel, VolumeRoiPriorProbe probe)
    {
        AnatomyStructureDefinition structure = new()
        {
            Id = ToPackStructureId(anatomyLabel),
            DisplayName = anatomyLabel.Trim(),
        };
        UpdatePackStructureDefinitionFromProbe(structure, anatomyLabel, probe, preserveIdentity: false);
        return structure;
    }

    private static void UpdatePackStructureDefinitionFromProbe(AnatomyStructureDefinition structure, string anatomyLabel, VolumeRoiPriorProbe probe, bool preserveIdentity)
    {
        ProbeKnowledgeFeatures features = BuildProbeKnowledgeFeatures(probe);
        string normalizedLabel = anatomyLabel.Trim();
        string lower = normalizedLabel.ToLowerInvariant();

        if (!preserveIdentity)
        {
            structure.Id = string.IsNullOrWhiteSpace(structure.Id) ? ToPackStructureId(normalizedLabel) : structure.Id.Trim();
            structure.DisplayName = normalizedLabel;
        }

        if (!structure.SupportedModalities.Contains(probe.Modality, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(probe.Modality))
        {
            structure.SupportedModalities.Add(probe.Modality);
        }

        MergeStringList(structure.AllowedCompartments, InferAllowedCompartments(lower));
        MergeExpectedMaterial(structure.ExpectedMaterial, InferExpectedMaterial(lower, features.LikelyCsf, probe.Modality), probe.Modality);
        MergeRelations(structure.RequiredRelations, InferRequiredRelations(lower));
        MergeRelations(structure.ForbiddenRelations, InferForbiddenRelations(lower));

        double lrMargin = Math.Max(0.06, probe.NormalizedSizeX * 0.75);
        double apMargin = Math.Max(0.06, probe.NormalizedSizeY * 0.75);
        double ccMargin = Math.Max(0.06, probe.NormalizedSizeZ * 0.75);
        double midlineMargin = Math.Max(0.04, Math.Max(probe.NormalizedSizeX, probe.NormalizedSizeY) * 0.55);

        MergeRange(structure.ExpectedPosition.LeftRight, features.SignedLeftRight, lrMargin);
        MergeRange(structure.ExpectedPosition.AnteriorPosterior, features.SignedAnteriorPosterior, apMargin);
        MergeRange(structure.ExpectedPosition.CranialCaudal, features.CranialCaudal, ccMargin);
        MergeRange(structure.ExpectedPosition.DistanceToMidline, features.DistanceToMidline, midlineMargin);
        MergeRange(structure.ExpectedPosition.DistanceToSkullBase, features.DistanceToSkullBase, ccMargin);
        MergeRange(structure.ExpectedPosition.DistanceToVertex, features.DistanceToVertex, ccMargin);
        structure.Normalize();
    }

    private static List<string> InferAllowedCompartments(string lowerLabel)
    {
        if (lowerLabel.Contains("ventricle"))
        {
            return ["ventricular_csf", "intracranial_space"];
        }

        if (lowerLabel.Contains("pons") || lowerLabel.Contains("brainstem") || lowerLabel.Contains("cerebell") || lowerLabel.Contains("vermis"))
        {
            return ["posterior_fossa", "intracranial_space"];
        }

        return ["intracranial_space"];
    }

    private static AnatomyExpectedMaterial InferExpectedMaterial(string lowerLabel, bool likelyCsf, string modality)
    {
        AnatomyExpectedMaterial material = new();
        if (string.Equals(modality, "CT", StringComparison.OrdinalIgnoreCase))
        {
            if (lowerLabel.Contains("ventricle") || likelyCsf)
            {
                material.CtClass = ["CSF"];
            }
            else
            {
                material.CtClass = ["BrainParenchyma"];
            }
        }
        else if (string.Equals(modality, "MR", StringComparison.OrdinalIgnoreCase))
        {
            if (lowerLabel.Contains("ventricle") || likelyCsf)
            {
                material.MrT1 = ["low"];
                material.MrT2 = ["high"];
                material.MrFlair = ["suppressed_or_low"];
            }
            else
            {
                material.MrT1 = ["intermediate"];
                material.MrT2 = ["intermediate"];
                material.MrFlair = ["intermediate"];
            }
        }

        return material;
    }

    private static List<AnatomyRelationRule> InferRequiredRelations(string lowerLabel)
    {
        List<AnatomyRelationRule> relations = [];
        if (lowerLabel.Contains("left") && lowerLabel.Contains("ventricle"))
        {
            relations.Add(new AnatomyRelationRule { Type = "left_of", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "cranial_to", Target = "brainstem", Strength = "hard" });
        }
        else if (lowerLabel.Contains("right") && lowerLabel.Contains("ventricle"))
        {
            relations.Add(new AnatomyRelationRule { Type = "right_of", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "cranial_to", Target = "brainstem", Strength = "hard" });
        }
        else if (lowerLabel.Contains("pons") || lowerLabel.Contains("brainstem"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "near", Target = "skull_base_plane", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "near", Target = "midline", Strength = "hard" });
        }
        else if (lowerLabel.Contains("vermis"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "near", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "away_from", Target = "skull_base_plane", Strength = "soft" });
        }
        else if (lowerLabel.Contains("thalam") || lowerLabel.Contains("caudate"))
        {
            relations.Add(new AnatomyRelationRule { Type = "cranial_to", Target = "brainstem", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "away_from", Target = "skull_base_plane", Strength = "soft" });

            if (lowerLabel.Contains("left"))
            {
                relations.Add(new AnatomyRelationRule { Type = "left_of", Target = "midline", Strength = "hard" });
            }
            else if (lowerLabel.Contains("right"))
            {
                relations.Add(new AnatomyRelationRule { Type = "right_of", Target = "midline", Strength = "hard" });
            }
        }
        else if (lowerLabel.Contains("lenticular") || lowerLabel.Contains("putamen") || lowerLabel.Contains("pallidus"))
        {
            relations.Add(new AnatomyRelationRule { Type = "far_from", Target = "midline", Strength = "hard" });

            if (lowerLabel.Contains("left"))
            {
                relations.Add(new AnatomyRelationRule { Type = "left_of", Target = "midline", Strength = "hard" });
            }
            else if (lowerLabel.Contains("right"))
            {
                relations.Add(new AnatomyRelationRule { Type = "right_of", Target = "midline", Strength = "hard" });
            }
        }
        else if (lowerLabel.Contains("left") && lowerLabel.Contains("cerebell"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "left_of", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "far_from", Target = "midline", Strength = "hard" });
        }
        else if (lowerLabel.Contains("right") && lowerLabel.Contains("cerebell"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "right_of", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "far_from", Target = "midline", Strength = "hard" });
        }
        else if (lowerLabel.Contains("cerebell"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
        }

        string? lateralityRelation = InferLateralityRelationType(lowerLabel);
        if (!string.IsNullOrWhiteSpace(lateralityRelation))
        {
            relations.Add(new AnatomyRelationRule { Type = lateralityRelation, Target = "midline", Strength = "hard" });
        }

        return relations;
    }

    private static List<AnatomyRelationRule> InferForbiddenRelations(string lowerLabel)
    {
        List<AnatomyRelationRule> relations = [];
        if (lowerLabel.Contains("ventricle"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "touches", Target = "skull_base_plane", Strength = "hard" });
        }
        else if (lowerLabel.Contains("pons") || lowerLabel.Contains("brainstem"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "ventricular_csf", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "far_from", Target = "midline", Strength = "hard" });
        }
        else if (lowerLabel.Contains("vermis"))
        {
            relations.Add(new AnatomyRelationRule { Type = "left_of", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "right_of", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "near", Target = "skull_base_plane", Strength = "soft" });
        }
        else if (lowerLabel.Contains("thalam") || lowerLabel.Contains("caudate"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "touches", Target = "skull_base_plane", Strength = "hard" });

            if (lowerLabel.Contains("left") || lowerLabel.Contains("right"))
            {
                relations.Add(new AnatomyRelationRule { Type = "near", Target = "midline", Strength = "soft" });
            }
        }
        else if (lowerLabel.Contains("lenticular") || lowerLabel.Contains("putamen") || lowerLabel.Contains("pallidus"))
        {
            relations.Add(new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "touches", Target = "skull_base_plane", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "near", Target = "midline", Strength = "hard" });
        }
        else if ((lowerLabel.Contains("left") || lowerLabel.Contains("right")) && lowerLabel.Contains("cerebell"))
        {
            relations.Add(new AnatomyRelationRule { Type = "near", Target = "midline", Strength = "hard" });
            relations.Add(new AnatomyRelationRule { Type = "near", Target = "skull_base_plane", Strength = "soft" });
        }

        return relations;
    }

    private static string? InferLateralityRelationType(string label)
    {
        string[] tokens = label.Split([' ', '\t', '\r', '\n', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', ',', ';', '.', ':'], StringSplitOptions.RemoveEmptyEntries);
        bool hasLeft = tokens.Contains("left", StringComparer.OrdinalIgnoreCase);
        bool hasRight = tokens.Contains("right", StringComparer.OrdinalIgnoreCase);
        bool hasBilateral = tokens.Contains("bilateral", StringComparer.OrdinalIgnoreCase) || tokens.Contains("both", StringComparer.OrdinalIgnoreCase);
        if (hasBilateral || hasLeft == hasRight)
        {
            return null;
        }

        return hasLeft ? "left_of" : "right_of";
    }

    private static void MergeRange(AnatomyNumericRange range, double center, double margin)
    {
        double nextMin = Math.Max(-1.0, center - margin);
        double nextMax = Math.Min(1.0, center + margin);

        range.Min = range.Min is null ? nextMin : Math.Min(range.Min.Value, nextMin);
        range.Max = range.Max is null ? nextMax : Math.Max(range.Max.Value, nextMax);
    }

    private static void MergeExpectedMaterial(AnatomyExpectedMaterial target, AnatomyExpectedMaterial source, string modality)
    {
        target ??= new AnatomyExpectedMaterial();
        source ??= new AnatomyExpectedMaterial();

        if (string.Equals(modality, "CT", StringComparison.OrdinalIgnoreCase))
        {
            target.CtClass = MergeStringValues(target.CtClass, source.CtClass);
            return;
        }

        if (string.Equals(modality, "MR", StringComparison.OrdinalIgnoreCase) || string.Equals(modality, "MRI", StringComparison.OrdinalIgnoreCase))
        {
            target.MrT1 = MergeStringValues(target.MrT1, source.MrT1);
            target.MrT2 = MergeStringValues(target.MrT2, source.MrT2);
            target.MrFlair = MergeStringValues(target.MrFlair, source.MrFlair);
        }
    }

    private static void MergeRelations(List<AnatomyRelationRule> target, IEnumerable<AnatomyRelationRule> source)
    {
        foreach (AnatomyRelationRule relation in source)
        {
            relation.Normalize();
            bool exists = target.Any(existing =>
                string.Equals(existing.Type, relation.Type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Target, relation.Target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Strength, relation.Strength, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                target.Add(new AnatomyRelationRule
                {
                    Type = relation.Type,
                    Target = relation.Target,
                    Strength = relation.Strength,
                });
            }
        }
    }

    private static void MergeStringList(List<string> target, IEnumerable<string> source)
    {
        List<string> merged = MergeStringValues(target, source);
        target.Clear();
        target.AddRange(merged);
    }

    private static List<string> MergeStringValues(IEnumerable<string>? left, IEnumerable<string>? right)
    {
        return (left ?? [])
            .Concat(right ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ToPackStructureId(string anatomyLabel)
    {
        string normalized = string.Join('_', anatomyLabel
            .Trim()
            .ToLowerInvariant()
            .Split([' ', '/', '\\', '-', ',', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(normalized) ? $"structure_{Guid.NewGuid():N}" : normalized;
    }

    private static void AddOrUpdateValidationCase(AnatomyKnowledgePack pack, string anatomyLabel)
    {
        string lower = anatomyLabel.Trim().ToLowerInvariant();
        if (lower.Contains("left") && lower.Contains("ventricle"))
        {
            AnatomyValidationCase? existing = pack.ValidationCases.FirstOrDefault(candidate => string.Equals(candidate.Id, "lv-vs-brainstem", StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                pack.ValidationCases.Add(new AnatomyValidationCase
                {
                    Id = "lv-vs-brainstem",
                    Description = "Left lateral ventricle must not be classified as brainstem.",
                    ExpectedStructureId = ToPackStructureId(anatomyLabel),
                    ForbiddenStructureIds = ["brainstem"],
                });
            }
            else
            {
                existing.ExpectedStructureId = ToPackStructureId(anatomyLabel);
                if (!existing.ForbiddenStructureIds.Contains("brainstem", StringComparer.OrdinalIgnoreCase))
                {
                    existing.ForbiddenStructureIds.Add("brainstem");
                }
                existing.Normalize();
            }
        }
    }

    private static string JoinList(IEnumerable<string> values) => string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static List<string> ParseList(string? value)
    {
        return (value ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatRange(AnatomyNumericRange range)
    {
        if (range.Min is null && range.Max is null)
        {
            return string.Empty;
        }

        string min = range.Min?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        string max = range.Max?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        return $"{min}:{max}";
    }

    private static void ApplyRangeText(AnatomyNumericRange range, string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            range.Min = null;
            range.Max = null;
            return;
        }

        string[] parts = text.Split(':', StringSplitOptions.TrimEntries);
        range.Min = TryParseNullableDouble(parts.ElementAtOrDefault(0));
        range.Max = parts.Length > 1 ? TryParseNullableDouble(parts[1]) : range.Min;
    }

    private static double? TryParseNullableDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static string FormatRelations(IEnumerable<AnatomyRelationRule> relations)
    {
        return string.Join(Environment.NewLine, relations.Select(relation => $"{relation.Type}:{relation.Target}:{relation.Strength}"));
    }

    private static List<AnatomyRelationRule> ParseRelations(string? value)
    {
        List<AnatomyRelationRule> relations = [];
        IEnumerable<string> tokens = (value ?? string.Empty)
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            string[] parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            relations.Add(new AnatomyRelationRule
            {
                Type = parts[0],
                Target = parts[1],
                Strength = parts.Length > 2 ? parts[2] : "soft",
            });
        }

        return relations;
    }

    private void OnAnatomyPanelPinClick(object? sender, RoutedEventArgs e)
    {
        _anatomyPanelPinned = AnatomyPanelPinButton.IsChecked == true;
        if (_anatomyPanelPinned)
        {
            _anatomyPanelVisible = true;
        }

        SaveViewerSettings();
        RefreshAnatomyPanel(forceVisible: _anatomyPanelPinned);
        e.Handled = true;
    }

    private void OnAnatomyPanelHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!AnatomyPanel.IsVisible || !e.GetCurrentPoint(AnatomyPanelDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _anatomyPanelDragPointer = e.Pointer;
        _anatomyPanelDragPointer.Capture(AnatomyPanelDragHandle);
        _anatomyPanelDragStart = e.GetPosition(ViewerContentHost);
        _anatomyPanelDragStartOffset = _anatomyPanelOffset;
        e.Handled = true;
    }

    private void OnAnatomyPanelHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_anatomyPanelDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _anatomyPanelDragStart;
        _anatomyPanelOffset = new Point(
            _anatomyPanelDragStartOffset.X + delta.X,
            _anatomyPanelDragStartOffset.Y + delta.Y);
        ApplyAnatomyPanelOffset();
        e.Handled = true;
    }

    private void OnAnatomyPanelHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_anatomyPanelDragPointer, e.Pointer))
        {
            return;
        }

        _anatomyPanelDragPointer.Capture(null);
        _anatomyPanelDragPointer = null;
        ApplyAnatomyPanelOffset();
        SaveViewerSettings();
        e.Handled = true;
    }

    private void ApplyAnatomyPanelOffset()
    {
        if (AnatomyPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureAnatomyPanelTransform();
        double panelWidth = AnatomyPanel.Bounds.Width;
        double panelHeight = AnatomyPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = AnatomyPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _anatomyPanelOffset.X;
            transform.Y = _anatomyPanelOffset.Y;
            return;
        }

        double defaultLeft = Math.Max(0, hostWidth - panelWidth - margin.Right);
        double defaultTop = margin.Top;
        double defaultBottom = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double overflowX = GetFloatingPanelOverflowAllowance(panelWidth);
        double overflowY = GetFloatingPanelOverflowAllowance(panelHeight);
        double clampedX = Math.Clamp(_anatomyPanelOffset.X, -defaultLeft - overflowX, overflowX);
        double clampedY = Math.Clamp(_anatomyPanelOffset.Y, -defaultTop - overflowY, defaultBottom + overflowY);
        _anatomyPanelOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureAnatomyPanelTransform()
    {
        if (AnatomyPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        AnatomyPanel.RenderTransform = transform;
        return transform;
    }
}
