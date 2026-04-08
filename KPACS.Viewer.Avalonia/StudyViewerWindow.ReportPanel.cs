using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly Dictionary<string, HeadAxisCorrectionModel> _headAxisCorrectionCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] s_neuroAnatomyOptions =
    [
        "Auto",
        "Cerebrum",
        "Frontal lobe",
        "Parietal lobe",
        "Temporal lobe",
        "Occipital lobe",
        "Cerebellum",
        "Brainstem",
        "Pituitary region",
        "Ventricular system",
        "Skull base",
        "Meninges",
        "Unassigned",
    ];

    private static readonly string[] s_thoraxAnatomyOptions =
    [
        "Auto",
        "Right lung upper region",
        "Right lung mid region",
        "Right lung lower region",
        "Left lung upper region",
        "Left lung mid region",
        "Left lung lower region",
        "Right hilum",
        "Left hilum",
        "Mediastinum",
        "Pleura",
        "Chest wall",
        "Heart",
        "Pericardium",
        "Aorta",
        "Trachea",
        "Esophagus",
        "Diaphragm",
        "Thorax",
        "Unassigned",
    ];

    private static readonly string[] s_abdomenAnatomyOptions =
    [
        "Auto",
        "Liver",
        "Gallbladder",
        "Spleen",
        "Pancreas",
        "Stomach",
        "Duodenum",
        "Small bowel",
        "Colon",
        "Appendix",
        "Mesenterium",
        "Retroperitoneum",
        "Right kidney",
        "Left kidney",
        "Right adrenal gland",
        "Left adrenal gland",
        "Abdominal wall",
        "Abdomen",
        "Unassigned",
    ];

    private static readonly string[] s_pelvisAnatomyOptions =
    [
        "Auto",
        "Urinary bladder",
        "Prostate",
        "Uterus",
        "Right ovary / adnexa",
        "Left ovary / adnexa",
        "Rectum",
        "Sigmoid colon",
        "Pelvic sidewall",
        "Sacrum",
        "Pelvis",
        "Unassigned",
    ];

    private static readonly string[] s_shoulderAnatomyOptions =
    [
        "Auto",
        "Shoulder joint",
        "Rotator cuff",
        "Supraspinatus tendon",
        "Infraspinatus tendon",
        "Subscapularis tendon",
        "Teres minor",
        "Biceps tendon",
        "Deltoid muscle",
        "Acromion",
        "Clavicle",
        "Scapula",
        "Glenoid",
        "Humeral head",
        "Proximal humerus",
        "Shoulder musculature",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_kneeAnatomyOptions =
    [
        "Auto",
        "Knee joint",
        "Distal femur",
        "Patella",
        "Proximal tibia",
        "Proximal fibula",
        "Medial meniscus",
        "Lateral meniscus",
        "Anterior cruciate ligament",
        "Posterior cruciate ligament",
        "Medial collateral ligament",
        "Lateral collateral ligament",
        "Quadriceps tendon",
        "Patellar tendon",
        "Popliteal fossa",
        "Knee musculature",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_ankleAnatomyOptions =
    [
        "Auto",
        "Ankle joint",
        "Distal tibia",
        "Distal fibula",
        "Talus",
        "Calcaneus",
        "Navicular",
        "Cuboid",
        "Cuneiforms",
        "Tibiotalar joint",
        "Subtalar joint",
        "Achilles tendon",
        "Peroneal tendons",
        "Posterior tibial tendon",
        "Deltoid ligament",
        "Lateral ankle ligaments",
        "Plantar fascia",
        "Ankle musculature",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_musculoskeletalGenericAnatomyOptions =
    [
        "Auto",
        "Bone",
        "Joint",
        "Muscle",
        "Tendon",
        "Ligament",
        "Cartilage",
        "Marrow",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_reportReviewStateOptions =
    [
        "Auto",
        "Confirmed",
        "Needs review",
    ];

    private static readonly string[] s_reportRegionOptions =
    [
        "Unassigned",
        "Neuro",
        "Upper thorax",
        "Lower thorax",
        "Upper abdomen",
        "Lower abdomen",
        "Pelvis",
        "Shoulder",
        "Knee",
        "Ankle",
    ];

    private Point _reportPanelOffset;
    private bool _reportPanelPinned;
    private bool _reportPanelVisible;
    private readonly Dictionary<Guid, string> _reportRegionOverrides = [];
    private readonly Dictionary<Guid, string> _reportAnatomyOverrides = [];
    private readonly Dictionary<Guid, string> _reportReviewStates = [];
    private readonly List<VolumeRoiAnatomyPriorRecord> _volumeRoiAnatomyPriors = [];
    private bool _volumeRoiAnatomyPriorsLoaded;
    private IPointer? _reportPanelDragPointer;
    private Point _reportPanelDragStart;
    private Point _reportPanelDragStartOffset;

    private static readonly IBrush s_reportComboForeground = new SolidColorBrush(Color.Parse("#FFF2F6FA"));
    private static readonly IBrush s_reportComboBackground = new SolidColorBrush(Color.Parse("#FF20262D"));
    private static readonly IBrush s_reportComboBorder = new SolidColorBrush(Color.Parse("#FF5D768E"));

    private void RefreshReportPanel(bool forceVisible = false)
    {
        bool requestedVisible = forceVisible || _reportPanelVisible || _reportPanelPinned;
        StudyViewerWindow reportOwner = GetSharedReportOwnerWindow();
        if (!ReferenceEquals(reportOwner, this))
        {
            _reportPanelVisible = false;
            _reportPanelPinned = false;
            HideReportPanel();
            if (requestedVisible || reportOwner._reportPanelVisible || reportOwner._reportPanelPinned)
            {
                reportOwner.ShowSharedReportPanelFromPeer(forceVisible: requestedVisible || reportOwner._reportPanelPinned);
            }
            return;
        }

        if (forceVisible)
        {
            _reportPanelVisible = true;
        }

        if (!_reportPanelVisible || ReportPanel is null)
        {
            HideReportPanel();
            return;
        }

        List<ReportEntry> entries = BuildReportEntries();
        ReportPanelPinButton.IsChecked = _reportPanelPinned;
        ReportPanelSummaryText.Text = BuildReportSummary(entries);
        ReportPanelHintText.Text = entries.Count == 0
            ? "Create measurements, annotations, 3D ROIs, or RECIST suggestions to populate the report. Anatomy starts as a heuristic guess and can be reviewed here."
            : "Shared report in Viewer 1. Entries can come from the report study or another open study of the same patient."
                + (_reportPanelPinned ? " Panel is pinned." : string.Empty);

        PopulateReportItems(entries);
        ReportPanel.IsVisible = true;
        ApplyReportPanelOffset();
    }

    private void HideReportPanel()
    {
        if (ReportPanel is null)
        {
            return;
        }

        ReportPanel.IsVisible = false;
        ReportItemsPanel?.Children.Clear();
        if (ReportPanelSummaryText is not null)
        {
            ReportPanelSummaryText.Text = string.Empty;
        }

        if (ReportPanelHintText is not null)
        {
            ReportPanelHintText.Text = string.Empty;
        }
    }

    private void ShowSharedReportPanelFromPeer(bool forceVisible)
    {
        _reportPanelVisible = true;
        RefreshReportPanel(forceVisible || _reportPanelPinned);
        Activate();
    }

    private List<ReportEntry> BuildReportEntries()
    {
        List<ReportMeasurementContext> measurementContexts = BuildReportMeasurementContexts();
        var entries = new List<ReportEntry>(measurementContexts.Count * 2);

        foreach (ReportMeasurementContext context in measurementContexts)
        {
            StudyViewerWindow sourceWindow = context.SourceWindow;
            StudyMeasurement measurement = context.Measurement;
            ViewportSlot? slot = context.Slot;
            bool hasRadiomicsEntry = TryBuildRadiomicsReportEntry(sourceWindow, measurement, slot, out ReportEntry radiomicsEntry);
            bool suppressBaseMeasurementEntry = hasRadiomicsEntry && IsInsightRelevantMeasurement(measurement);

            if (!suppressBaseMeasurementEntry)
            {
                entries.Add(BuildMeasurementReportEntry(sourceWindow, measurement, slot));
            }

            if (TryBuildRecistReportEntry(sourceWindow, measurement, slot, out ReportEntry recistEntry))
            {
                entries.Add(recistEntry);
            }

            if (hasRadiomicsEntry)
            {
                entries.Add(radiomicsEntry);
            }
        }

        entries.AddRange(BuildVascularPlanningReportEntries());
        entries.AddRange(BuildValidationReportEntries());

        return entries
            .OrderByDescending(entry => entry.IsSelected)
            .ThenBy(entry => entry.IsFromReportStudy ? 0 : 1)
            .ThenBy(entry => entry.SortOrder)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ReportMeasurementContext> BuildReportMeasurementContexts()
    {
        IEnumerable<StudyViewerWindow> sourceWindows = ReferenceEquals(GetSharedReportOwnerWindow(), this)
            ? GetSharedReportWindows()
            : [this];

        return sourceWindows
            .SelectMany(window => window._studyMeasurements.Select(measurement => new ReportMeasurementContext(window, measurement, window.FindSlotForMeasurement(measurement))))
            .ToList();
    }

    private List<ReportEntry> BuildValidationReportEntries()
    {
        IEnumerable<StudyViewerWindow> sourceWindows = ReferenceEquals(GetSharedReportOwnerWindow(), this)
            ? GetSharedReportWindows()
            : [this];

        return sourceWindows
            .Select(window => new ReportEntry(
                window,
                MeasurementId: null,
                Category: "Validation",
                Title: "Phase 1 validation and guardrails",
                Origin: $"{ResolveReportStudyLabel(window, null)} · Viewer {window._viewerNumber} · release hardening",
                Region: "Validation",
                AutoRegion: "Validation",
                IsRegionManual: false,
                Anatomy: "Vascular planning QA",
                Details: window.BuildVascularValidationSummary(),
                Hint: window.BuildVascularValidationHint(),
                IsSelected: false,
                AccentHex: "#FFF1D57A",
                IsRegionLearned: false,
                RegionConfidence: 0,
                IsAnatomyLearned: false,
                AnatomyConfidence: 0,
                IsFromReportStudy: window._viewerNumber == 1,
                IsRegionEditable: false,
                IsAnatomyManual: false,
                IsAnatomyEditable: false,
                AnatomyOptions: [],
                ReviewState: "Auto",
                SortOrder: 4))
            .ToList();
    }

    private List<ReportEntry> BuildVascularPlanningReportEntries()
    {
        IEnumerable<StudyViewerWindow> sourceWindows = ReferenceEquals(GetSharedReportOwnerWindow(), this)
            ? GetSharedReportWindows()
            : [this];

        List<ReportEntry> entries = [];
        foreach (StudyViewerWindow window in sourceWindows)
        {
            foreach ((Guid seedSetId, VascularPlanningBundle bundle) in window._vascularPlanningBundles.OrderBy(entry => entry.Value.CreatedUtc))
            {
                if (!window._centerlineSeedSets.TryGetValue(seedSetId, out CenterlineSeedSet? seedSet))
                {
                    continue;
                }

                if (bundle.Markers.Count == 0 && string.IsNullOrWhiteSpace(bundle.Metrics?.Summary))
                {
                    continue;
                }

                string details = BuildVascularPlanningDetails(bundle);
                string markerHint = BuildVascularPlanningHint(bundle);
                entries.Add(new ReportEntry(
                    window,
                    MeasurementId: null,
                    Category: "EVAR",
                    Title: $"EVAR planning · {seedSet.Label}",
                    Origin: $"{ResolveReportStudyLabel(window, null)} · Viewer {window._viewerNumber} · centerline planning",
                    Region: "Lower abdomen / pelvis",
                    AutoRegion: "Lower abdomen / pelvis",
                    IsRegionManual: false,
                    Anatomy: "Aorta / iliac landing zones",
                    Details: details,
                    Hint: markerHint,
                    IsSelected: window._selectedCenterlineSeedSetId == seedSetId,
                    AccentHex: "#FF73C7FF",
                    IsRegionLearned: false,
                    RegionConfidence: 0,
                    IsAnatomyLearned: false,
                    AnatomyConfidence: 0,
                    IsFromReportStudy: window._viewerNumber == 1,
                    IsRegionEditable: false,
                    IsAnatomyManual: false,
                    IsAnatomyEditable: false,
                    AnatomyOptions: [],
                    ReviewState: "Auto",
                    SortOrder: 3));
            }
        }

        return entries;
    }

    private static string BuildVascularPlanningDetails(VascularPlanningBundle bundle)
    {
        if (bundle.Metrics is not VascularPlanningMetrics metrics)
        {
            return "Planning markers set; metrics will appear after centerline and mask refresh.";
        }

        List<string> parts = [];
        if (metrics.ProximalNeck?.LengthMm is double neckLength)
        {
            string diameterText = metrics.ProximalNeck.MeanEquivalentDiameterMm is double neckDiameter
                ? $" · mean Øeq {neckDiameter:0.0} mm"
                : string.Empty;
            parts.Add($"Proximal neck {neckLength:0.0} mm{diameterText}");
        }

        if (metrics.NeckAngulationDegrees is double angulation)
        {
            parts.Add($"Neck angulation {angulation:0.0}°");
        }

        if (metrics.DistalLanding?.LengthMm is double distalLength)
        {
            string distalDiameterText = metrics.DistalLanding.MeanEquivalentDiameterMm is double distalDiameter
                ? $" · mean Øeq {distalDiameter:0.0} mm"
                : string.Empty;
            parts.Add($"Distal landing {distalLength:0.0} mm{distalDiameterText}");
        }

        return parts.Count == 0 ? metrics.Summary : string.Join(" · ", parts);
    }

    private static string BuildVascularPlanningHint(VascularPlanningBundle bundle)
    {
        string FormatMarker(VascularPlanningMarkerKind kind, string label)
        {
            VascularPlanningMarker? marker = bundle.GetMarker(kind);
            return marker is null ? $"{label}: pending" : $"{label}: {marker.ArcLengthMm:0.0} mm";
        }

        return string.Join(" · ",
            FormatMarker(VascularPlanningMarkerKind.ProximalNeckStart, "Neck start"),
            FormatMarker(VascularPlanningMarkerKind.ProximalNeckEnd, "Neck end"),
            FormatMarker(VascularPlanningMarkerKind.DistalLandingStart, "Distal start"),
            FormatMarker(VascularPlanningMarkerKind.DistalLandingEnd, "Distal end"));
    }

    private ReportEntry BuildMeasurementReportEntry(StudyViewerWindow sourceWindow, StudyMeasurement measurement, ViewportSlot? slot)
    {
        RegionGuess region = sourceWindow.ResolveMeasurementRegion(measurement, slot);
        AnatomyGuess anatomy = sourceWindow.ResolveMeasurementAnatomy(measurement, slot, region);
        string typeLabel = GetMeasurementTypeLabel(measurement);
        string title = measurement.Kind == MeasurementKind.VolumeRoi
            ? sourceWindow.GetMeasurementDisplayTitle(measurement) ?? typeLabel
            : typeLabel;
        if (measurement.Kind == MeasurementKind.Annotation && !string.IsNullOrWhiteSpace(measurement.AnnotationText))
        {
            title = measurement.AnnotationText.Trim();
        }
        else if (measurement.Kind != MeasurementKind.VolumeRoi && !string.IsNullOrWhiteSpace(measurement.Tracking?.Label))
        {
            title = $"{typeLabel} · {measurement.Tracking.Label.Trim()}";
        }

        string details = sourceWindow.BuildMeasurementDetails(measurement);
        return new ReportEntry(
            sourceWindow,
            measurement.Id,
            typeLabel,
            title,
            BuildMeasurementOriginLabel(sourceWindow, measurement, slot),
            region.Label,
            region.AutoLabel,
            region.IsManual,
            anatomy.DisplayLabel,
            details,
            CombineReportHints(region.Hint, anatomy.Hint),
            measurement.Id == sourceWindow._selectedMeasurementId,
            sourceWindow.GetMeasurementAccentHex(measurement),
            region.IsLearned,
            region.Confidence,
            anatomy.IsLearned,
            anatomy.Confidence,
            IsFromReportStudy: sourceWindow._viewerNumber == 1,
            IsRegionEditable: false,
            anatomy.IsManual,
            IsAnatomyEditable: false,
            AnatomyOptions: sourceWindow.BuildAnatomySelectionOptions(measurement, slot),
            ReviewState: sourceWindow.GetReviewStateSelectionValue(measurement.Id),
            SortOrder: 0);
    }

    private bool TryBuildRecistReportEntry(StudyViewerWindow sourceWindow, StudyMeasurement measurement, ViewportSlot? slot, out ReportEntry entry)
    {
        entry = default!;
        MeasurementTrackingMetadata? tracking = measurement.Tracking;
        if (tracking is null)
        {
            return false;
        }

        RegionGuess region = sourceWindow.ResolveMeasurementRegion(measurement, slot);
        AnatomyGuess anatomy = sourceWindow.ResolveMeasurementAnatomy(measurement, slot, region);
        string confidence = tracking.Confidence > 0
            ? $"{tracking.Confidence:P0}"
            : "n/a";
        string review = tracking.ReviewState == MeasurementReviewState.None
            ? string.Empty
            : $" · {tracking.ReviewState}";
        string timepoint = string.IsNullOrWhiteSpace(tracking.TimepointLabel)
            ? string.Empty
            : $" · {tracking.TimepointLabel.Trim()}";
        string detailText = string.IsNullOrWhiteSpace(tracking.ConfidenceSummary)
            ? $"Confidence {confidence}{review}{timepoint}"
            : $"{tracking.ConfidenceSummary.Trim()} · {confidence}{review}{timepoint}";

        entry = new ReportEntry(
            sourceWindow,
            measurement.Id,
            "RECIST",
            string.IsNullOrWhiteSpace(tracking.Label) ? "RECIST follow-up" : $"RECIST · {tracking.Label.Trim()}",
            BuildTrackingOriginLabel(sourceWindow, measurement, slot),
            region.Label,
            region.AutoLabel,
            region.IsManual,
            anatomy.DisplayLabel,
            detailText,
            CombineReportHints(region.Hint, anatomy.Hint),
            measurement.Id == sourceWindow._selectedMeasurementId,
            "#FF89CFF0",
            region.IsLearned,
            region.Confidence,
            anatomy.IsLearned,
            anatomy.Confidence,
            IsFromReportStudy: sourceWindow._viewerNumber == 1,
            IsRegionEditable: false,
            anatomy.IsManual,
            IsAnatomyEditable: false,
            AnatomyOptions: [],
            ReviewState: sourceWindow.GetReviewStateSelectionValue(measurement.Id),
            SortOrder: 1);
        return true;
    }

    private bool TryBuildRadiomicsReportEntry(StudyViewerWindow sourceWindow, StudyMeasurement measurement, ViewportSlot? slot, out ReportEntry entry)
    {
        entry = default!;
        if (!IsInsightRelevantMeasurement(measurement))
        {
            return false;
        }

        if (!sourceWindow.TryResolveMeasurementInsightContext(measurement, out ViewportSlot? resolvedSlot, out DicomViewPanel.RoiDistributionDetails distribution))
        {
            return false;
        }

        slot ??= resolvedSlot;
        RegionGuess region = sourceWindow.ResolveMeasurementRegion(measurement, slot);
        AnatomyGuess anatomy = sourceWindow.ResolveMeasurementAnatomy(measurement, slot, region);
        string supplement = sourceWindow.GetMeasurementTextSupplement(measurement, []) ?? string.Empty;
        string supplementLine = supplement
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        string detailText = $"{distribution.QuantityLabel} μ {distribution.Mean:F1} · med {distribution.Median:F1} · σ {distribution.StandardDeviation:F1}";
        if (!string.IsNullOrWhiteSpace(supplementLine))
        {
            detailText = $"{detailText} · {supplementLine}";
        }

        entry = new ReportEntry(
            sourceWindow,
            measurement.Id,
            "Radiomics",
            $"Radiomics · {GetMeasurementTypeLabel(measurement)}",
            BuildMeasurementOriginLabel(sourceWindow, measurement, slot),
            region.Label,
            region.AutoLabel,
            region.IsManual,
            anatomy.DisplayLabel,
            detailText,
            CombineReportHints(region.Hint, anatomy.Hint),
            measurement.Id == sourceWindow._selectedMeasurementId,
            "#FFC9A4FF",
            region.IsLearned,
            region.Confidence,
            anatomy.IsLearned,
            anatomy.Confidence,
            IsFromReportStudy: sourceWindow._viewerNumber == 1,
            IsRegionEditable: false,
            anatomy.IsManual,
            IsAnatomyEditable: false,
            AnatomyOptions: [],
            ReviewState: sourceWindow.GetReviewStateSelectionValue(measurement.Id),
            SortOrder: 2);
        return true;
    }

    private void PopulateReportItems(IReadOnlyList<ReportEntry> entries)
    {
        ReportItemsPanel.Children.Clear();
        if (entries.Count == 0)
        {
            ReportItemsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#181F2630")),
                BorderBrush = new SolidColorBrush(Color.Parse("#334D5F74")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8),
                Child = new TextBlock
                {
                    Text = "No reportable objects yet.",
                    Foreground = new SolidColorBrush(Color.Parse("#FFD3E5F5")),
                    FontSize = 11,
                }
            });
            return;
        }

        foreach (ReportEntry entry in entries)
        {
            ReportItemsPanel.Children.Add(CreateReportEntryCard(entry));
        }
    }

    private Control CreateReportEntryCard(ReportEntry entry)
    {
        if (string.Equals(entry.Category, "Validation", StringComparison.Ordinal))
        {
            return CreateValidationReportEntryCard(entry);
        }

        Color accent = Color.Parse(entry.AccentHex);
        Control jumpButton = CreateJumpButton(entry);
        var content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 3,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = entry.Title,
                                    Foreground = new SolidColorBrush(Color.Parse("#FFF6FBFF")),
                                    FontWeight = FontWeight.SemiBold,
                                    FontSize = 12,
                                    TextWrapping = TextWrapping.Wrap,
                                },
                                CreateBadgeRow(entry),
                            }
                        },
                        jumpButton,
                    }
                },
                new TextBlock
                {
                    Text = entry.Origin,
                    Foreground = new SolidColorBrush(Color.Parse("#FFAEC2D3")),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                },
                CreateRegionControl(entry),
                new TextBlock
                {
                    Text = $"Assigned anatomy · {entry.Anatomy}",
                    Foreground = new SolidColorBrush(accent),
                    FontSize = 10,
                    FontWeight = FontWeight.Medium,
                    TextWrapping = TextWrapping.Wrap,
                },
                CreateAnatomyEditor(entry),
                CreateReviewStateEditor(entry),
                new TextBlock
                {
                    Text = entry.Details,
                    Foreground = new SolidColorBrush(Color.Parse("#FFD3E5F5")),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = !string.IsNullOrWhiteSpace(entry.Details),
                },
                new TextBox
                {
                    Text = entry.Hint,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    Foreground = new SolidColorBrush(Color.Parse("#FF94ABC1")),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = !string.IsNullOrWhiteSpace(entry.Hint),
                }
            }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(entry.IsSelected ? "#243D5368" : "#181F2630")),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(entry.IsSelected ? 1.4 : 1.0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = content,
        };
    }

    private Control CreateBadgeRow(ReportEntry entry)
    {
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6,
            LineSpacing = 4,
        };

        row.Children.Add(CreateBadge(entry.Category, entry.AccentHex, bold: true));
        if (entry.IsSelected)
        {
            row.Children.Add(CreateBadge("Selected", "#FFF1D57A", bold: false));
        }

        if (entry.IsAnatomyManual)
        {
            row.Children.Add(CreateBadge("Manual anatomy", "#FFB8F28F", bold: false));
        }

        if (entry.IsRegionManual)
        {
            row.Children.Add(CreateBadge("Manual region", "#FF88D7FF", bold: false));
        }

        if (entry.IsRegionLearned)
        {
            row.Children.Add(CreateBadge("Suggested region", "#FF73C7FF", bold: false));
        }

        if (entry.IsAnatomyLearned)
        {
            row.Children.Add(CreateBadge("Suggested anatomy", "#FF7FDFA2", bold: false));
        }

        if (!string.Equals(entry.ReviewState, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            row.Children.Add(CreateBadge(entry.ReviewState, GetReviewStateColor(entry.ReviewState), bold: false));
        }

        return row;
    }

    private Control CreateRegionControl(ReportEntry entry)
    {
        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "Assigned region",
                    Foreground = new SolidColorBrush(Color.Parse("#FFB8CADA")),
                    FontSize = 9,
                    FontWeight = FontWeight.Medium,
                },
                new TextBlock
                {
                    Text = entry.Region,
                    Foreground = new SolidColorBrush(Color.Parse("#FF8FC6E8")),
                    FontSize = 10,
                    FontWeight = FontWeight.Medium,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = DescribeReportAssignmentSource(entry.IsRegionManual, entry.IsRegionLearned, entry.RegionConfidence, entry.AutoRegion),
                    Foreground = new SolidColorBrush(Color.Parse("#FF8FA6BA")),
                    FontSize = 9,
                    TextWrapping = TextWrapping.Wrap,
                },
            }
        };
    }

    private ContextMenu CreateRegionContextMenu(Guid measurementId, string autoRegion)
    {
        var items = new List<Control>
        {
            CreateRegionMenuItem($"Auto ({autoRegion})", measurementId, "Auto"),
        };

        foreach (string option in s_reportRegionOptions)
        {
            items.Add(CreateRegionMenuItem(option, measurementId, option));
        }

        return new ContextMenu
        {
            ItemsSource = items,
        };
    }

    private MenuItem CreateRegionMenuItem(string header, Guid measurementId, string value)
    {
        var item = new MenuItem { Header = header, Tag = (measurementId, value) };
        item.Click += OnRegionMenuItemClick;
        return item;
    }

    private void OnRegionMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ValueTuple<Guid, string> payload })
        {
            ApplyManualRegionSelection(payload.Item1, payload.Item2);
        }
    }

    private Control CreateAnatomyEditor(ReportEntry entry)
    {
        return new TextBlock
        {
            Text = DescribeReportAssignmentSource(entry.IsAnatomyManual, entry.IsAnatomyLearned, entry.AnatomyConfidence, null),
            Foreground = new SolidColorBrush(Color.Parse("#FF8FA6BA")),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private Control CreateReviewStateEditor(ReportEntry entry)
    {
        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "Review status",
                    Foreground = new SolidColorBrush(Color.Parse("#FFB8CADA")),
                    FontSize = 9,
                    FontWeight = FontWeight.Medium,
                },
                new TextBlock
                {
                    Text = string.Equals(entry.ReviewState, "Auto", StringComparison.OrdinalIgnoreCase) ? "Not reviewed" : entry.ReviewState,
                    Foreground = new SolidColorBrush(Color.Parse("#FFD3E5F5")),
                    FontSize = 10,
                    FontWeight = FontWeight.Medium,
                    TextWrapping = TextWrapping.Wrap,
                },
            }
        };
    }

    private static Control CreateBadge(string text, string colorHex, bool bold)
    {
        Color color = Color.Parse(colorHex);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 2),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontSize = 9,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Medium,
            }
        };
    }

    private Control CreateJumpButton(ReportEntry entry)
    {
        var button = new Button
        {
            Content = "Show",
            MinWidth = 56,
            Height = 26,
            IsVisible = entry.MeasurementId is not null,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        button.Click += (_, _) =>
        {
            if (entry.MeasurementId is Guid measurementId)
            {
                entry.SourceWindow.FocusReportMeasurement(measurementId);
                entry.SourceWindow.Activate();
            }
        };

        Grid.SetColumn(button, 1);
        return button;
    }

    private void FocusReportMeasurement(Guid measurementId)
    {
        StudyMeasurement? measurement = _studyMeasurements.FirstOrDefault(candidate => candidate.Id == measurementId);
        if (measurement is null)
        {
            return;
        }

        _selectedMeasurementId = measurementId;
        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        if (slot is not null)
        {
            SetActiveSlot(slot, requestPriority: false);
            if (slot.Volume is not null && measurement.TryGetPatientCenter(out _))
            {
                FocusSlotOnMeasurement(slot, measurement);
            }
            else
            {
                LoadSlot(slot, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));
            }
        }

        RefreshMeasurementPanels();
    }

    private string BuildReportSummary(IReadOnlyList<ReportEntry> entries)
    {
        List<ReportMeasurementContext> measurementContexts = BuildReportMeasurementContexts();
        int manualCount = entries.Count(entry => entry.SortOrder == 0);
        int recistCount = entries.Count(entry => entry.Category == "RECIST");
        int radiomicsCount = entries.Count(entry => entry.Category == "Radiomics");
        int annotationCount = measurementContexts.Count(context => context.Measurement.Kind == MeasurementKind.Annotation);
        int roiCount = measurementContexts.Count(context => context.Measurement.Kind is MeasurementKind.RectangleRoi or MeasurementKind.EllipseRoi or MeasurementKind.PolygonRoi or MeasurementKind.VolumeRoi);
        int reviewedCount = entries.Count(entry => entry.SortOrder == 0 && entry.IsAnatomyManual);
        int confirmedCount = entries.Count(entry => entry.SortOrder == 0 && string.Equals(entry.ReviewState, "Confirmed", StringComparison.OrdinalIgnoreCase));
        int needsReviewCount = entries.Count(entry => entry.SortOrder == 0 && string.Equals(entry.ReviewState, "Needs review", StringComparison.OrdinalIgnoreCase));
        int vascularCount = entries.Count(entry => entry.Category == "EVAR");
        int validationCount = entries.Count(entry => entry.Category == "Validation");
        return $"{manualCount} findings · {reviewedCount} reviewed anatomy · {confirmedCount} confirmed · {needsReviewCount} needs review · {annotationCount} annotations · {roiCount} ROIs · {recistCount} RECIST · {radiomicsCount} radiomics · {vascularCount} EVAR · {validationCount} validation";
    }

    private string BuildMeasurementOriginLabel(StudyViewerWindow sourceWindow, StudyMeasurement measurement, ViewportSlot? slot)
    {
        string studyLabel = ResolveReportStudyLabel(sourceWindow, slot?.Series);
        string seriesLabel = slot?.Series is null
            ? "Series unavailable"
            : BuildSeriesLabel(slot.Series);

        string trackingLabel = measurement.Tracking is null || string.IsNullOrWhiteSpace(measurement.Tracking.TimepointLabel)
            ? string.Empty
            : $" · {measurement.Tracking.TimepointLabel.Trim()}";
        return $"{studyLabel} · Viewer {sourceWindow._viewerNumber} · {seriesLabel}{trackingLabel}";
    }

    private string BuildTrackingOriginLabel(StudyViewerWindow sourceWindow, StudyMeasurement measurement, ViewportSlot? slot)
    {
        MeasurementTrackingMetadata tracking = measurement.Tracking!;
        string origin = BuildMeasurementOriginLabel(sourceWindow, measurement, slot);
        if (tracking.SourceMeasurementId is Guid sourceMeasurementId)
        {
            origin = $"{origin} · source {sourceMeasurementId.ToString("N")[..8]}";
        }

        return origin;
    }

    private string ResolveReportStudyLabel(StudyViewerWindow sourceWindow, SeriesRecord? series)
    {
        if (sourceWindow._viewerNumber == 1)
        {
            return "Report study";
        }

        return "Other study";
    }

    private StudyViewerWindow GetSharedReportOwnerWindow()
    {
        lock (s_openViewerSyncLock)
        {
            return s_openViewerWindows
                .Where(window => string.Equals(window._context.StudyDetails.Study.StudyInstanceUid, _context.StudyDetails.Study.StudyInstanceUid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(window => window._viewerNumber)
                .FirstOrDefault() ?? this;
        }
    }

    private List<StudyViewerWindow> GetSharedReportWindows()
    {
        lock (s_openViewerSyncLock)
        {
            return s_openViewerWindows
                .Where(window => string.Equals(window._context.StudyDetails.Study.StudyInstanceUid, _context.StudyDetails.Study.StudyInstanceUid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(window => window._viewerNumber)
                .ToList();
        }
    }

    private string BuildMeasurementDetails(StudyMeasurement measurement)
    {
        return measurement.Kind switch
        {
            MeasurementKind.Annotation => string.IsNullOrWhiteSpace(measurement.AnnotationText)
                ? "Free-text annotation"
                : $"Text: {measurement.AnnotationText.Trim()}",
            MeasurementKind.Line => $"{measurement.Anchors.Length} anchor points · linear measurement",
            MeasurementKind.Angle => $"{measurement.Anchors.Length} anchor points · angular measurement",
            MeasurementKind.VolumeRoi => measurement.VolumeContours is { Length: > 0 } contours
                ? $"{contours.Length} contour slices · 3D ROI"
                : "3D ROI",
            _ => measurement.Anchors.Length > 0
                ? $"{measurement.Anchors.Length} anchor points"
                : string.Empty,
        };
    }

    private static void ApplyReportComboBoxStyling(ComboBox comboBox)
    {
        comboBox.Background = s_reportComboBackground;
        comboBox.Foreground = s_reportComboForeground;
        comboBox.BorderBrush = s_reportComboBorder;
        comboBox.BorderThickness = new Thickness(1);
        comboBox.Padding = new Thickness(8, 4);
        comboBox.FontSize = 11;
        comboBox.ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock
        {
            Text = item,
            Foreground = s_reportComboForeground,
            FontSize = 11,
        });
    }

    private RegionGuess ResolveMeasurementRegion(StudyMeasurement measurement, ViewportSlot? slot)
    {
        if (_reportRegionOverrides.TryGetValue(measurement.Id, out string? manualOverride) &&
            !string.IsNullOrWhiteSpace(manualOverride))
        {
            string estimatedAutoLabel = EstimateMeasurementRegion(measurement, slot).AutoLabel;
            return new RegionGuess(manualOverride.Trim(), estimatedAutoLabel, $"Manual region override. Auto suggestion: {estimatedAutoLabel}.", true);
        }

        if (TryGetSegmentationBackedMeasurementDisplay(measurement, out _, out string segmentationRegion) &&
            !string.IsNullOrWhiteSpace(segmentationRegion))
        {
            return new RegionGuess(segmentationRegion, segmentationRegion, "Segmentation plugin supplied the region label.");
        }

        RegionGuess estimated = EstimateMeasurementRegion(measurement, slot);

        if (!string.Equals(estimated.Label, "Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            return estimated;
        }

        if (TryFindLearnedSeriesRegionMatch(slot, out RegionGuess learnedSeriesRegion))
        {
            return learnedSeriesRegion;
        }

        if (TryFindLearnedMeasurementPriorMatch(measurement, slot, requiredRegionLabel: null, out VolumeRoiAnatomyPriorMatch learnedMeasurementMatch))
        {
            return new RegionGuess(
                learnedMeasurementMatch.Prior.RegionLabel,
                learnedMeasurementMatch.Prior.RegionLabel,
                learnedMeasurementMatch.Hint,
                IsLearned: true,
                Confidence: NormalizeDisplayedConfidence(learnedMeasurementMatch.Score));
        }

        return estimated;
    }

    private string? GetMeasurementDisplayTitle(StudyMeasurement measurement)
    {
        if (measurement.Kind != MeasurementKind.VolumeRoi)
        {
            return null;
        }

        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        if (TryGetSegmentationBackedMeasurementDisplay(measurement, out string? segmentationTitle, out _) &&
            !HasManualMeasurementAnatomyOverride(measurement))
        {
            return segmentationTitle;
        }

        string anatomyLabel = GetAssignedMeasurementAnatomyLabel(measurement, slot);
        if (!string.IsNullOrWhiteSpace(anatomyLabel))
        {
            return anatomyLabel;
        }

        string regionLabel = GetAssignedMeasurementRegionLabel(measurement, slot);
        if (!string.IsNullOrWhiteSpace(regionLabel))
        {
            return regionLabel;
        }

        if (measurement.SegmentationMaskId is Guid segmentationMaskId &&
            _segmentationMasks.TryGetValue(segmentationMaskId, out SegmentationMask3D? segmentationMask) &&
            !string.IsNullOrWhiteSpace(segmentationMask.Name))
        {
            return segmentationMask.Name.Trim();
        }

        return null;
    }

    private string? GetMeasurementSecondaryLabel(StudyMeasurement measurement)
    {
        if (measurement.Kind != MeasurementKind.VolumeRoi)
        {
            return null;
        }

        if (TryGetSegmentationBackedMeasurementDisplay(measurement, out _, out string? segmentationRegion) &&
            !HasManualMeasurementRegionOverride(measurement))
        {
            return segmentationRegion;
        }

        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        string regionLabel = GetAssignedMeasurementRegionLabel(measurement, slot);
        string anatomyLabel = GetAssignedMeasurementAnatomyLabel(measurement, slot);
        if (!string.IsNullOrWhiteSpace(regionLabel) &&
            !string.IsNullOrWhiteSpace(anatomyLabel) &&
            !string.Equals(regionLabel, anatomyLabel, StringComparison.OrdinalIgnoreCase))
        {
            return regionLabel;
        }

        return string.IsNullOrWhiteSpace(regionLabel)
            ? null
            : regionLabel;
    }

    private string GetMeasurementAccentHex(StudyMeasurement measurement)
    {
        Color color = GetMeasurementAccentColor(measurement) ?? Color.Parse(GetAccentColor(GetMeasurementTypeLabel(measurement)));
        return $"#FF{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private Color? GetMeasurementAccentColor(StudyMeasurement measurement)
    {
        if (measurement.Kind != MeasurementKind.VolumeRoi)
        {
            return null;
        }

        if (TryGetSegmentationBackedMeasurementDisplay(measurement, out _, out string? segmentationRegion) &&
            !HasManualMeasurementRegionOverride(measurement))
        {
            return ResolveAnatomyRegionAccentColor(segmentationRegion);
        }

        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        string regionLabel = GetAssignedMeasurementRegionLabel(measurement, slot);
        return ResolveAnatomyRegionAccentColor(regionLabel);
    }

    private bool TryGetSegmentationBackedMeasurementDisplay(StudyMeasurement measurement, out string title, out string region)
    {
        title = string.Empty;
        region = string.Empty;

        if (measurement.SegmentationMaskId is not Guid segmentationMaskId ||
            !_segmentationMasks.TryGetValue(segmentationMaskId, out SegmentationMask3D? segmentationMask))
        {
            return false;
        }

        title = segmentationMask.Name?.Trim() ?? string.Empty;
        region = ExtractSegmentationRegionLabel(segmentationMask.Metadata.Notes);
        return !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(region);
    }

    private bool HasManualMeasurementRegionOverride(StudyMeasurement measurement) =>
        _reportRegionOverrides.TryGetValue(measurement.Id, out string? regionOverride) &&
        !string.IsNullOrWhiteSpace(regionOverride);

    private bool HasManualMeasurementAnatomyOverride(StudyMeasurement measurement) =>
        _reportAnatomyOverrides.TryGetValue(measurement.Id, out string? anatomyOverride) &&
        !string.IsNullOrWhiteSpace(anatomyOverride);

    private static string ExtractSegmentationRegionLabel(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        const string marker = "Region:";
        int markerIndex = notes.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        string region = notes[(markerIndex + marker.Length)..].Trim();
        int delimiterIndex = region.IndexOfAny([',', ';', '\n', '\r']);
        if (delimiterIndex >= 0)
        {
            region = region[..delimiterIndex].Trim();
        }

        return NormalizeAssignedMeasurementLabel(region);
    }

    private string GetAssignedMeasurementRegionLabel(StudyMeasurement measurement, ViewportSlot? slot)
    {
        if (TryGetSegmentationBackedMeasurementDisplay(measurement, out _, out string segmentationRegion) &&
            !HasManualMeasurementRegionOverride(measurement))
        {
            return segmentationRegion;
        }

        string label = _reportRegionOverrides.TryGetValue(measurement.Id, out string? regionOverride) && !string.IsNullOrWhiteSpace(regionOverride)
            ? regionOverride.Trim()
            : ResolveMeasurementRegion(measurement, slot).Label;
        return NormalizeAssignedMeasurementLabel(label);
    }

    private string GetAssignedMeasurementAnatomyLabel(StudyMeasurement measurement, ViewportSlot? slot)
    {
        if (TryGetSegmentationBackedMeasurementDisplay(measurement, out string segmentationTitle, out _) &&
            !HasManualMeasurementAnatomyOverride(measurement))
        {
            return NormalizeAssignedMeasurementLabel(segmentationTitle);
        }

        string label = _reportAnatomyOverrides.TryGetValue(measurement.Id, out string? anatomyOverride) && !string.IsNullOrWhiteSpace(anatomyOverride)
            ? anatomyOverride.Trim()
            : ResolveMeasurementAnatomy(measurement, slot).Label;
        return NormalizeAssignedMeasurementLabel(label);
    }

    private static string NormalizeAssignedMeasurementLabel(string? label)
    {
        string normalized = label?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, "Auto", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Unassigned", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }

    private static Color ResolveAnatomyRegionAccentColor(string? regionLabel) => (regionLabel?.Trim().ToLowerInvariant() ?? string.Empty) switch
    {
        "neuro" => Color.Parse("#FF8C7CFF"),
        "thorax" => Color.Parse("#FF48C7C2"),
        "thoraxabdomen" => Color.Parse("#FF49C9A2"),
        "thorax abdomen" => Color.Parse("#FF49C9A2"),
        "abdomen" => Color.Parse("#FFE0B85A"),
        "abdomenpelvis" => Color.Parse("#FFD99A68"),
        "abdomen pelvis" => Color.Parse("#FFD99A68"),
        "pelvis" => Color.Parse("#FFD77AA8"),
        "shoulder" => Color.Parse("#FF6FA8FF"),
        "knee" => Color.Parse("#FF72D38E"),
        "ankle" => Color.Parse("#FFF3A65E"),
        _ => Color.Parse("#FF56D3C2"),
    };

    private AnatomyGuess ResolveMeasurementAnatomy(StudyMeasurement measurement, ViewportSlot? slot, RegionGuess? resolvedRegion = null)
    {
        RegionGuess region = resolvedRegion ?? ResolveMeasurementRegion(measurement, slot);
        string? requiredRegionLabel = string.Equals(region.Label, "Unassigned", StringComparison.OrdinalIgnoreCase) ? null : region.Label;

        if (_reportAnatomyOverrides.TryGetValue(measurement.Id, out string? manualLabel) &&
            !string.IsNullOrWhiteSpace(manualLabel))
        {
            AnatomyGuess estimatedOverride = EstimateMeasurementAnatomy(measurement, slot, region);
            return new AnatomyGuess(manualLabel.Trim(), $"Manually confirmed in report panel. Auto suggestion: {estimatedOverride.DisplayLabel}.", true);
        }

        if (TryGetSegmentationBackedMeasurementDisplay(measurement, out string segmentationTitle, out _) &&
            !string.IsNullOrWhiteSpace(segmentationTitle))
        {
            return new AnatomyGuess(segmentationTitle, "Segmentation plugin supplied the structure label.");
        }

        if (TryFindKnowledgePackStructureMatch(measurement, slot, requiredRegionLabel, out PackStructureMatch packMatch))
        {
            return new AnatomyGuess(
                packMatch.Structure.DisplayName,
                packMatch.Hint,
                Confidence: NormalizeDisplayedConfidence(packMatch.Score));
        }

        if (TryFindLearnedMeasurementPriorMatch(
            measurement,
            slot,
            requiredRegionLabel,
            out VolumeRoiAnatomyPriorMatch learnedMatch))
        {
            return new AnatomyGuess(learnedMatch.Prior.AnatomyLabel, learnedMatch.Hint, IsLearned: true, Confidence: NormalizeDisplayedConfidence(learnedMatch.Score));
        }

        AnatomyGuess estimated = EstimateMeasurementAnatomy(measurement, slot, region);

        return estimated;
    }

    private AnatomyGuess EstimateMeasurementAnatomy(StudyMeasurement measurement, ViewportSlot? slot, RegionGuess region)
    {
        AnatomyRegionProfile profile = MapRegionLabelToProfile(region.Label);
        bool headContext = profile == AnatomyRegionProfile.Neuro;
        bool chestContext = profile is AnatomyRegionProfile.Thorax or AnatomyRegionProfile.ThoraxAbdomen;
        bool abdomenContext = profile is AnatomyRegionProfile.Abdomen or AnatomyRegionProfile.AbdomenPelvis or AnatomyRegionProfile.ThoraxAbdomen;
        bool pelvisContext = profile == AnatomyRegionProfile.Pelvis;
        bool shoulderContext = profile == AnatomyRegionProfile.Shoulder;
        bool kneeContext = profile == AnatomyRegionProfile.Knee;
        bool ankleContext = profile == AnatomyRegionProfile.Ankle;
        string bodyPart = ResolveSoftBodyPartExamined(slot);

        if (!measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            if (shoulderContext)
            {
                return new AnatomyGuess("Shoulder joint", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven shoulder estimate."));
            }

            if (kneeContext)
            {
                return new AnatomyGuess("Knee joint", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven knee estimate."));
            }

            if (ankleContext)
            {
                return new AnatomyGuess("Ankle joint", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven ankle estimate."));
            }

            return headContext
                ? new AnatomyGuess("Cerebrum", "Descriptor-driven estimate.")
                : chestContext
                    ? new AnatomyGuess("Thorax", "Descriptor-driven estimate.")
                    : pelvisContext
                        ? new AnatomyGuess("Pelvis", "Descriptor-driven estimate.")
                    : abdomenContext
                        ? new AnatomyGuess("Abdomen", "Descriptor-driven estimate.")
                        : new AnatomyGuess("Unassigned", "No patient-space center available.");
        }

        SpatialBounds? bounds = GetSpatialBounds(slot?.Volume);
        if (bounds is null)
        {
            if (shoulderContext)
            {
                return new AnatomyGuess("Shoulder joint", BuildSoftBodyPartHint(bodyPart, "Study descriptors suggest shoulder anatomy."));
            }

            if (kneeContext)
            {
                return new AnatomyGuess("Knee joint", BuildSoftBodyPartHint(bodyPart, "Study descriptors suggest knee anatomy."));
            }

            if (ankleContext)
            {
                return new AnatomyGuess("Ankle joint", BuildSoftBodyPartHint(bodyPart, "Study descriptors suggest ankle anatomy."));
            }

            return headContext
                ? new AnatomyGuess("Cerebrum", "Study descriptors suggest neuro anatomy.")
                : chestContext
                    ? new AnatomyGuess("Thorax", "Study descriptors suggest thoracic anatomy.")
                    : pelvisContext
                        ? new AnatomyGuess("Pelvis", "Study descriptors suggest pelvic anatomy.")
                    : abdomenContext
                        ? new AnatomyGuess("Abdomen", "Study descriptors suggest abdominal anatomy.")
                        : new AnatomyGuess("Unassigned", "No aligned volume available for normalization.");
        }

        double normalizedX = Normalize(center.X, bounds.Value.MinX, bounds.Value.MaxX);
        double normalizedY = Normalize(center.Y, bounds.Value.MinY, bounds.Value.MaxY);
        double normalizedZ = Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ);
        string laterality = normalizedX <= 0.44 ? "left" : normalizedX >= 0.56 ? "right" : string.Empty;
        string vertical = normalizedZ >= 0.62 ? "upper" : normalizedZ <= 0.38 ? "lower" : "mid";
        bool central = normalizedX > 0.38 && normalizedX < 0.62;
        bool anterior = normalizedY < 0.44;
        bool posterior = normalizedY > 0.56;

        if (shoulderContext)
        {
            if (laterality == "right" || laterality == "left")
            {
                return new AnatomyGuess($"{Capitalize(laterality)} shoulder joint", BuildSoftBodyPartHint(bodyPart, "Shoulder-focused fallback using laterality."));
            }

            return new AnatomyGuess("Shoulder joint", BuildSoftBodyPartHint(bodyPart, "Shoulder-focused fallback."));
        }

        if (kneeContext)
        {
            if (anterior)
            {
                return new AnatomyGuess("Patellar tendon", BuildSoftBodyPartHint(bodyPart, "Anterior knee-focused fallback."));
            }

            if (posterior)
            {
                return new AnatomyGuess("Popliteal fossa", BuildSoftBodyPartHint(bodyPart, "Posterior knee-focused fallback."));
            }

            return new AnatomyGuess("Knee joint", BuildSoftBodyPartHint(bodyPart, "Knee-focused fallback."));
        }

        if (ankleContext)
        {
            if (posterior)
            {
                return new AnatomyGuess("Achilles tendon", BuildSoftBodyPartHint(bodyPart, "Posterior ankle-focused fallback."));
            }

            if (anterior)
            {
                return new AnatomyGuess("Ankle joint", BuildSoftBodyPartHint(bodyPart, "Anterior ankle-focused fallback."));
            }

            return new AnatomyGuess("Tibiotalar joint", BuildSoftBodyPartHint(bodyPart, "Ankle-focused fallback."));
        }

        if (headContext)
        {
            if (normalizedZ <= 0.34 && posterior)
            {
                return new AnatomyGuess("Cerebellum", "Inferior-posterior neuro position.");
            }

            return new AnatomyGuess("Cerebrum", "Supratentorial neuro position.");
        }

        if (chestContext)
        {
            if (!central)
            {
                string zone = vertical switch
                {
                    "upper" => "upper",
                    "lower" => "lower",
                    _ => "mid"
                };
                string side = string.IsNullOrWhiteSpace(laterality) ? "lung" : $"{laterality} lung";
                return new AnatomyGuess($"{side} {zone} region".Replace("  ", " ").Trim(), "Thoracic lateral compartment.");
            }

            return new AnatomyGuess("Mediastinum", "Thoracic central compartment.");
        }

        if (abdomenContext)
        {
            if (laterality == "right" && vertical == "upper")
            {
                return new AnatomyGuess("Liver", "Right upper abdominal location.");
            }

            if (laterality == "left" && vertical == "upper" && anterior)
            {
                return new AnatomyGuess("Stomach", "Left upper anterior abdominal location.");
            }

            if (central && vertical != "upper")
            {
                return new AnatomyGuess("Mesenterium", "Central abdominal location.");
            }

            if (anterior || vertical == "lower")
            {
                return new AnatomyGuess("Intestines", "Anterior or lower abdominal location.");
            }

            return new AnatomyGuess("Abdomen", "Abdominal volume context.");
        }

        return new AnatomyGuess("Unassigned", $"No strong anatomical heuristic matched. Filter profile: {profile}.");
    }

    private RegionGuess EstimateMeasurementRegion(StudyMeasurement measurement, ViewportSlot? slot)
    {
        string context = BuildMeasurementAnatomyContext(slot);
        string bodyPart = ResolveSoftBodyPartExamined(slot);

        if (ContainsAny(context, "shoulder", "rotator", "glenoid", "clav", "acrom", "scap", "humeral head", "proximal humerus") ||
            ContainsAny(bodyPart, "shoulder", "acromioclavicular", "clavicle", "scapula"))
        {
            return new RegionGuess("Shoulder", "Shoulder", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven shoulder region."));
        }

        if (ContainsAny(context, "knee", "menisc", "patell", "cruciate", "collateral", "femorotib", "distal femur", "proximal tibia", "poplite") ||
            ContainsAny(bodyPart, "knee", "patella", "distal femur", "proximal tibia", "proximal fibula"))
        {
            return new RegionGuess("Knee", "Knee", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven knee region."));
        }

        if (ContainsAny(context, "ankle", "achilles", "tibiotalar", "subtalar", "malleol", "talus", "calcane", "navicular", "cuboid", "perone", "posterior tibial") ||
            ContainsAny(bodyPart, "ankle", "foot", "talus", "calcaneus", "heel"))
        {
            return new RegionGuess("Ankle", "Ankle", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven ankle region."));
        }

        if (ContainsAny(context, "brain", "head", "cran", "cereb", "neuro", "pituitary") || ContainsAny(bodyPart, "brain", "head", "skull"))
        {
            return new RegionGuess("Neuro", "Neuro", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven neuro region."));
        }

        if (ContainsAny(context, "pelvis", "pelvic", "bladder", "prostate", "uter", "ovar", "rect") || ContainsAny(bodyPart, "pelvis", "pelvic"))
        {
            return new RegionGuess("Pelvis", "Pelvis", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven pelvic region."));
        }

        if (ContainsAny(context, "upper thorax", "upper chest", "apical lung"))
        {
            return new RegionGuess("Upper thorax", "Upper thorax", "Series descriptors suggest upper thorax.");
        }

        if (ContainsAny(context, "lower thorax", "basal lung", "lung base"))
        {
            return new RegionGuess("Lower thorax", "Lower thorax", "Series descriptors suggest lower thorax.");
        }

        if (ContainsAny(context, "upper abdomen", "liver", "hep", "stomach", "gastric", "pancrea", "spleen", "adrenal", "kidney", "renal"))
        {
            return new RegionGuess("Upper abdomen", "Upper abdomen", BuildSoftBodyPartHint(bodyPart, "Series descriptors suggest upper abdomen."));
        }

        if (ContainsAny(context, "lower abdomen", "appendix", "small bowel", "colon", "mesent", "retroperitone"))
        {
            return new RegionGuess("Lower abdomen", "Lower abdomen", BuildSoftBodyPartHint(bodyPart, "Series descriptors suggest lower abdomen."));
        }

        bool chestContext = ContainsAny(context, "chest", "thorax", "lung", "pulm", "mediast", "card", "heart", "pleura") || ContainsAny(bodyPart, "chest", "thorax", "lung");
        bool abdomenContext = ContainsAny(context, "abd", "bowel", "intestin", "mesent", "pancrea", "spleen", "renal", "kidney") || ContainsAny(bodyPart, "abdomen", "abdominal");

        if (chestContext && abdomenContext)
        {
            return EstimateBorderTorsoRegion(measurement, slot, bodyPart);
        }

        if (chestContext)
        {
            return EstimateThoraxRegion(measurement, slot, bodyPart);
        }

        if (abdomenContext)
        {
            return EstimateAbdomenRegion(measurement, slot, bodyPart);
        }

        AnatomyRegionProfile profile = ResolveAnatomyRegionProfile(measurement, slot, context);
        return profile switch
        {
            AnatomyRegionProfile.Neuro => new RegionGuess("Neuro", "Neuro", "Fallback region profile suggests neuro."),
            AnatomyRegionProfile.Thorax => EstimateThoraxRegion(measurement, slot, bodyPart),
            AnatomyRegionProfile.Abdomen => EstimateAbdomenRegion(measurement, slot, bodyPart),
            AnatomyRegionProfile.Pelvis => new RegionGuess("Pelvis", "Pelvis", "Fallback region profile suggests pelvis."),
            AnatomyRegionProfile.Shoulder => new RegionGuess("Shoulder", "Shoulder", "Fallback region profile suggests shoulder."),
            AnatomyRegionProfile.Knee => new RegionGuess("Knee", "Knee", "Fallback region profile suggests knee."),
            AnatomyRegionProfile.Ankle => new RegionGuess("Ankle", "Ankle", "Fallback region profile suggests ankle."),
            _ => new RegionGuess("Unassigned", "Unassigned", BuildSoftBodyPartHint(bodyPart, "Auto-detect found no reliable region context.")),
        };
    }

    private IReadOnlyList<string> BuildAnatomySelectionOptions(StudyMeasurement measurement, ViewportSlot? slot)
    {
        RegionGuess region = ResolveMeasurementRegion(measurement, slot);
        List<string> options = GetAnatomyStructureOptionsForRegion(region.Label);

        if (_reportAnatomyOverrides.TryGetValue(measurement.Id, out string? manualLabel) &&
            !string.IsNullOrWhiteSpace(manualLabel) &&
            !options.Contains(manualLabel, StringComparer.OrdinalIgnoreCase))
        {
            options.Insert(1, manualLabel.Trim());
        }

        return options;
    }

    private string BuildMeasurementAnatomyContext(ViewportSlot? slot) =>
        string.Join(" ", new[]
        {
            slot?.Series?.Modality,
            slot?.Series?.BodyPart,
            FindLegacySeriesInfo(slot?.Series)?.BodyPart,
            slot?.Series?.SeriesDescription,
            ResolveMeasurementStudyDescription(slot),
            ResolveMeasurementModalities(slot),
        }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private AnatomyRegionProfile ResolveAnatomyRegionProfile(StudyMeasurement measurement, ViewportSlot? slot, string context)
    {
        string bodyPart = ResolveSoftBodyPartExamined(slot);
        bool shoulderContext = ContainsAny(context, "shoulder", "rotator", "glenoid", "clav", "acrom", "scap", "humeral head", "proximal humerus");
        bool kneeContext = ContainsAny(context, "knee", "menisc", "patell", "cruciate", "collateral", "femorotib", "distal femur", "proximal tibia", "poplite");
        bool ankleContext = ContainsAny(context, "ankle", "achilles", "tibiotalar", "subtalar", "malleol", "talus", "calcane", "navicular", "cuboid", "perone", "posterior tibial");
        bool headContext = ContainsAny(context, "brain", "head", "cran", "cereb", "neuro", "pituitary");
        bool chestContext = ContainsAny(context, "chest", "thorax", "lung", "pulm", "mediast", "card", "heart", "pleura");
        bool abdomenContext = ContainsAny(context, "abd", "liver", "hep", "stomach", "gastric", "bowel", "intestin", "mesent", "pancrea", "spleen", "renal", "kidney");
        bool pelvisContext = ContainsAny(context, "pelvis", "pelvic", "bladder", "prostate", "uter", "ovar", "rect");

        if (shoulderContext)
        {
            return AnatomyRegionProfile.Shoulder;
        }

        if (kneeContext)
        {
            return AnatomyRegionProfile.Knee;
        }

        if (ankleContext)
        {
            return AnatomyRegionProfile.Ankle;
        }

        if (headContext)
        {
            return AnatomyRegionProfile.Neuro;
        }

        if (chestContext && abdomenContext)
        {
            return AnatomyRegionProfile.ThoraxAbdomen;
        }

        if (abdomenContext && pelvisContext)
        {
            return AnatomyRegionProfile.AbdomenPelvis;
        }

        if (chestContext)
        {
            return AnatomyRegionProfile.Thorax;
        }

        if (pelvisContext)
        {
            return AnatomyRegionProfile.Pelvis;
        }

        if (abdomenContext)
        {
            return AnatomyRegionProfile.Abdomen;
        }

        if (TryResolveSoftBodyPartProfile(bodyPart, out AnatomyRegionProfile bodyPartProfile) &&
            !headContext &&
            !chestContext &&
            !abdomenContext &&
            !pelvisContext)
        {
            return bodyPartProfile;
        }

        return TryResolveSoftBodyPartProfile(bodyPart, out bodyPartProfile)
            ? bodyPartProfile
            : AnatomyRegionProfile.Unknown;
    }

    private AnatomyProfileGuess EstimateProfileFromMeasurementPosition(StudyMeasurement measurement, ViewportSlot? slot)
    {
        AnatomyGuess estimated = EstimateMeasurementAnatomyFromPosition(measurement, slot);
        AnatomyRegionProfile profile = estimated.Label switch
        {
            "Cerebrum" or "Cerebellum" => AnatomyRegionProfile.Neuro,
            "Mediastinum" or "Thorax" => AnatomyRegionProfile.Thorax,
            _ when estimated.Label.Contains("lung", StringComparison.OrdinalIgnoreCase) => AnatomyRegionProfile.Thorax,
            "Liver" or "Stomach" or "Mesenterium" or "Intestines" or "Abdomen" => AnatomyRegionProfile.Abdomen,
            "Shoulder joint" or "Right shoulder joint" or "Left shoulder joint" => AnatomyRegionProfile.Shoulder,
            "Knee joint" or "Patellar tendon" or "Popliteal fossa" => AnatomyRegionProfile.Knee,
            "Ankle joint" or "Tibiotalar joint" or "Achilles tendon" => AnatomyRegionProfile.Ankle,
            _ => AnatomyRegionProfile.Unknown,
        };

        return new AnatomyProfileGuess(profile, estimated.Hint);
    }

    private AnatomyGuess EstimateMeasurementAnatomyFromPosition(StudyMeasurement measurement, ViewportSlot? slot)
    {
        SpatialBounds? bounds = GetSpatialBounds(slot?.Volume);
        if (!measurement.TryGetPatientCenter(out SpatialVector3D center) || bounds is null)
        {
            return new AnatomyGuess("Unassigned", "No spatial fallback available.");
        }

        double normalizedX = Normalize(center.X, bounds.Value.MinX, bounds.Value.MaxX);
        double normalizedY = Normalize(center.Y, bounds.Value.MinY, bounds.Value.MaxY);
        double normalizedZ = Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ);
        string laterality = normalizedX <= 0.44 ? "left" : normalizedX >= 0.56 ? "right" : string.Empty;
        string vertical = normalizedZ >= 0.62 ? "upper" : normalizedZ <= 0.38 ? "lower" : "mid";
        bool central = normalizedX > 0.38 && normalizedX < 0.62;
        bool anterior = normalizedY < 0.44;
        bool posterior = normalizedY > 0.56;

        if (normalizedZ <= 0.28 && posterior)
        {
            return new AnatomyGuess("Cerebellum", "Inferior-posterior fallback position.");
        }

        if (normalizedZ >= 0.78)
        {
            return new AnatomyGuess("Cerebrum", "Superior fallback position.");
        }

        if (!central)
        {
            string zone = vertical switch
            {
                "upper" => "upper",
                "lower" => "lower",
                _ => "mid"
            };
            string side = string.IsNullOrWhiteSpace(laterality) ? "lung" : $"{laterality} lung";
            return new AnatomyGuess($"{side} {zone} region".Replace("  ", " ").Trim(), "Lateral fallback position.");
        }

        if (laterality == "right" && vertical == "upper")
        {
            return new AnatomyGuess("Liver", "Right upper fallback position.");
        }

        if (laterality == "left" && vertical == "upper" && anterior)
        {
            return new AnatomyGuess("Stomach", "Left upper anterior fallback position.");
        }

        if (central && vertical != "upper")
        {
            return new AnatomyGuess("Mesenterium", "Central abdominal fallback position.");
        }

        if (anterior || vertical == "lower")
        {
            return new AnatomyGuess("Intestines", "Anterior or lower fallback position.");
        }

        return new AnatomyGuess("Unassigned", "Fallback position remained ambiguous.");
    }

    private static List<string> MergeAnatomyOptions(params IReadOnlyList<string>[] groups)
    {
        var merged = new List<string>();
        foreach (IReadOnlyList<string> group in groups)
        {
            foreach (string option in group)
            {
                if (!merged.Contains(option, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(option);
                }
            }
        }

        return merged;
    }

    private string ResolveSoftBodyPartExamined(ViewportSlot? slot)
    {
        if (!string.IsNullOrWhiteSpace(slot?.Series?.BodyPart))
        {
            return slot.Series.BodyPart.Trim();
        }

        return FindLegacySeriesInfo(slot?.Series)?.BodyPart?.Trim() ?? string.Empty;
    }

    private KPACS.DCMClasses.Models.SeriesInfo? FindLegacySeriesInfo(SeriesRecord? series)
    {
        if (series is null)
        {
            return null;
        }

        return _context.StudyDetails.LegacyStudy?.Series.FirstOrDefault(candidate =>
            string.Equals(candidate.SerInstUid, series.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveSoftBodyPartProfile(string bodyPart, out AnatomyRegionProfile profile)
    {
        if (ContainsAny(bodyPart, "brain", "head", "skull", "cran", "neuro"))
        {
            profile = AnatomyRegionProfile.Neuro;
            return true;
        }

        if (ContainsAny(bodyPart, "chest", "thorax", "lung", "pulmonary"))
        {
            profile = AnatomyRegionProfile.Thorax;
            return true;
        }

        if (ContainsAny(bodyPart, "abdomen", "abdominal", "liver", "renal", "kidney", "pancreas", "spleen"))
        {
            profile = AnatomyRegionProfile.Abdomen;
            return true;
        }

        if (ContainsAny(bodyPart, "pelvis", "pelvic", "bladder", "prostate", "uterus", "ovary", "rectum"))
        {
            profile = AnatomyRegionProfile.Pelvis;
            return true;
        }

        if (ContainsAny(bodyPart, "shoulder", "acromioclavicular", "clavicle", "scapula"))
        {
            profile = AnatomyRegionProfile.Shoulder;
            return true;
        }

        if (ContainsAny(bodyPart, "knee", "patella", "distal femur", "proximal tibia", "proximal fibula"))
        {
            profile = AnatomyRegionProfile.Knee;
            return true;
        }

        if (ContainsAny(bodyPart, "ankle", "foot", "talus", "calcaneus", "heel"))
        {
            profile = AnatomyRegionProfile.Ankle;
            return true;
        }

        profile = AnatomyRegionProfile.Unknown;
        return false;
    }

    private string ResolveMeasurementStudyDescription(ViewportSlot? slot)
    {
        if (slot?.Series is null)
        {
            return _context.StudyDetails.Study.StudyDescription;
        }

        return _context.StudyDetails.Series.Any(candidate =>
            string.Equals(candidate.SeriesInstanceUid, slot.Series.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase))
            ? _context.StudyDetails.Study.StudyDescription
            : _selectedPriorStudy?.StudyDescription ?? string.Empty;
    }

    private string ResolveMeasurementModalities(ViewportSlot? slot)
    {
        if (slot?.Series is null)
        {
            return _context.StudyDetails.Study.Modalities;
        }

        return _context.StudyDetails.Series.Any(candidate =>
            string.Equals(candidate.SeriesInstanceUid, slot.Series.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase))
            ? _context.StudyDetails.Study.Modalities
            : _selectedPriorStudy?.Modalities ?? string.Empty;
    }

    private static AnatomyRegionProfile MapRegionLabelToProfile(string regionLabel) => regionLabel switch
    {
        "Neuro" => AnatomyRegionProfile.Neuro,
        "Upper thorax" or "Lower thorax" => AnatomyRegionProfile.Thorax,
        "Upper abdomen" or "Lower abdomen" => AnatomyRegionProfile.Abdomen,
        "Pelvis" => AnatomyRegionProfile.Pelvis,
        "Shoulder" => AnatomyRegionProfile.Shoulder,
        "Knee" => AnatomyRegionProfile.Knee,
        "Ankle" => AnatomyRegionProfile.Ankle,
        _ => AnatomyRegionProfile.Unknown,
    };

    private RegionGuess EstimateThoraxRegion(StudyMeasurement measurement, ViewportSlot? slot, string bodyPart)
    {
        if (TryEstimateNormalizedZ(measurement, slot, out double normalizedZ) && normalizedZ >= 0.58)
        {
            return new RegionGuess("Upper thorax", "Upper thorax", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests upper thorax."));
        }

        return new RegionGuess("Lower thorax", "Lower thorax", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests lower thorax."));
    }

    private RegionGuess EstimateAbdomenRegion(StudyMeasurement measurement, ViewportSlot? slot, string bodyPart)
    {
        if (TryEstimateNormalizedZ(measurement, slot, out double normalizedZ) && normalizedZ >= 0.50)
        {
            return new RegionGuess("Upper abdomen", "Upper abdomen", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests upper abdomen."));
        }

        return new RegionGuess("Lower abdomen", "Lower abdomen", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests lower abdomen."));
    }

    private RegionGuess EstimateBorderTorsoRegion(StudyMeasurement measurement, ViewportSlot? slot, string bodyPart)
    {
        if (TryEstimateNormalizedZ(measurement, slot, out double normalizedZ) && normalizedZ >= 0.52)
        {
            return new RegionGuess("Lower thorax", "Lower thorax", BuildSoftBodyPartHint(bodyPart, "Mixed descriptors with superior bias suggest lower thorax."));
        }

        return new RegionGuess("Upper abdomen", "Upper abdomen", BuildSoftBodyPartHint(bodyPart, "Mixed descriptors with inferior bias suggest upper abdomen."));
    }

    private bool TryEstimateNormalizedZ(StudyMeasurement measurement, ViewportSlot? slot, out double normalizedZ)
    {
        normalizedZ = 0.5;
        SpatialBounds? bounds = GetSpatialBounds(slot?.Volume);
        if (bounds is null || !measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            return false;
        }

        normalizedZ = Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ);
        return true;
    }

    private string GetRegionSelectionValue(Guid measurementId) =>
        _reportRegionOverrides.TryGetValue(measurementId, out string? regionLabel) && !string.IsNullOrWhiteSpace(regionLabel)
            ? regionLabel
            : "Auto";

    private void ApplyManualRegionSelection(Guid measurementId, string selectedValue)
    {
        if (string.Equals(selectedValue, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(selectedValue))
        {
            _reportRegionOverrides.Remove(measurementId);
        }
        else
        {
            _reportRegionOverrides[measurementId] = selectedValue.Trim();
            _reportReviewStates[measurementId] = "Confirmed";
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: true);
    }

    private static string BuildSoftBodyPartHint(string bodyPart, string fallbackHint)
    {
        return string.IsNullOrWhiteSpace(bodyPart)
            ? fallbackHint
            : $"{fallbackHint} Soft body-part hint: {bodyPart.Trim()}.";
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private string GetAnatomySelectionValue(Guid measurementId) =>
        _reportAnatomyOverrides.TryGetValue(measurementId, out string? manualLabel) &&
        !string.IsNullOrWhiteSpace(manualLabel)
            ? manualLabel
            : "Auto";

    private void ApplyManualAnatomySelection(Guid measurementId, string selectedValue)
    {
        if (string.Equals(selectedValue, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(selectedValue))
        {
            _reportAnatomyOverrides.Remove(measurementId);
        }
        else
        {
            _reportAnatomyOverrides[measurementId] = selectedValue.Trim();
            _reportReviewStates[measurementId] = "Confirmed";
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: true);
    }

    private async Task LoadVolumeRoiAnatomyPriorsAsync()
    {
        if (Application.Current is not App app)
        {
            return;
        }

        List<VolumeRoiAnatomyPriorRecord> priors = await app.Repository.GetVolumeRoiAnatomyPriorsAsync(null, _priorLookupCancellation.Token);
        if (_priorLookupCancellation.IsCancellationRequested)
        {
            return;
        }

        _volumeRoiAnatomyPriors.Clear();
        _volumeRoiAnatomyPriors.AddRange(priors);
        _volumeRoiAnatomyPriorsLoaded = true;

        if (_reportPanelVisible || _reportPanelPinned)
        {
            RefreshReportPanel(forceVisible: _reportPanelPinned);
        }

        if (_anatomyPanelVisible || _anatomyPanelPinned)
        {
            RefreshAnatomyPanel(forceVisible: _anatomyPanelPinned);
        }
    }

    private async Task PersistVolumeRoiAnatomyPriorAsync(Guid measurementId, string anatomyLabel)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        StudyMeasurement? measurement = _studyMeasurements.FirstOrDefault(candidate => candidate.Id == measurementId && candidate.Kind == MeasurementKind.VolumeRoi);
        if (measurement is null)
        {
            return;
        }

        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        RegionGuess region = ResolveMeasurementRegion(measurement, slot);
        if (!TryBuildVolumeRoiPriorRecord(measurement, slot, anatomyLabel, region.Label, out VolumeRoiAnatomyPriorRecord prior))
        {
            return;
        }

        await app.Repository.UpsertVolumeRoiAnatomyPriorAsync(prior, _priorLookupCancellation.Token);
        if (_priorLookupCancellation.IsCancellationRequested)
        {
            return;
        }

        int existingIndex = _volumeRoiAnatomyPriors.FindIndex(candidate => string.Equals(candidate.Signature, prior.Signature, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            VolumeRoiAnatomyPriorRecord existing = _volumeRoiAnatomyPriors[existingIndex];
            _volumeRoiAnatomyPriors[existingIndex] = prior with { PriorKey = existing.PriorKey, UseCount = existing.UseCount + 1 };
        }
        else
        {
            _volumeRoiAnatomyPriors.Insert(0, prior);
        }

        _volumeRoiAnatomyPriorsLoaded = true;
    }

    private bool TryFindLearnedSeriesRegionMatch(ViewportSlot? slot, out RegionGuess region)
    {
        region = default!;
        if (slot?.Series is null || !_volumeRoiAnatomyPriorsLoaded || _volumeRoiAnatomyPriors.Count == 0)
        {
            return false;
        }

        string modality = slot.Series.Modality?.Trim() ?? string.Empty;
        string bodyPart = ResolveSoftBodyPartExamined(slot);
        string studyDescription = ResolveMeasurementStudyDescription(slot).Trim();
        string seriesDescription = slot.Series.SeriesDescription?.Trim() ?? string.Empty;
        string context = $"{studyDescription} {seriesDescription} {bodyPart}".Trim();

        Dictionary<string, double> regionScores = new(StringComparer.OrdinalIgnoreCase);
        foreach (VolumeRoiAnatomyPriorRecord prior in _volumeRoiAnatomyPriors)
        {
            if (string.IsNullOrWhiteSpace(prior.RegionLabel) || string.Equals(prior.RegionLabel, "Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double score = ScoreSeriesRegionPrior(modality, bodyPart, context, prior);
            if (score < 0.22)
            {
                continue;
            }

            regionScores[prior.RegionLabel] = regionScores.GetValueOrDefault(prior.RegionLabel) + score;
        }

        if (regionScores.Count == 0)
        {
            return false;
        }

        KeyValuePair<string, double> best = regionScores.OrderByDescending(pair => pair.Value).First();
        double secondBest = regionScores.Count > 1
            ? regionScores.OrderByDescending(pair => pair.Value).Skip(1).First().Value
            : 0;

        if (best.Value < 0.42 || best.Value - secondBest < 0.08)
        {
            return false;
        }

        region = new RegionGuess(
            best.Key,
            best.Key,
            $"Learned from previously labeled 3D ROI context in similar series ({best.Key}, confidence {Math.Min(0.99, best.Value):P0}).",
            IsLearned: true,
            Confidence: Math.Min(0.99, best.Value));
        return true;
    }

    private bool TryFindLearnedMeasurementPriorMatch(StudyMeasurement measurement, ViewportSlot? slot, string? requiredRegionLabel, out VolumeRoiAnatomyPriorMatch match)
    {
        match = null!;
        if (!_useLegacyAnatomyPriors || !_volumeRoiAnatomyPriorsLoaded || _volumeRoiAnatomyPriors.Count == 0)
        {
            return false;
        }

        if (!TryBuildMeasurementPriorProbe(measurement, slot, out VolumeRoiPriorProbe probe))
        {
            return false;
        }

        bool compareShape = measurement.Kind == MeasurementKind.VolumeRoi;
        VolumeRoiAnatomyPriorMatch? bestMatch = null;
        MatchCandidateDebugInfo? bestAcceptedCandidate = null;
        MatchCandidateDebugInfo? runnerUpCandidate = null;
        MatchCandidateDebugInfo? bestRejectedCandidate = null;
        foreach (VolumeRoiAnatomyPriorRecord prior in _volumeRoiAnatomyPriors)
        {
            if (!string.IsNullOrWhiteSpace(requiredRegionLabel) &&
                !string.Equals(requiredRegionLabel, prior.RegionLabel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double baseScore = ScoreAnatomyPriorMatch(probe, prior, compareShape);
            KnowledgePackPriorAssessment knowledgeAssessment = AssessKnowledgePackPriorMatch(probe, prior);
            if (knowledgeAssessment.Rejected)
            {
                MatchCandidateDebugInfo rejectedCandidate = new(prior, baseScore, baseScore, knowledgeAssessment);
                if (bestRejectedCandidate is null || rejectedCandidate.BaseScore > bestRejectedCandidate.BaseScore)
                {
                    bestRejectedCandidate = rejectedCandidate;
                }
                continue;
            }

            double score = baseScore + knowledgeAssessment.ScoreAdjustment;
            if (score < (compareShape ? 0.55 : 0.50))
            {
                continue;
            }

            MatchCandidateDebugInfo candidate = new(prior, baseScore, score, knowledgeAssessment);

            if (bestMatch is null || score > bestMatch.Score)
            {
                runnerUpCandidate = bestAcceptedCandidate;
                bestAcceptedCandidate = candidate;
                bestMatch = new VolumeRoiAnatomyPriorMatch(prior, score, string.Empty);
            }
            else if (runnerUpCandidate is null || score > runnerUpCandidate.FinalScore)
            {
                runnerUpCandidate = candidate;
            }
        }

        if (bestMatch is null)
        {
            return false;
        }

        string hint = BuildPackConfidenceHint(bestMatch.Score);
        if (bestAcceptedCandidate is not null)
        {
            string debugHint = BuildMatchDebugExplanation(bestAcceptedCandidate, runnerUpCandidate, bestRejectedCandidate, probe);
            hint = AppendReportDebugHint(hint, debugHint);
        }

        match = bestMatch with { Hint = hint };
        return true;
    }

    private bool TryFindLearnedVolumeRoiPriorMatch(StudyMeasurement measurement, ViewportSlot? slot, out VolumeRoiAnatomyPriorMatch match)
    {
        match = null!;
        if (measurement.Kind != MeasurementKind.VolumeRoi)
        {
            return false;
        }

        return TryFindLearnedMeasurementPriorMatch(measurement, slot, requiredRegionLabel: null, out match);
    }

    private bool TryBuildVolumeRoiPriorRecord(StudyMeasurement measurement, ViewportSlot? slot, string anatomyLabel, string regionLabel, out VolumeRoiAnatomyPriorRecord prior)
    {
        prior = null!;
        if (!TryBuildVolumeRoiPriorProbe(measurement, slot, out VolumeRoiPriorProbe probe))
        {
            return false;
        }

        string signature = string.Join('|', new[]
        {
            _context.StudyDetails.Study.StudyInstanceUid,
            slot?.Series?.SeriesInstanceUid ?? string.Empty,
            anatomyLabel.Trim(),
            regionLabel.Trim(),
            probe.NormalizedCenterX.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedCenterY.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedCenterZ.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedSizeX.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedSizeY.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedSizeZ.ToString("F3", CultureInfo.InvariantCulture),
        });

        prior = new VolumeRoiAnatomyPriorRecord(
            0,
            signature,
            anatomyLabel.Trim(),
            regionLabel.Trim(),
            probe.Modality,
            probe.BodyPartExamined,
            probe.StudyDescription,
            probe.SeriesDescription,
            probe.NormalizedCenterX,
            probe.NormalizedCenterY,
            probe.NormalizedCenterZ,
            probe.NormalizedSizeX,
            probe.NormalizedSizeY,
            probe.NormalizedSizeZ,
            probe.EstimatedVolumeCubicMillimeters,
            probe.StructureSignature,
            _context.StudyDetails.Study.StudyInstanceUid,
            slot?.Series?.SeriesInstanceUid ?? string.Empty,
            DateTime.UtcNow,
            1);
        return true;
    }

    private bool TryBuildVolumeRoiPriorProbe(StudyMeasurement measurement, ViewportSlot? slot, out VolumeRoiPriorProbe probe)
    {
        probe = null!;
        if (measurement.Kind != MeasurementKind.VolumeRoi || measurement.VolumeContours is not { Length: > 0 })
        {
            return false;
        }

        return TryBuildMeasurementPriorProbe(measurement, slot, out probe);
    }

    private bool TryBuildMeasurementPriorProbe(StudyMeasurement measurement, ViewportSlot? slot, out VolumeRoiPriorProbe probe)
    {
        probe = null!;

        SeriesVolume? volume = slot?.Volume;
        SpatialBounds? bounds = GetSpatialBounds(volume);
        SpatialVector3D[] points = measurement.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();
        if (bounds is null || points.Length == 0 || !measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            return false;
        }

        double rangeX = Math.Max(bounds.Value.MaxX - bounds.Value.MinX, double.Epsilon);
        double rangeY = Math.Max(bounds.Value.MaxY - bounds.Value.MinY, double.Epsilon);
        double rangeZ = Math.Max(bounds.Value.MaxZ - bounds.Value.MinZ, double.Epsilon);
        double minX = points.Min(point => point.X);
        double maxX = points.Max(point => point.X);
        double minY = points.Min(point => point.Y);
        double maxY = points.Max(point => point.Y);
        double minZ = points.Min(point => point.Z);
        double maxZ = points.Max(point => point.Z);
        ProbeAxisCorrection? axisCorrection = TryBuildProbeAxisCorrection(volume, center);

        probe = new VolumeRoiPriorProbe(
            slot?.Series?.Modality?.Trim() ?? string.Empty,
            ResolveSoftBodyPartExamined(slot),
            ResolveMeasurementStudyDescription(slot).Trim(),
            slot?.Series?.SeriesDescription?.Trim() ?? string.Empty,
            Normalize(center.X, bounds.Value.MinX, bounds.Value.MaxX),
            Normalize(center.Y, bounds.Value.MinY, bounds.Value.MaxY),
            Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ),
            Math.Clamp((maxX - minX) / rangeX, 0, 1),
            Math.Clamp((maxY - minY) / rangeY, 0, 1),
            Math.Clamp((maxZ - minZ) / rangeZ, 0, 1),
            measurement.Kind == MeasurementKind.VolumeRoi && measurement.VolumeContours is { Length: > 0 }
                ? EstimateMeasurementVolumeCubicMillimeters(measurement.VolumeContours)
                : 0,
            TryBuildMeasurementStructureSignature(measurement, slot?.Volume, out AnatomyStructureSignature? structureSignature)
                ? structureSignature
                : null,
            axisCorrection);
        return true;
    }

    private static bool TryBuildMeasurementStructureSignature(StudyMeasurement measurement, SeriesVolume? volume, out AnatomyStructureSignature? signature)
    {
        signature = null;
        if (volume is null)
        {
            return false;
        }

        bool success = measurement.Kind == MeasurementKind.VolumeRoi && measurement.VolumeContours is { Length: > 0 }
            ? AnatomyStructureSignatureService.TryCreateForStoredPrior(measurement, volume, out AnatomyStructureSignature built)
            : AnatomyStructureSignatureService.TryCreateForMeasurementProbe(measurement, volume, out built);
        if (!success)
        {
            return false;
        }

        signature = built;
        return true;
    }

    private double ScoreVolumeRoiPrior(VolumeRoiPriorProbe probe, VolumeRoiAnatomyPriorRecord prior)
    {
        return ScoreAnatomyPriorMatch(probe, prior, compareShape: true);
    }

    private double ScoreAnatomyPriorMatch(VolumeRoiPriorProbe probe, VolumeRoiAnatomyPriorRecord prior, bool compareShape)
    {
        double score = 0;

        if (!string.IsNullOrWhiteSpace(probe.Modality) && string.Equals(probe.Modality, prior.Modality, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.16;
        }

        score += 0.12 * ComputeBodyPartSimilarity(probe.BodyPartExamined, prior.BodyPartExamined);

        double centerDistance = Math.Sqrt(
            Math.Pow(probe.NormalizedCenterX - prior.NormalizedCenterX, 2) +
            Math.Pow(probe.NormalizedCenterY - prior.NormalizedCenterY, 2) +
            Math.Pow(probe.NormalizedCenterZ - prior.NormalizedCenterZ, 2));
        score += Math.Max(0, 0.36 - (centerDistance * 0.72));

        if (IsProbeCenterWithinPrior(probe, prior))
        {
            score += 0.22;
        }

        score += 0.12 * ComputeContextTokenOverlap(probe.ContextText, $"{prior.StudyDescription} {prior.SeriesDescription} {prior.BodyPartExamined}");

        if (compareShape)
        {
            double sizeDistance = (
                Math.Abs(probe.NormalizedSizeX - prior.NormalizedSizeX) +
                Math.Abs(probe.NormalizedSizeY - prior.NormalizedSizeY) +
                Math.Abs(probe.NormalizedSizeZ - prior.NormalizedSizeZ)) / 3.0;
            score += Math.Max(0, 0.16 - (sizeDistance * 0.30));

            if (probe.EstimatedVolumeCubicMillimeters > 0 && prior.EstimatedVolumeCubicMillimeters > 0)
            {
                double ratio = Math.Max(probe.EstimatedVolumeCubicMillimeters, prior.EstimatedVolumeCubicMillimeters) /
                               Math.Max(1.0, Math.Min(probe.EstimatedVolumeCubicMillimeters, prior.EstimatedVolumeCubicMillimeters));
                score += Math.Max(0, 0.10 - (Math.Log(ratio) * 0.06));
            }
        }

        double structureSimilarity = AnatomyStructureSignatureService.Compare(probe.StructureSignature, prior.StructureSignature, compareShape);
        if (structureSimilarity > 0)
        {
            score += structureSimilarity * (compareShape ? 0.26 : 0.22);
        }

        score += Math.Min(0.08, Math.Max(0, (prior.UseCount - 1) * 0.01));
        return score;
    }

    private KnowledgePackPriorAssessment AssessKnowledgePackPriorMatch(VolumeRoiPriorProbe probe, VolumeRoiAnatomyPriorRecord prior)
    {
        AnatomyKnowledgePack? pack = _activeCraniumKnowledgePack;
        if (pack is null || !string.Equals(prior.RegionLabel, "Neuro", StringComparison.OrdinalIgnoreCase))
        {
            return KnowledgePackPriorAssessment.Neutral;
        }

        AnatomyStructureDefinition? structure = ResolveKnowledgePackStructureForPrior(pack, prior.AnatomyLabel);
        if (structure is null)
        {
            return KnowledgePackPriorAssessment.Neutral;
        }

        ProbeKnowledgeFeatures features = BuildProbeKnowledgeFeatures(probe);
        string? rejectedReason = EvaluateKnowledgePackHardRules(structure, features);
        if (!string.IsNullOrWhiteSpace(rejectedReason))
        {
            string debugSummary = BuildKnowledgeDebugSummary(structure, features, 0, 0, 0, rejectedReason);
            return new KnowledgePackPriorAssessment(true, 0, $"Knowledge pack rejected candidate: {rejectedReason}.", debugSummary, rejectedReason, 0, 0, 0);
        }

        double positionFit = ComputeStructurePositionFit(structure, features);
        double materialFit = ComputeStructureMaterialFit(probe, structure, features);
        double relationFit = ComputeStructureRelationFit(structure, features);
        double scoreAdjustment = (positionFit * 0.18) + (materialFit * 0.14) + (relationFit * 0.12);

        if (scoreAdjustment <= 0.001)
        {
            string debugSummary = BuildKnowledgeDebugSummary(structure, features, positionFit, materialFit, relationFit, null);
            return new KnowledgePackPriorAssessment(false, 0, string.Empty, debugSummary, null, positionFit, materialFit, relationFit);
        }

        string hint = $"Pack confidence: {DescribeMatchStrength(scoreAdjustment)} · {pack.DisplayName}.";
        string summary = BuildKnowledgeDebugSummary(structure, features, positionFit, materialFit, relationFit, null);
        return new KnowledgePackPriorAssessment(false, scoreAdjustment, hint, summary, null, positionFit, materialFit, relationFit);
    }

    private bool TryFindKnowledgePackStructureMatch(StudyMeasurement measurement, ViewportSlot? slot, string? requiredRegionLabel, out PackStructureMatch match)
    {
        match = null!;

        AnatomyKnowledgePack? pack = _activeCraniumKnowledgePack;
        if (pack is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requiredRegionLabel) && !string.Equals(requiredRegionLabel, "Neuro", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryBuildMeasurementPriorProbe(measurement, slot, out VolumeRoiPriorProbe probe))
        {
            return false;
        }

        PackStructureMatch? bestMatch = null;
        PackStructureMatch? runnerUp = null;
        PackStructureMatch? bestRejected = null;
        foreach (AnatomyStructureDefinition structure in pack.Structures)
        {
            KnowledgePackPriorAssessment assessment = AssessKnowledgePackStructureMatch(probe, structure);
            if (assessment.Rejected)
            {
                PackStructureMatch rejected = new(structure, assessment.ScoreAdjustment, assessment.Hint, assessment);
                if (bestRejected is null || rejected.Score > bestRejected.Score)
                {
                    bestRejected = rejected;
                }

                continue;
            }

            double score = assessment.ScoreAdjustment;
            bool hasMeaningfulPosition = assessment.PositionFit >= 0.26;
            bool hasMeaningfulRelations = assessment.RelationFit >= 0.45;
            bool hasMeaningfulMaterial = assessment.MaterialFit >= 0.35;
            if (score < 0.20 || (!hasMeaningfulPosition && !hasMeaningfulRelations && !hasMeaningfulMaterial))
            {
                continue;
            }

            PackStructureMatch candidate = new(structure, score, string.Empty, assessment);
            if (bestMatch is null || candidate.Score > bestMatch.Score)
            {
                runnerUp = bestMatch;
                bestMatch = candidate;
            }
            else if (runnerUp is null || candidate.Score > runnerUp.Score)
            {
                runnerUp = candidate;
            }
        }

        if (bestMatch is null)
        {
            return false;
        }

        string hint = $"Direct knowledge-pack match from the current anatomy pack. {BuildPackConfidenceHint(bestMatch.Score)}";
        string debug = BuildDirectPackMatchDebugExplanation(bestMatch, runnerUp, bestRejected, probe);
        hint = AppendReportDebugHint(hint, debug);

        match = bestMatch with { Hint = hint };
        return true;
    }

    private string AppendReportDebugHint(string hint, string? debugHint)
    {
        if (!_reportDebugEnabled || string.IsNullOrWhiteSpace(debugHint))
        {
            return hint;
        }

        return string.Concat(hint, Environment.NewLine, debugHint);
    }

    private KnowledgePackPriorAssessment AssessKnowledgePackStructureMatch(VolumeRoiPriorProbe probe, AnatomyStructureDefinition structure)
    {
        ProbeKnowledgeFeatures features = BuildProbeKnowledgeFeatures(probe);
        string? rejectedReason = EvaluateKnowledgePackHardRules(structure, features);
        double modalityFit = ComputeStructureModalityFit(probe, structure);
        if (!string.IsNullOrWhiteSpace(rejectedReason))
        {
            string debugSummary = BuildKnowledgeDebugSummary(structure, features, 0, 0, 0, rejectedReason);
            return new KnowledgePackPriorAssessment(true, 0, $"Knowledge pack rejected candidate: {rejectedReason}.", debugSummary, rejectedReason, 0, 0, 0);
        }

        double positionFit = ComputeStructurePositionFit(structure, features);
        double materialFit = ComputeStructureMaterialFit(probe, structure, features);
        double relationFit = ComputeStructureRelationFit(structure, features);
        double materialTerm = materialFit < 0
            ? Math.Max(-0.12, materialFit * 0.08)
            : materialFit * 0.14;
        double score = (positionFit * 0.56) + (relationFit * 0.24) + materialTerm + (modalityFit * 0.06);
        score = Math.Clamp(score, 0, 0.99);

        string hint = $"Direct knowledge-pack match. {BuildPackConfidenceHint(score)}";
        string summary = BuildKnowledgeDebugSummary(structure, features, positionFit, materialFit, relationFit, null);
        return new KnowledgePackPriorAssessment(false, score, hint, summary, null, positionFit, materialFit, relationFit);
    }

    private static double ComputeStructureModalityFit(VolumeRoiPriorProbe probe, AnatomyStructureDefinition structure)
    {
        if (structure.SupportedModalities.Count == 0 || string.IsNullOrWhiteSpace(probe.Modality))
        {
            return 1;
        }

        return structure.SupportedModalities.Any(value => string.Equals(value, probe.Modality, StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;
    }

    private string BuildMatchDebugExplanation(MatchCandidateDebugInfo winner, MatchCandidateDebugInfo? runnerUp, MatchCandidateDebugInfo? rejectedCandidate, VolumeRoiPriorProbe probe)
    {
        List<string> parts = [];

        parts.Add($"Debug: winner {winner.Prior.AnatomyLabel} (score {winner.FinalScore:0.00}). {winner.Assessment.DebugSummary}");

        if (runnerUp is not null)
        {
            parts.Add($"Next candidate {runnerUp.Prior.AnatomyLabel} (score {runnerUp.FinalScore:0.00}). {runnerUp.Assessment.DebugSummary}");
        }

        if (rejectedCandidate is not null)
        {
            parts.Add($"Rejected candidate {rejectedCandidate.Prior.AnatomyLabel}. {rejectedCandidate.Assessment.DebugSummary}");
        }

        parts.Add($"Probe: {BuildProbeDebugSummary(probe)}");
        return string.Join(Environment.NewLine, parts);
    }

    private string BuildDirectPackMatchDebugExplanation(PackStructureMatch winner, PackStructureMatch? runnerUp, PackStructureMatch? rejectedCandidate, VolumeRoiPriorProbe probe)
    {
        List<string> parts = [];

        parts.Add($"Debug: direct pack winner {winner.Structure.DisplayName} (score {winner.Score:0.00}). {winner.Assessment.DebugSummary}");

        if (runnerUp is not null)
        {
            parts.Add($"Next direct pack candidate {runnerUp.Structure.DisplayName} (score {runnerUp.Score:0.00}). {runnerUp.Assessment.DebugSummary}");
        }

        if (rejectedCandidate is not null)
        {
            parts.Add($"Rejected direct pack candidate {rejectedCandidate.Structure.DisplayName}. {rejectedCandidate.Assessment.DebugSummary}");
        }

        parts.Add($"Probe: {BuildProbeDebugSummary(probe)}");
        return string.Join(Environment.NewLine, parts);
    }

    private static string BuildProbeDebugSummary(VolumeRoiPriorProbe probe)
    {
        ProbeKnowledgeFeatures features = BuildProbeKnowledgeFeatures(probe);
        string axisSummary = probe.AxisCorrection is null
            ? "axis=raw"
            : $"axis=auto(rot={probe.AxisCorrection.Value.RotationDegrees:0.0}°, shiftX={probe.AxisCorrection.Value.CenterlineShiftX:0.00}, shiftY={probe.AxisCorrection.Value.CenterlineShiftY:0.00})";
        return $"lr={features.SignedLeftRight:0.00}, ap={features.SignedAnteriorPosterior:0.00}, midline={features.DistanceToMidline:0.00}, skull_base={features.DistanceToSkullBase:0.00}, vertex={features.DistanceToVertex:0.00}, posterior_fossa={(features.LikelyPosteriorFossa ? "yes" : "no")}, {axisSummary}";
    }

    private static string BuildKnowledgeDebugSummary(AnatomyStructureDefinition structure, ProbeKnowledgeFeatures features, double positionFit, double materialFit, double relationFit, string? rejectedReason)
    {
        string structureName = string.IsNullOrWhiteSpace(structure.DisplayName) ? structure.Id : structure.DisplayName;
        string core = $"{structureName}: posterior_fossa={(features.LikelyPosteriorFossa ? "yes" : "no")}, ap={features.SignedAnteriorPosterior:0.00}, midline={features.DistanceToMidline:0.00}, skull_base={features.DistanceToSkullBase:0.00}, lr={features.SignedLeftRight:0.00}, position={DescribeFitBand(positionFit)}, relations={DescribeFitBand(relationFit)}, material={DescribeFitBand(Math.Max(0, materialFit))}";
        return string.IsNullOrWhiteSpace(rejectedReason)
            ? core
            : $"{core}, rejected={rejectedReason}";
    }

    private static string DescribeFitBand(double value)
    {
        if (value >= 0.85)
        {
            return "strong";
        }

        if (value >= 0.6)
        {
            return "moderate";
        }

        if (value > 0.2)
        {
            return "weak";
        }

        return "low";
    }

    private string BuildPackConfidenceHint(double score)
    {
        string? packName = _activeCraniumKnowledgePack?.DisplayName;
        if (string.IsNullOrWhiteSpace(packName))
        {
            return $"Pack confidence: {DescribeMatchStrength(score)}.";
        }

        return $"Pack confidence: {DescribeMatchStrength(score)} · {packName}.";
    }

    private static string DescribeReportAssignmentSource(bool isManual, bool isLearned, double confidence, string? autoLabel)
    {
        if (isManual)
        {
            return "Assignment source: manual.";
        }

        if (isLearned)
        {
            return $"Assignment source: suggested ({DescribeMatchStrength(confidence)} confidence).";
        }

        if (!string.IsNullOrWhiteSpace(autoLabel))
        {
            return $"Assignment source: automatic estimate ({autoLabel}).";
        }

        return "Assignment source: automatic estimate.";
    }

    private static double NormalizeDisplayedConfidence(double score) => Math.Clamp(score, 0.0, 0.99);

    private static string DescribeMatchStrength(double score)
    {
        double normalized = NormalizeDisplayedConfidence(score);
        if (normalized >= 0.9)
        {
            return "very strong";
        }

        if (normalized >= 0.75)
        {
            return "strong";
        }

        if (normalized >= 0.6)
        {
            return "moderate";
        }

        return "tentative";
    }

    private static AnatomyStructureDefinition? ResolveKnowledgePackStructure(AnatomyKnowledgePack pack, string anatomyLabel)
    {
        if (string.IsNullOrWhiteSpace(anatomyLabel))
        {
            return null;
        }

        string normalized = anatomyLabel.Trim();
        return pack.Structures.FirstOrDefault(structure =>
            string.Equals(structure.Id, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(structure.DisplayName, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static AnatomyStructureDefinition? ResolveKnowledgePackStructureForPrior(AnatomyKnowledgePack pack, string anatomyLabel)
    {
        AnatomyStructureDefinition? structure = ResolveKnowledgePackStructure(pack, anatomyLabel);
        if (structure is not null)
        {
            return structure;
        }

        return BuildHeuristicNeuroStructure(anatomyLabel);
    }

    private static AnatomyStructureDefinition? BuildHeuristicNeuroStructure(string anatomyLabel)
    {
        if (string.IsNullOrWhiteSpace(anatomyLabel))
        {
            return null;
        }

        string normalized = anatomyLabel.Trim();
        string identity = normalized.ToLowerInvariant();
        if (!identity.Contains("pons")
            && !identity.Contains("brainstem")
            && !identity.Contains("vermis")
            && !identity.Contains("cerebell")
            && !identity.Contains("ventricle")
            && !identity.Contains("thalam")
            && !identity.Contains("caudate")
            && !identity.Contains("lenticular")
            && !identity.Contains("putamen")
            && !identity.Contains("pallidus"))
        {
            return null;
        }

        return new AnatomyStructureDefinition
        {
            Id = normalized.Replace(' ', '_'),
            DisplayName = normalized,
        };
    }

    private static ProbeKnowledgeFeatures BuildProbeKnowledgeFeatures(VolumeRoiPriorProbe probe)
    {
        double signedLeftRight = probe.AxisCorrection?.SignedLeftRight ?? ((probe.NormalizedCenterX * 2.0) - 1.0);
        double signedAnteriorPosterior = probe.AxisCorrection?.SignedAnteriorPosterior ?? (1.0 - (probe.NormalizedCenterY * 2.0));
        double cranialCaudal = probe.NormalizedCenterZ;
        double distanceToMidline = probe.AxisCorrection?.DistanceToMidline ?? Math.Abs(signedLeftRight);
        double distanceToSkullBase = cranialCaudal;
        double distanceToVertex = 1.0 - cranialCaudal;
        bool likelyCsf = IsLikelyCsfLike(probe);
        bool likelyPosteriorFossa = cranialCaudal <= 0.45 && signedAnteriorPosterior <= -0.08 && !likelyCsf;
        bool likelyVentricularCsf = cranialCaudal >= 0.40 && likelyCsf;

        return new ProbeKnowledgeFeatures(
            signedLeftRight,
            signedAnteriorPosterior,
            cranialCaudal,
            distanceToMidline,
            distanceToSkullBase,
            distanceToVertex,
            likelyCsf,
            likelyPosteriorFossa,
            likelyVentricularCsf);
    }

    private static string? EvaluateKnowledgePackHardRules(AnatomyStructureDefinition structure, ProbeKnowledgeFeatures features)
    {
        List<string> effectiveCompartments = GetEffectiveAllowedCompartments(structure);
        if (effectiveCompartments.Count > 0)
        {
            bool requiresSpecificCompartment = effectiveCompartments.Any(compartment =>
                !string.Equals(compartment, "intracranial_space", StringComparison.OrdinalIgnoreCase));

            bool compartmentAllowed = effectiveCompartments.Any(compartment =>
                string.Equals(compartment, "ventricular_csf", StringComparison.OrdinalIgnoreCase) && features.LikelyVentricularCsf ||
                string.Equals(compartment, "posterior_fossa", StringComparison.OrdinalIgnoreCase) && features.LikelyPosteriorFossa ||
                !requiresSpecificCompartment && string.Equals(compartment, "intracranial_space", StringComparison.OrdinalIgnoreCase));

            if (!compartmentAllowed)
            {
                return "compartment mismatch";
            }
        }

        foreach (AnatomyRelationRule relation in GetEffectiveRequiredRelations(structure))
        {
            if (!IsRequiredRelationSatisfied(relation, features))
            {
                return $"missing required relation '{relation.Type} {relation.Target}'";
            }
        }

        foreach (AnatomyRelationRule relation in GetEffectiveForbiddenRelations(structure))
        {
            if (IsForbiddenRelationViolated(relation, features))
            {
                return $"violated forbidden relation '{relation.Type} {relation.Target}'";
            }
        }

        return null;
    }

    private static double ComputeStructurePositionFit(AnatomyStructureDefinition structure, ProbeKnowledgeFeatures features)
    {
        AnatomyExpectedPosition expected = structure.ExpectedPosition;
        double fit = 0;
        int count = 0;

        AccumulateRangeFit(expected.LeftRight, features.SignedLeftRight, ref fit, ref count);
        AccumulateRangeFit(expected.AnteriorPosterior, features.SignedAnteriorPosterior, ref fit, ref count);
        AccumulateRangeFit(expected.CranialCaudal, features.CranialCaudal, ref fit, ref count);
        AccumulateRangeFit(expected.DistanceToMidline, features.DistanceToMidline, ref fit, ref count);
        AccumulateRangeFit(expected.DistanceToSkullBase, features.DistanceToSkullBase, ref fit, ref count);
        AccumulateRangeFit(expected.DistanceToVertex, features.DistanceToVertex, ref fit, ref count);

        return count == 0 ? 0.5 : fit / count;
    }

    private static double ComputeStructureMaterialFit(VolumeRoiPriorProbe probe, AnatomyStructureDefinition structure, ProbeKnowledgeFeatures features)
    {
        if (structure.ExpectedMaterial.CtClass.Count == 0)
        {
            return 0;
        }

        if (!string.Equals(probe.Modality, "CT", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        bool expectsCsf = structure.ExpectedMaterial.CtClass.Any(value => string.Equals(value, "CSF", StringComparison.OrdinalIgnoreCase));
        bool expectsParenchyma = structure.ExpectedMaterial.CtClass.Any(value => string.Equals(value, "BrainParenchyma", StringComparison.OrdinalIgnoreCase));
        if (expectsCsf)
        {
            return features.LikelyCsf ? 1 : -0.5;
        }

        if (expectsParenchyma)
        {
            return features.LikelyCsf ? -0.75 : 0.8;
        }

        return 0;
    }

    private static double ComputeStructureRelationFit(AnatomyStructureDefinition structure, ProbeKnowledgeFeatures features)
    {
        double fit = 0;
        int count = 0;

        foreach (AnatomyRelationRule relation in GetEffectiveRequiredRelations(structure))
        {
            fit += IsRequiredRelationSatisfied(relation, features) ? 1 : 0;
            count++;
        }

        foreach (AnatomyRelationRule relation in GetEffectiveForbiddenRelations(structure))
        {
            fit += IsForbiddenRelationViolated(relation, features) ? 0 : 1;
            count++;
        }

        return count == 0 ? 0.5 : fit / count;
    }

    private static bool IsRequiredRelationSatisfied(AnatomyRelationRule relation, ProbeKnowledgeFeatures features)
    {
        return relation.Type.ToLowerInvariant() switch
        {
            "left_of" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.SignedLeftRight <= -0.05,
            "right_of" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.SignedLeftRight >= 0.05,
            "near" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.DistanceToMidline <= 0.14,
            "far_from" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.DistanceToMidline >= 0.14,
            "cranial_to" when string.Equals(relation.Target, "brainstem", StringComparison.OrdinalIgnoreCase) => features.CranialCaudal >= 0.45,
            "inside" when string.Equals(relation.Target, "posterior_fossa", StringComparison.OrdinalIgnoreCase) => features.LikelyPosteriorFossa,
            "near" when string.Equals(relation.Target, "skull_base_plane", StringComparison.OrdinalIgnoreCase) => features.DistanceToSkullBase <= 0.18,
            "away_from" when string.Equals(relation.Target, "skull_base_plane", StringComparison.OrdinalIgnoreCase) => features.DistanceToSkullBase >= 0.14,
            _ => true,
        };
    }

    private static bool IsForbiddenRelationViolated(AnatomyRelationRule relation, ProbeKnowledgeFeatures features)
    {
        return relation.Type.ToLowerInvariant() switch
        {
            "inside" when string.Equals(relation.Target, "posterior_fossa", StringComparison.OrdinalIgnoreCase) => features.LikelyPosteriorFossa,
            "touches" when string.Equals(relation.Target, "skull_base_plane", StringComparison.OrdinalIgnoreCase) => features.DistanceToSkullBase <= 0.12,
            "inside" when string.Equals(relation.Target, "ventricular_csf", StringComparison.OrdinalIgnoreCase) => features.LikelyVentricularCsf,
            "near" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.DistanceToMidline <= 0.14,
            "far_from" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.DistanceToMidline >= 0.14,
            "away_from" when string.Equals(relation.Target, "skull_base_plane", StringComparison.OrdinalIgnoreCase) => features.DistanceToSkullBase >= 0.14,
            "left_of" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.SignedLeftRight <= -0.05,
            "right_of" when string.Equals(relation.Target, "midline", StringComparison.OrdinalIgnoreCase) => features.SignedLeftRight >= 0.05,
            _ => false,
        };
    }

    private static List<string> GetEffectiveAllowedCompartments(AnatomyStructureDefinition structure)
    {
        List<string> compartments = structure.AllowedCompartments
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string identity = GetStructureIdentityText(structure);
        if (identity.Contains("ventricle"))
        {
            MergeCompartment(compartments, "ventricular_csf");
        }

        if (identity.Contains("pons") || identity.Contains("brainstem") || identity.Contains("cerebell") || identity.Contains("vermis"))
        {
            MergeCompartment(compartments, "posterior_fossa");
        }

        if (compartments.Count == 0)
        {
            compartments.Add("intracranial_space");
        }

        return compartments;
    }

    private static IReadOnlyList<AnatomyRelationRule> GetEffectiveRequiredRelations(AnatomyStructureDefinition structure)
    {
        List<AnatomyRelationRule> relations = CloneRelations(structure.RequiredRelations);
        string identity = GetStructureIdentityText(structure);

        if (identity.Contains("pons") || identity.Contains("brainstem"))
        {
            AddRelation(relations, "inside", "posterior_fossa", "hard");
            AddRelation(relations, "near", "skull_base_plane", "hard");
            AddRelation(relations, "near", "midline", "hard");
        }
        else if (identity.Contains("vermis"))
        {
            AddRelation(relations, "inside", "posterior_fossa", "hard");
            AddRelation(relations, "near", "midline", "hard");
            AddRelation(relations, "away_from", "skull_base_plane", "soft");
        }
        else if (identity.Contains("thalam") || identity.Contains("caudate"))
        {
            AddRelation(relations, "cranial_to", "brainstem", "hard");
            AddRelation(relations, "away_from", "skull_base_plane", "soft");

            if (identity.Contains("left"))
            {
                AddRelation(relations, "left_of", "midline", "hard");
            }
            else if (identity.Contains("right"))
            {
                AddRelation(relations, "right_of", "midline", "hard");
            }
        }
        else if (identity.Contains("lenticular") || identity.Contains("putamen") || identity.Contains("pallidus"))
        {
            AddRelation(relations, "far_from", "midline", "hard");

            if (identity.Contains("left"))
            {
                AddRelation(relations, "left_of", "midline", "hard");
            }
            else if (identity.Contains("right"))
            {
                AddRelation(relations, "right_of", "midline", "hard");
            }
        }
        else if (identity.Contains("left") && identity.Contains("cerebell"))
        {
            AddRelation(relations, "inside", "posterior_fossa", "hard");
            AddRelation(relations, "left_of", "midline", "hard");
            AddRelation(relations, "far_from", "midline", "hard");
        }
        else if (identity.Contains("right") && identity.Contains("cerebell"))
        {
            AddRelation(relations, "inside", "posterior_fossa", "hard");
            AddRelation(relations, "right_of", "midline", "hard");
            AddRelation(relations, "far_from", "midline", "hard");
        }
        else if (identity.Contains("cerebell"))
        {
            AddRelation(relations, "inside", "posterior_fossa", "hard");
        }

        string? lateralityRelation = GetImplicitLateralityRelation(identity);
        if (!string.IsNullOrWhiteSpace(lateralityRelation))
        {
            AddRelation(relations, lateralityRelation, "midline", "hard");
        }

        return relations;
    }

    private static IReadOnlyList<AnatomyRelationRule> GetEffectiveForbiddenRelations(AnatomyStructureDefinition structure)
    {
        List<AnatomyRelationRule> relations = CloneRelations(structure.ForbiddenRelations);
        string identity = GetStructureIdentityText(structure);

        if (identity.Contains("pons") || identity.Contains("brainstem"))
        {
            AddRelation(relations, "far_from", "midline", "hard");
        }
        else if (identity.Contains("vermis"))
        {
            AddRelation(relations, "left_of", "midline", "hard");
            AddRelation(relations, "right_of", "midline", "hard");
            AddRelation(relations, "near", "skull_base_plane", "soft");
        }
        else if (identity.Contains("thalam") || identity.Contains("caudate"))
        {
            AddRelation(relations, "inside", "posterior_fossa", "hard");
            AddRelation(relations, "touches", "skull_base_plane", "hard");

            if ((identity.Contains("left") || identity.Contains("right")) && !identity.Contains("ventricle"))
            {
                AddRelation(relations, "near", "midline", "soft");
            }
        }
        else if (identity.Contains("lenticular") || identity.Contains("putamen") || identity.Contains("pallidus"))
        {
            AddRelation(relations, "inside", "posterior_fossa", "hard");
            AddRelation(relations, "touches", "skull_base_plane", "hard");
            AddRelation(relations, "near", "midline", "hard");
        }
        else if ((identity.Contains("left") || identity.Contains("right")) && identity.Contains("cerebell"))
        {
            AddRelation(relations, "near", "midline", "hard");
            AddRelation(relations, "near", "skull_base_plane", "soft");
        }

        return relations;
    }

    private static List<AnatomyRelationRule> CloneRelations(IEnumerable<AnatomyRelationRule> relations)
    {
        return relations
            .Where(relation => relation is not null)
            .Select(relation => new AnatomyRelationRule
            {
                Type = relation.Type,
                Target = relation.Target,
                Strength = relation.Strength,
            })
            .ToList();
    }

    private static void AddRelation(List<AnatomyRelationRule> relations, string type, string target, string strength)
    {
        bool exists = relations.Any(existing =>
            string.Equals(existing.Type, type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Target, target, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            relations.Add(new AnatomyRelationRule { Type = type, Target = target, Strength = strength });
        }
    }

    private static void MergeCompartment(List<string> compartments, string compartment)
    {
        if (!compartments.Contains(compartment, StringComparer.OrdinalIgnoreCase))
        {
            compartments.Add(compartment);
        }
    }

    private static string GetStructureIdentityText(AnatomyStructureDefinition structure)
    {
        return $"{structure.Id} {structure.DisplayName}".Trim().ToLowerInvariant();
    }

    private static string? GetImplicitLateralityRelation(string identity)
    {
        bool hasLeft = IdentityContainsWord(identity, "left");
        bool hasRight = IdentityContainsWord(identity, "right");
        bool hasBilateral = IdentityContainsWord(identity, "bilateral") || IdentityContainsWord(identity, "both");
        if (hasBilateral || hasLeft == hasRight)
        {
            return null;
        }

        return hasLeft ? "left_of" : "right_of";
    }

    private static bool IdentityContainsWord(string identity, string word)
    {
        string[] tokens = identity.Split([' ', '\t', '\r', '\n', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', ',', ';', '.', ':'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    private static void AccumulateRangeFit(AnatomyNumericRange range, double value, ref double fit, ref int count)
    {
        if (range.Min is null && range.Max is null)
        {
            return;
        }

        fit += ComputeRangeFit(range, value);
        count++;
    }

    private static double ComputeRangeFit(AnatomyNumericRange range, double value)
    {
        if (range.Min is null && range.Max is null)
        {
            return 0;
        }

        if (range.Min is not null && value < range.Min.Value)
        {
            return Math.Max(0, 1.0 - ((range.Min.Value - value) * 4.0));
        }

        if (range.Max is not null && value > range.Max.Value)
        {
            return Math.Max(0, 1.0 - ((value - range.Max.Value) * 4.0));
        }

        return 1.0;
    }

    private static bool IsLikelyCsfLike(VolumeRoiPriorProbe probe)
    {
        double intensityMedian = probe.StructureSignature?.IntensityMedian ?? double.NaN;
        double intensitySpread = probe.StructureSignature?.IntensitySpread ?? double.NaN;
        if (double.IsNaN(intensityMedian))
        {
            return false;
        }

        if (string.Equals(probe.Modality, "CT", StringComparison.OrdinalIgnoreCase))
        {
            return intensityMedian >= -15 && intensityMedian <= 24 && (double.IsNaN(intensitySpread) || intensitySpread <= 24);
        }

        return false;
    }

    private double ScoreSeriesRegionPrior(string modality, string bodyPart, string context, VolumeRoiAnatomyPriorRecord prior)
    {
        double score = 0;
        if (!string.IsNullOrWhiteSpace(modality) && string.Equals(modality, prior.Modality, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.22;
        }

        score += 0.18 * ComputeBodyPartSimilarity(bodyPart, prior.BodyPartExamined);
        score += 0.34 * ComputeContextTokenOverlap(context, $"{prior.StudyDescription} {prior.SeriesDescription} {prior.BodyPartExamined}");
        score += Math.Min(0.08, Math.Max(0, (prior.UseCount - 1) * 0.01));
        return score;
    }

    private static bool IsProbeCenterWithinPrior(VolumeRoiPriorProbe probe, VolumeRoiAnatomyPriorRecord prior)
    {
        double toleranceX = Math.Max(0.035, prior.NormalizedSizeX * 0.5 + probe.NormalizedSizeX * 0.5);
        double toleranceY = Math.Max(0.035, prior.NormalizedSizeY * 0.5 + probe.NormalizedSizeY * 0.5);
        double toleranceZ = Math.Max(0.035, prior.NormalizedSizeZ * 0.5 + probe.NormalizedSizeZ * 0.5);

        return Math.Abs(probe.NormalizedCenterX - prior.NormalizedCenterX) <= toleranceX &&
               Math.Abs(probe.NormalizedCenterY - prior.NormalizedCenterY) <= toleranceY &&
               Math.Abs(probe.NormalizedCenterZ - prior.NormalizedCenterZ) <= toleranceZ;
    }

    private static double ComputeContextTokenOverlap(string left, string right)
    {
        HashSet<string> leftTokens = TokenizeContext(left);
        HashSet<string> rightTokens = TokenizeContext(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        int matches = leftTokens.Count(token => rightTokens.Contains(token));
        return (double)matches / Math.Max(leftTokens.Count, rightTokens.Count);
    }

    private static double ComputeBodyPartSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return ComputeContextTokenOverlap(left, right);
    }

    private static HashSet<string> TokenizeContext(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        string[] tokens = value
            .Split([' ', '\\', '/', '-', '_', ',', ';', '.', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string GetReviewStateSelectionValue(Guid measurementId) =>
        _reportReviewStates.TryGetValue(measurementId, out string? state) && !string.IsNullOrWhiteSpace(state)
            ? state
            : "Auto";

    private void ApplyReviewStateSelection(Guid measurementId, string selectedValue)
    {
        if (string.Equals(selectedValue, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(selectedValue))
        {
            _reportReviewStates.Remove(measurementId);
        }
        else
        {
            _reportReviewStates[measurementId] = selectedValue.Trim();
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: true);
    }

    private static string GetReviewStateColor(string reviewState) => reviewState switch
    {
        "Confirmed" => "#FF7FDFA2",
        "Needs review" => "#FFF5B267",
        _ => "#FFA8C0D8",
    };

    private SpatialBounds? GetSpatialBounds(SeriesVolume? volume)
    {
        if (volume is null)
        {
            return null;
        }

        SpatialVector3D[] corners =
        [
            volume.VoxelToPatient(0, 0, 0),
            volume.VoxelToPatient(volume.SizeX - 1, 0, 0),
            volume.VoxelToPatient(0, volume.SizeY - 1, 0),
            volume.VoxelToPatient(0, 0, volume.SizeZ - 1),
            volume.VoxelToPatient(volume.SizeX - 1, volume.SizeY - 1, 0),
            volume.VoxelToPatient(volume.SizeX - 1, 0, volume.SizeZ - 1),
            volume.VoxelToPatient(0, volume.SizeY - 1, volume.SizeZ - 1),
            volume.VoxelToPatient(volume.SizeX - 1, volume.SizeY - 1, volume.SizeZ - 1),
        ];

        return new SpatialBounds(
            corners.Min(point => point.X),
            corners.Max(point => point.X),
            corners.Min(point => point.Y),
            corners.Max(point => point.Y),
            corners.Min(point => point.Z),
            corners.Max(point => point.Z));
    }

    private ProbeAxisCorrection? TryBuildProbeAxisCorrection(SeriesVolume? volume, SpatialVector3D patientCenter)
    {
        if (volume is null)
        {
            return null;
        }

        HeadAxisCorrectionModel model = GetOrCreateHeadAxisCorrectionModel(volume);
        if (!model.IsReliable)
        {
            return null;
        }

        (double voxelX, double voxelY, double voxelZ) = volume.PatientToVoxel(new SpatialVector3D(patientCenter.X, patientCenter.Y, patientCenter.Z));
        double expectedCenterX = model.CenterlineInterceptX + (model.CenterlineSlopeX * voxelZ);
        double expectedCenterY = model.CenterlineInterceptY + (model.CenterlineSlopeY * voxelZ);
        double offsetX = voxelX - expectedCenterX;
        double offsetY = voxelY - expectedCenterY;

        double lrProjection = (offsetX * model.LrAxisX) + (offsetY * model.LrAxisY);
        double apProjection = (offsetX * model.ApAxisX) + (offsetY * model.ApAxisY);

        double signedLeftRight = Math.Clamp((lrProjection / model.LrHalfExtent) * model.LrSign, -1.0, 1.0);
        double signedAnteriorPosterior = Math.Clamp((apProjection / model.ApHalfExtent) * model.ApSign, -1.0, 1.0);
        double centerlineShiftX = expectedCenterX - ((volume.SizeX - 1) * 0.5);
        double centerlineShiftY = expectedCenterY - ((volume.SizeY - 1) * 0.5);

        return new ProbeAxisCorrection(
            signedLeftRight,
            signedAnteriorPosterior,
            Math.Abs(signedLeftRight),
            model.RotationDegrees,
            Math.Clamp(centerlineShiftX / Math.Max(1.0, volume.SizeX * 0.5), -1.0, 1.0),
            Math.Clamp(centerlineShiftY / Math.Max(1.0, volume.SizeY * 0.5), -1.0, 1.0));
    }

    private HeadAxisCorrectionModel GetOrCreateHeadAxisCorrectionModel(SeriesVolume volume)
    {
        if (!string.IsNullOrWhiteSpace(volume.SeriesInstanceUid) &&
            _headAxisCorrectionCache.TryGetValue(volume.SeriesInstanceUid, out HeadAxisCorrectionModel? cached))
        {
            return cached;
        }

        HeadAxisCorrectionModel model = BuildHeadAxisCorrectionModel(volume);
        if (!string.IsNullOrWhiteSpace(volume.SeriesInstanceUid))
        {
            _headAxisCorrectionCache[volume.SeriesInstanceUid] = model;
        }

        return model;
    }

    private static HeadAxisCorrectionModel BuildHeadAxisCorrectionModel(SeriesVolume volume)
    {
        int width = volume.SizeX;
        int height = volume.SizeY;
        int depth = volume.SizeZ;
        if (width < 16 || height < 16 || depth < 4)
        {
            return HeadAxisCorrectionModel.Unreliable;
        }

        int stepX = Math.Max(1, width / 96);
        int stepY = Math.Max(1, height / 96);
        int stepZ = Math.Max(1, depth / 48);
        int sampledPixelsPerSlice = Math.Max(1, ((width + stepX - 1) / stepX) * ((height + stepY - 1) / stepY));
        double threshold = volume.MinValue + ((volume.MaxValue - volume.MinValue) * 0.12);

        List<HeadSliceSample> samples = [];
        double aggregatedCovXX = 0;
        double aggregatedCovYY = 0;
        double aggregatedCovXY = 0;
        double totalWeight = 0;

        for (int sliceIndex = 0; sliceIndex < depth; sliceIndex += stepZ)
        {
            long tissueCount = 0;
            double sumX = 0;
            double sumY = 0;
            double sumXX = 0;
            double sumYY = 0;
            double sumXY = 0;

            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    if (volume.GetVoxel(x, y, sliceIndex) <= threshold)
                    {
                        continue;
                    }

                    tissueCount++;
                    sumX += x;
                    sumY += y;
                    sumXX += x * x;
                    sumYY += y * y;
                    sumXY += x * y;
                }
            }

            if (tissueCount < 12)
            {
                continue;
            }

            double occupancy = tissueCount / (double)sampledPixelsPerSlice;
            if (occupancy < 0.06)
            {
                continue;
            }

            double centerX = sumX / tissueCount;
            double centerY = sumY / tissueCount;
            double covXX = Math.Max(0, (sumXX / tissueCount) - (centerX * centerX));
            double covYY = Math.Max(0, (sumYY / tissueCount) - (centerY * centerY));
            double covXY = (sumXY / tissueCount) - (centerX * centerY);
            double weight = tissueCount;

            samples.Add(new HeadSliceSample(sliceIndex, centerX, centerY, weight));
            aggregatedCovXX += covXX * weight;
            aggregatedCovYY += covYY * weight;
            aggregatedCovXY += covXY * weight;
            totalWeight += weight;
        }

        if (samples.Count < 4 || totalWeight <= 0)
        {
            return HeadAxisCorrectionModel.Unreliable;
        }

        double meanCovXX = aggregatedCovXX / totalWeight;
        double meanCovYY = aggregatedCovYY / totalWeight;
        double meanCovXY = aggregatedCovXY / totalWeight;
        double orientation = 0.5 * Math.Atan2(2.0 * meanCovXY, meanCovXX - meanCovYY);

        double majorAxisX = Math.Cos(orientation);
        double majorAxisY = Math.Sin(orientation);
        double minorAxisX = -majorAxisY;
        double minorAxisY = majorAxisX;

        double lrPatientX = (volume.RowDirection.X * minorAxisX * volume.SpacingX) + (volume.ColumnDirection.X * minorAxisY * volume.SpacingY);
        double apPatientY = (volume.RowDirection.Y * majorAxisX * volume.SpacingX) + (volume.ColumnDirection.Y * majorAxisY * volume.SpacingY);
        double lrSign = lrPatientX >= 0 ? -1.0 : 1.0;
        double apSign = apPatientY >= 0 ? -1.0 : 1.0;

        (double slopeX, double interceptX) = ComputeWeightedLinearFit(samples, sample => sample.SliceZ, sample => sample.CenterX, sample => sample.Weight);
        (double slopeY, double interceptY) = ComputeWeightedLinearFit(samples, sample => sample.SliceZ, sample => sample.CenterY, sample => sample.Weight);

        double maxAbsMinor = 0;
        double maxAbsMajor = 0;
        foreach (HeadSliceSample sample in samples)
        {
            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    if (volume.GetVoxel(x, y, sample.SliceZ) <= threshold)
                    {
                        continue;
                    }

                    double dx = x - sample.CenterX;
                    double dy = y - sample.CenterY;
                    double minorProjection = (dx * minorAxisX) + (dy * minorAxisY);
                    double majorProjection = (dx * majorAxisX) + (dy * majorAxisY);
                    maxAbsMinor = Math.Max(maxAbsMinor, Math.Abs(minorProjection));
                    maxAbsMajor = Math.Max(maxAbsMajor, Math.Abs(majorProjection));
                }
            }
        }

        double lrHalfExtent = Math.Max(6.0, maxAbsMinor);
        double apHalfExtent = Math.Max(6.0, maxAbsMajor);
        double rotationDegrees = orientation * (180.0 / Math.PI);
        bool reliable = lrHalfExtent >= 8.0 && apHalfExtent >= 8.0;

        return new HeadAxisCorrectionModel(
            slopeX,
            interceptX,
            slopeY,
            interceptY,
            minorAxisX,
            minorAxisY,
            majorAxisX,
            majorAxisY,
            lrHalfExtent,
            apHalfExtent,
            lrSign,
            apSign,
            rotationDegrees,
            reliable);
    }

    private static (double Slope, double Intercept) ComputeWeightedLinearFit(
        IReadOnlyList<HeadSliceSample> samples,
        Func<HeadSliceSample, double> xSelector,
        Func<HeadSliceSample, double> ySelector,
        Func<HeadSliceSample, double> weightSelector)
    {
        double sumW = 0;
        double sumX = 0;
        double sumY = 0;
        double sumXX = 0;
        double sumXY = 0;

        foreach (HeadSliceSample sample in samples)
        {
            double weight = Math.Max(1.0, weightSelector(sample));
            double x = xSelector(sample);
            double y = ySelector(sample);
            sumW += weight;
            sumX += weight * x;
            sumY += weight * y;
            sumXX += weight * x * x;
            sumXY += weight * x * y;
        }

        if (sumW <= 0)
        {
            return (0, 0);
        }

        double denominator = (sumW * sumXX) - (sumX * sumX);
        if (Math.Abs(denominator) <= 1e-6)
        {
            return (0, sumY / sumW);
        }

        double slope = ((sumW * sumXY) - (sumX * sumY)) / denominator;
        double intercept = (sumY - (slope * sumX)) / sumW;
        return (slope, intercept);
    }

    private void OnReportPanelPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _reportPanelPinned = ReportPanelPinButton.IsChecked == true;
        if (_reportPanelPinned)
        {
            _reportPanelVisible = true;
        }

        SaveViewerSettings();
        ScheduleMeasurementSessionSave();
        RefreshReportPanel(forceVisible: _reportPanelPinned);
        e.Handled = true;
    }

    private void OnReportPanelHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReportPanel.IsVisible || !e.GetCurrentPoint(ReportPanelDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _reportPanelDragPointer = e.Pointer;
        _reportPanelDragPointer.Capture(ReportPanelDragHandle);
        _reportPanelDragStart = e.GetPosition(ViewerContentHost);
        _reportPanelDragStartOffset = _reportPanelOffset;
        e.Handled = true;
    }

    private void OnReportPanelHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_reportPanelDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _reportPanelDragStart;
        _reportPanelOffset = new Point(
            _reportPanelDragStartOffset.X + delta.X,
            _reportPanelDragStartOffset.Y + delta.Y);
        ApplyReportPanelOffset();
        e.Handled = true;
    }

    private void OnReportPanelHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_reportPanelDragPointer, e.Pointer))
        {
            return;
        }

        _reportPanelDragPointer.Capture(null);
        _reportPanelDragPointer = null;
        ApplyReportPanelOffset();
        SaveViewerSettings();
        e.Handled = true;
    }

    private void ApplyReportPanelOffset()
    {
        if (ReportPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureReportPanelTransform();
        double panelWidth = ReportPanel.Bounds.Width;
        double panelHeight = ReportPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = ReportPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _reportPanelOffset.X;
            transform.Y = _reportPanelOffset.Y;
            return;
        }

        double defaultLeft = Math.Max(0, hostWidth - panelWidth - margin.Right);
        double defaultTop = margin.Top;
        double defaultBottom = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double overflowX = GetFloatingPanelOverflowAllowance(panelWidth);
        double overflowY = GetFloatingPanelOverflowAllowance(panelHeight);
        double clampedX = Math.Clamp(_reportPanelOffset.X, -defaultLeft - overflowX, overflowX);
        double clampedY = Math.Clamp(_reportPanelOffset.Y, -defaultTop - overflowY, defaultBottom + overflowY);
        _reportPanelOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureReportPanelTransform()
    {
        if (ReportPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        ReportPanel.RenderTransform = transform;
        return transform;
    }

    private string ResolveStudyLabel(SeriesRecord? series)
    {
        if (series is null)
        {
            return _isShowingCurrentStudy ? "Current study" : "Comparison study";
        }

        bool isCurrentStudy = _context.StudyDetails.Series.Any(candidate =>
            string.Equals(candidate.SeriesInstanceUid, series.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase));
        if (isCurrentStudy)
        {
            return "Current study";
        }

        if (_selectedPriorStudy is not null)
        {
            return $"Comparison study · {_selectedPriorStudy.DisplayLabel}";
        }

        return "Comparison study";
    }

    private static string BuildSeriesLabel(SeriesRecord series)
    {
        string description = series.SeriesDescription?.Trim() ?? string.Empty;
        string prefix = $"Series {series.SeriesNumber}";
        if (!string.IsNullOrWhiteSpace(series.Modality))
        {
            prefix = $"{prefix} · {series.Modality}";
        }

        return string.IsNullOrWhiteSpace(description)
            ? prefix
            : $"{prefix} · {description}";
    }

    private static string GetMeasurementTypeLabel(StudyMeasurement measurement) => measurement.Kind switch
    {
        MeasurementKind.Line => "Measurement",
        MeasurementKind.Angle => "Angle",
        MeasurementKind.Annotation => "Annotation",
        MeasurementKind.RectangleRoi => "Rectangle ROI",
        MeasurementKind.EllipseRoi => "Ellipse ROI",
        MeasurementKind.PolygonRoi => "Polygon ROI",
        MeasurementKind.VolumeRoi => "3D ROI",
        _ => "Finding",
    };

    private static string GetAccentColor(string typeLabel) => typeLabel switch
    {
        "Annotation" => "#FFF5B267",
        "Measurement" => "#FF7FC8F8",
        "Angle" => "#FF7FC8F8",
        "Rectangle ROI" => "#FF7FDFA2",
        "Ellipse ROI" => "#FF7FDFA2",
        "Polygon ROI" => "#FF7FDFA2",
        "3D ROI" => "#FF56D3C2",
        _ => "#FFA8C0D8",
    };

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (string needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CombineReportHints(string regionHint, string anatomyHint)
    {
        if (string.IsNullOrWhiteSpace(regionHint))
        {
            return anatomyHint;
        }

        if (string.IsNullOrWhiteSpace(anatomyHint))
        {
            return regionHint;
        }

        return string.Equals(regionHint, anatomyHint, StringComparison.Ordinal)
            ? regionHint
            : $"{regionHint} {anatomyHint}";
    }

    private static double Normalize(double value, double minimum, double maximum)
    {
        if (maximum - minimum <= double.Epsilon)
        {
            return 0.5;
        }

        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private sealed record ReportEntry(
        StudyViewerWindow SourceWindow,
        Guid? MeasurementId,
        string Category,
        string Title,
        string Origin,
        string Region,
        string AutoRegion,
        bool IsRegionManual,
        string Anatomy,
        string Details,
        string Hint,
        bool IsSelected,
        string AccentHex,
        bool IsRegionLearned,
        double RegionConfidence,
        bool IsAnatomyLearned,
        double AnatomyConfidence,
        bool IsFromReportStudy,
        bool IsRegionEditable,
        bool IsAnatomyManual,
        bool IsAnatomyEditable,
        IReadOnlyList<string> AnatomyOptions,
        string ReviewState,
        int SortOrder);

    private sealed record ReportMeasurementContext(
        StudyViewerWindow SourceWindow,
        StudyMeasurement Measurement,
        ViewportSlot? Slot);

    private sealed record VolumeRoiPriorProbe(
        string Modality,
        string BodyPartExamined,
        string StudyDescription,
        string SeriesDescription,
        double NormalizedCenterX,
        double NormalizedCenterY,
        double NormalizedCenterZ,
        double NormalizedSizeX,
        double NormalizedSizeY,
        double NormalizedSizeZ,
        double EstimatedVolumeCubicMillimeters,
        AnatomyStructureSignature? StructureSignature,
        ProbeAxisCorrection? AxisCorrection)
    {
        public string ContextText => $"{StudyDescription} {SeriesDescription} {BodyPartExamined}".Trim();
    }

    private sealed record KnowledgePackPriorAssessment(
        bool Rejected,
        double ScoreAdjustment,
        string Hint,
        string DebugSummary,
        string? RejectionReason,
        double PositionFit,
        double MaterialFit,
        double RelationFit)
    {
        public static KnowledgePackPriorAssessment Neutral { get; } = new(false, 0, string.Empty, string.Empty, null, 0, 0, 0);
    }

    private sealed record MatchCandidateDebugInfo(
        VolumeRoiAnatomyPriorRecord Prior,
        double BaseScore,
        double FinalScore,
        KnowledgePackPriorAssessment Assessment);

    private sealed record PackStructureMatch(
        AnatomyStructureDefinition Structure,
        double Score,
        string Hint,
        KnowledgePackPriorAssessment Assessment);

    private readonly record struct ProbeKnowledgeFeatures(
        double SignedLeftRight,
        double SignedAnteriorPosterior,
        double CranialCaudal,
        double DistanceToMidline,
        double DistanceToSkullBase,
        double DistanceToVertex,
        bool LikelyCsf,
        bool LikelyPosteriorFossa,
        bool LikelyVentricularCsf);

    private readonly record struct ProbeAxisCorrection(
        double SignedLeftRight,
        double SignedAnteriorPosterior,
        double DistanceToMidline,
        double RotationDegrees,
        double CenterlineShiftX,
        double CenterlineShiftY);

    private readonly record struct HeadSliceSample(
        int SliceZ,
        double CenterX,
        double CenterY,
        double Weight);

    private sealed record HeadAxisCorrectionModel(
        double CenterlineSlopeX,
        double CenterlineInterceptX,
        double CenterlineSlopeY,
        double CenterlineInterceptY,
        double LrAxisX,
        double LrAxisY,
        double ApAxisX,
        double ApAxisY,
        double LrHalfExtent,
        double ApHalfExtent,
        double LrSign,
        double ApSign,
        double RotationDegrees,
        bool IsReliable)
    {
        public static HeadAxisCorrectionModel Unreliable { get; } = new(
            0,
            0,
            0,
            0,
            1,
            0,
            0,
            1,
            1,
            1,
            1,
            1,
            0,
            false);
    }

    private sealed record RegionGuess(
        string Label,
        string AutoLabel,
        string Hint,
        bool IsManual = false,
        bool IsLearned = false,
        double Confidence = 0);

    private sealed record AnatomyGuess(
        string Label,
        string Hint,
        bool IsManual = false,
        bool IsLearned = false,
        double Confidence = 0)
    {
        public string DisplayLabel => IsManual || string.Equals(Label, "Unassigned", StringComparison.OrdinalIgnoreCase)
            ? Label
            : $"Likely {Label}";
    }

    private readonly record struct SpatialBounds(
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double MinZ,
        double MaxZ);

    private enum AnatomyRegionProfile
    {
        Unknown,
        Neuro,
        Thorax,
        Abdomen,
        Pelvis,
        Shoulder,
        Knee,
        Ankle,
        ThoraxAbdomen,
        AbdomenPelvis,
    }

    private sealed record AnatomyProfileGuess(AnatomyRegionProfile Profile, string Hint);
}