using Avalonia.Controls;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer;

/// <summary>
/// Diameter-Chart (Phase C1). Bindet ein <see cref="DiameterChartPanel"/> in den
/// einklappbaren Chart-Streifen, speist es mit der Diameter-Kurve des aktiven
/// Centerline-Segments und hält die Station bidirektional synchron: Ein Klick/Drag im
/// Chart setzt die Querschnitts-Station (Slider + Orthogonalschnitt), und der Slider
/// bewegt die Stations-Markierung im Chart. Die Datenformung und die Klick→Station-Zuordnung
/// liegen in <see cref="VascularDiameterChartHelper"/> (pur, getestet).
/// </summary>
public partial class VascularWorkspaceWindow
{
    private DiameterChartPanel? _diameterChart;
    private bool _chartStationFromClick;

    /// <summary>
    /// Erzeugt das Chart beim ersten Aufruf und legt es in den Chart-Streifen.
    /// </summary>
    private void EnsureDiameterChart()
    {
        if (_diameterChart is not null || ChartHost is null)
        {
            return;
        }

        DiameterChartPanel chart = new();
        chart.StationRequested += OnChartStationRequested;
        ChartHost.Child = chart;
        _diameterChart = chart;
    }

    /// <summary>
    /// Baut die Diameter-Kurve aus dem aktiven Segment neu und setzt die Stations-Markierung.
    /// </summary>
    private void RefreshDiameterChart()
    {
        EnsureDiameterChart();
        if (_diameterChart is null)
        {
            return;
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        CenterlinePath? path = ActiveSegment?.Path;
        IReadOnlyList<VascularDiameterChartHelper.ChartPoint> samples =
            VascularDiameterChartHelper.BuildFromPath(path);

        DiameterChartPanel.CenterlinePathRef? pathRef =
            path?.HasRenderablePath == true
                ? new DiameterChartPanel.CenterlinePathRef { Path = path }
                : null;

        _diameterChart.SetSamples(samples, pathRef);
        _diameterChart.SetStation(_csStationIndex);
        ApplyGraftBandsToChart();

        stopwatch.Stop();
        RecordVascularPerformanceMetric("chart-update", stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Klick/Drag im Chart → Station setzen (Slider + Querschnitt). Guard gegen Rückkopplung.
    /// </summary>
    private void OnChartStationRequested(int stationIndex)
    {
        if (stationIndex < 0 || stationIndex == _csStationIndex)
        {
            return;
        }

        _chartStationFromClick = true;
        try
        {
            _csStationIndex = stationIndex;
            if (_stationSlider is not null)
            {
                _stationSlider.Value = stationIndex;
            }

            ScheduleCrossSectionRender();
            _diameterChart?.SetStation(stationIndex);
            PushWorkspaceSnapshot();
        }
        finally
        {
            _chartStationFromClick = false;
        }
    }

    /// <summary>
    /// Slider-Bewegung → Stations-Markierung im Chart nachziehen (nur wenn der Klick nicht
    /// vom Chart selbst kam, um ein Hin-und-Her zu vermeiden).
    /// </summary>
    private void SyncChartStationFromSlider(int stationIndex)
    {
        if (!_chartStationFromClick)
        {
            _diameterChart?.SetStation(stationIndex);
        }
    }
}
