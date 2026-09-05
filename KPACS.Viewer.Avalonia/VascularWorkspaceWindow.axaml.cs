using Avalonia.Controls;
using Avalonia.Interactivity;
using KPACS.SDK.Models;
using KPACS.Viewer.Rendering;
using System.Threading.Tasks;

namespace KPACS.Viewer;

/// <summary>
/// Dedizierter, geführter Vascular Workspace (EVAR/TEVAR und TAVI).
/// Eigenes Fenster mit Modus-Umschalter, Schritt-Leiste, 2x2-Viewport-Grid und
/// Sidebar des aktiven Schritts. Gerüst aus Phase B1; die Schritt-Inhalte werden
/// in den <c>.Steps.*</c>-Partials gefüllt.
/// </summary>
public partial class VascularWorkspaceWindow : Window
{
    private readonly SeriesVolume? _volume;
    private readonly string _seriesTitle;
    private VascularWorkspaceMode _mode = VascularWorkspaceMode.Evar;
    private int _currentStep;

    /// <summary>
    /// Optional auto-segmentation entry point supplied by the owning
    /// <see cref="StudyViewerWindow"/> (Phase B2). Runs TotalSegmentator on the given
    /// volume and returns the aorta + iliac-artery masks. Null when the workspace was
    /// opened without a host (e.g. designer), in which case the Auto button is disabled.
    /// </summary>
    public Func<SeriesVolume, IProgress<ProgressReport>, CancellationToken, Task<IReadOnlyList<Models.SegmentationMask3D>>>? AutoSegmentationRunner { get; init; }

    /// <summary>
    /// Optional callback invoked whenever the workspace's planning state changes, so the
    /// owning <see cref="StudyViewerWindow"/> can persist a <see cref="Models.VascularWorkspaceSnapshot"/>
    /// in the measurement-session envelope (Phase E). Null when opened without a host.
    /// </summary>
    public Action<Models.VascularWorkspaceSnapshot>? SnapshotChanged { get; init; }

    /// <summary>Designer-/parameterloser Konstruktor (leeres Fenster).</summary>
    public VascularWorkspaceWindow()
        : this(null, string.Empty)
    {
    }

    public VascularWorkspaceWindow(SeriesVolume? volume, string seriesTitle)
    {
        InitializeComponent();
        _volume = volume;
        _seriesTitle = string.IsNullOrWhiteSpace(seriesTitle) ? "Aktive Serie" : seriesTitle;
        InitializeViewports();
        UpdateModeUi();
        SelectStep(_mode, 0);
        UpdateStatus();
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _mode = ModeTaviButton.IsChecked == true ? VascularWorkspaceMode.Tavi : VascularWorkspaceMode.Evar;
        UpdateModeUi();
        SelectStep(_mode, 0);
        UpdateStatus();
    }

    private void OnEvarStepChanged(object? sender, RoutedEventArgs e)
    {
        if (IsLoaded && _mode == VascularWorkspaceMode.Evar)
        {
            _currentStep = StepIndexFromSender(sender);
            OnStepActivated();
            UpdateStatus();
        }
    }

    private void OnTaviStepChanged(object? sender, RoutedEventArgs e)
    {
        if (IsLoaded && _mode == VascularWorkspaceMode.Tavi)
        {
            _currentStep = StepIndexFromSender(sender);
            OnStepActivated();
            UpdateStatus();
        }
    }

    private void OnToggleChartStripClick(object? sender, RoutedEventArgs e)
    {
        ChartStrip.IsVisible = !ChartStrip.IsVisible;
    }

    private int StepIndexFromSender(object? sender)
    {
        if (ReferenceEquals(sender, EvarStep2) || ReferenceEquals(sender, TaviStep2))
        {
            return 1;
        }

        if (ReferenceEquals(sender, EvarStep3) || ReferenceEquals(sender, TaviStep3))
        {
            return 2;
        }

        if (ReferenceEquals(sender, EvarStep4) || ReferenceEquals(sender, TaviStep4))
        {
            return 3;
        }

        if (ReferenceEquals(sender, EvarStep5))
        {
            return 4;
        }

        return 0;
    }

    private void UpdateModeUi()
    {
        bool tavi = _mode == VascularWorkspaceMode.Tavi;
        EvarStepBar.IsVisible = !tavi;
        TaviStepBar.IsVisible = tavi;
        WorkspaceSubtitleText.Text = tavi
            ? "Structural-Heart-Planung (TAVI)"
            : "Geführte Gefäß-Planung (EVAR/TEVAR)";
    }

    private void SelectStep(VascularWorkspaceMode mode, int index)
    {
        _currentStep = index;
        RadioButton target = mode switch
        {
            VascularWorkspaceMode.Tavi => index switch
            {
                1 => TaviStep2,
                2 => TaviStep3,
                3 => TaviStep4,
                _ => TaviStep1,
            },
            _ => index switch
            {
                1 => EvarStep2,
                2 => EvarStep3,
                3 => EvarStep4,
                4 => EvarStep5,
                _ => EvarStep1,
            },
        };
        target.IsChecked = true;
        OnStepActivated();
    }

    private void UpdateStatus()
    {
        string mode = _mode == VascularWorkspaceMode.Tavi ? "TAVI" : "EVAR/TEVAR";
        string step = StepName(_mode, _currentStep);
        StatusText.Text = $"{mode} · Schritt {_currentStep + 1}: {step} · {_seriesTitle}";
    }

    private static string StepName(VascularWorkspaceMode mode, int index) => mode switch
    {
        VascularWorkspaceMode.Tavi => index switch
        {
            0 => "Annulus",
            1 => "Double-Oblique / Verkalkung",
            2 => "Ostien / C-Arm",
            _ => "Sizing / Bericht",
        },
        _ => index switch
        {
            0 => "Segmentierung",
            1 => "Centerline",
            2 => "Messungen",
            3 => "Sizing",
            _ => "Bericht",
        },
    };
}

/// <summary>Arbeitsmodus des Vascular Workspace.</summary>
public enum VascularWorkspaceMode
{
    Evar,
    Tavi,
}
