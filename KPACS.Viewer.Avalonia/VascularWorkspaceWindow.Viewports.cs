using Avalonia.Controls;
using Avalonia.Layout;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer;

/// <summary>
/// Viewport-Verwaltung des Vascular Workspace (Phase B1-Gerüst).
/// Bindet die übergebene Serie an DVR- und MPR-Viewport; CPR- und
/// Orthogonalschnitt-Viewport werden in späteren Phasen gefüllt.
/// </summary>
public partial class VascularWorkspaceWindow
{
    private DicomViewPanel? _dvrPanel;
    private DicomViewPanel? _mprPanel;
    private IRenderBackend? _dvrBackend;
    private IRenderBackend? _mprBackend;

    private void InitializeViewports()
    {
        if (_volume is null)
        {
            SidebarBody.Text = "Keine Serie geladen. Öffnen Sie den Vascular Workspace über die " +
                "Toolbar des Study Viewers mit einer aktiven Serie.";
            return;
        }

        _dvrPanel = CreatePanel();
        DvrHost.Content = _dvrPanel;

        _mprPanel = CreatePanel();
        MprHost.Content = _mprPanel;

        BindVolumeToViewports();
    }

    private static DicomViewPanel CreatePanel() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        ShowOverlay = true,
    };

    private void BindVolumeToViewports()
    {
        if (_volume is null)
        {
            return;
        }

        int mid = Math.Max(0, VolumeReslicer.GetSliceCount(_volume, SliceOrientation.Axial) / 2);

        if (_dvrPanel is not null)
        {
            _dvrBackend = new LocalRenderBackend(_volume);
            _dvrPanel.BindVolumeWithBackend(_volume, _dvrBackend, SliceOrientation.Axial, mid);
            _dvrPanel.SetProjectionMode(VolumeProjectionMode.Dvr);
            _dvrPanel.SetDvrPreset(TransferFunctionPreset.Angio);
        }

        if (_mprPanel is not null)
        {
            _mprBackend = new LocalRenderBackend(_volume);
            _mprPanel.BindVolumeWithBackend(_volume, _mprBackend, SliceOrientation.Axial, mid);
            _mprPanel.SetProjectionMode(VolumeProjectionMode.Mpr);
        }
    }

    /// <summary>
    /// Setzt den 3D-Cursor in allen Viewports (Platzhalter; Vollausbau in Phase B/C
    /// nach Muster <c>SyncCenterlineCrossSectionCursor</c> mit 16ms-Throttle).
    /// </summary>
    private void Broadcast3DCursor()
    {
        // Phase B1: Gerüst ohne Cursor-Logik.
    }
}
