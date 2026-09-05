using Avalonia.Controls;
using Avalonia.Interactivity;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer;

/// <summary>
/// Einstiegspunkt in den dedizierten Vascular Workspace aus dem Study Viewer
/// (Toolbar-Button „Vascular"). Übergibt die aktive Serie als <see cref="SeriesVolume"/>.
/// </summary>
public partial class StudyViewerWindow
{
    private VascularWorkspaceWindow? _vascularWorkspaceWindow;
    private VascularWorkspaceSnapshot? _vascularWorkspaceSnapshot;

    /// <summary>
    /// Called by the vascular workspace whenever its planning state changes, so the
    /// snapshot can be persisted in the measurement-session envelope (Phase E).
    /// </summary>
    private void OnVascularWorkspaceSnapshotChanged(VascularWorkspaceSnapshot snapshot)
    {
        _vascularWorkspaceSnapshot = snapshot;
        ScheduleMeasurementSessionSave();
    }

    private async void OnWorkspaceVascularClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();

        ViewportSlot? slot = _activeSlot;
        if (slot?.Series is null)
        {
            ShowToast("Vascular Workspace: keine aktive Serie ausgewählt.", ToastSeverity.Warning);
            return;
        }

        SeriesVolume? volume = slot.Volume
            ?? (_volumeCache.TryGetValue(slot.Series.SeriesInstanceUid, out SeriesVolume? cached) ? cached : null);

        if (volume is null)
        {
            await EnsureVolumeLoadedForSlotAsync(slot, slot.Series);
            volume = _volumeCache.TryGetValue(slot.Series.SeriesInstanceUid, out SeriesVolume? loaded) ? loaded : null;
        }

        if (volume is null)
        {
            ShowToast("Vascular Workspace: Serie kann nicht als 3D-Volumen geladen werden.", ToastSeverity.Warning);
            return;
        }

        string title = string.IsNullOrWhiteSpace(slot.Series.SeriesDescription)
            ? $"S{slot.Series.SeriesNumber}"
            : $"S{slot.Series.SeriesNumber} {slot.Series.SeriesDescription.Trim()}";

        if (_vascularWorkspaceWindow is { IsLoaded: true })
        {
            _vascularWorkspaceWindow.Activate();
            return;
        }

        _vascularWorkspaceWindow = new VascularWorkspaceWindow(volume, title)
        {
            AutoSegmentationRunner = RunVascularAutoSegmentationAsync,
            SnapshotChanged = OnVascularWorkspaceSnapshotChanged,
        };
        _vascularWorkspaceWindow.Closed += (_, _) => _vascularWorkspaceWindow = null;
        _vascularWorkspaceWindow.Show(this);
    }
}
