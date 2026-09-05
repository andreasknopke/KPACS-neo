using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using KPACS.Viewer.Services.VesselAnalysis;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;

namespace KPACS.Viewer.Controls;

/// <summary>
/// Interactive diameter chart for the EVAR workspace (Phase C1). Plots vessel diameter
/// (mm) against distance along the centerline (mm from the proximal reference) using
/// ScottPlot, and reports a click/drag back as a centerline station index so the chart,
/// the orthogonal cross-section and the CPR view stay in sync (bidirectional station sync).
///
/// The control owns only presentation; all data shaping and the click → station mapping
/// live in <see cref="VascularDiameterChartHelper"/> (pure, unit-tested).
/// </summary>
internal sealed class DiameterChartPanel : Grid
{
    private readonly AvaPlot _avaPlot;
    private VerticalLine? _stationLine;
    private IReadOnlyList<VascularDiameterChartHelper.ChartPoint> _samples = [];
    private CenterlinePathRef? _pathRef;

    /// <summary>
    /// Raised when the user clicks or drags in the chart. The argument is the centerline
    /// station index nearest the clicked arc length, or -1 when no path is bound.
    /// </summary>
    public event Action<int>? StationRequested;

    public DiameterChartPanel()
    {
        _avaPlot = new AvaPlot
        {
            Margin = new Avalonia.Thickness(2),
        };
        Children.Add(_avaPlot);

        // We drive interaction ourselves (click → station); disable ScottPlot's pan/zoom
        // so a left-drag does not fight the station scrubber.
        _avaPlot.UserInputProcessor.Disable();

        _avaPlot.PointerPressed += OnPointerChanged;
        _avaPlot.PointerMoved += OnPointerMoved;

        StylePlot();
    }

    /// <summary>
    /// Replace the plotted diameter curve. Passing an empty list clears the chart.
    /// </summary>
    public void SetSamples(IReadOnlyList<VascularDiameterChartHelper.ChartPoint> samples, CenterlinePathRef? pathRef)
    {
        _samples = samples ?? [];
        _pathRef = pathRef;

        Plot plot = _avaPlot.Plot;
        plot.Clear();
        StylePlot();

        if (_samples.Count >= 1)
        {
            double[] xs = new double[_samples.Count];
            double[] ys = new double[_samples.Count];
            for (int i = 0; i < _samples.Count; i++)
            {
                xs[i] = _samples[i].ArcLengthMm;
                ys[i] = _samples[i].DiameterMm;
            }

            var scatter = plot.Add.ScatterLine(xs, ys);
            scatter.LineWidth = 1.5f;
            scatter.Color = Colors.DeepSkyBlue;
            plot.Axes.AutoScale();
            plot.Axes.Margins(bottom: 0.05, top: 0.1, left: 0.05, right: 0.05);
        }
        else
        {
            plot.Axes.SetLimits(0, 1, 0, 1);
        }

        _stationLine = plot.Add.VerticalLine(double.NaN);
        _stationLine.Color = Colors.Orange;
        _stationLine.LineWidth = 1.5f;
        _stationLine.Text = "Station";

        _avaPlot.Refresh();
    }

    /// <summary>
    /// Move the station marker to the given centerline station index (from the bound path).
    /// </summary>
    public void SetStation(int stationIndex)
    {
        if (_stationLine is null || _pathRef is null)
        {
            return;
        }

        double? arc = _pathRef.ArcLengthAt(stationIndex);
        if (arc is null)
        {
            return;
        }

        _stationLine.X = arc.Value;
        _avaPlot.Refresh();
    }

    /// <summary>
    /// Phase D: draw semi-transparent graft-component bands over the chart. Each band spans
    /// the component's station range (x) and the component's proximal/distal diameter (y).
    /// Passing an empty list clears the bands.
    /// </summary>
    public void SetGraftBands(IReadOnlyList<Models.GraftComponent> components)
    {
        Plot plot = _avaPlot.Plot;
        if (components is null || components.Count == 0)
        {
            return;
        }

        foreach (Models.GraftComponent component in components)
        {
            double x0 = Math.Min(component.StartStationMm, component.EndStationMm);
            double x1 = Math.Max(component.StartStationMm, component.EndStationMm);
            double y0 = Math.Min(component.ProximalDiameterMm, component.DistalDiameterMm);
            double y1 = Math.Max(component.ProximalDiameterMm, component.DistalDiameterMm);

            var rect = plot.Add.Rectangle(x0, x1, y0, y1);
            rect.FillColor = component.Name.StartsWith("Aorten", StringComparison.OrdinalIgnoreCase)
                ? Colors.Orange.WithAlpha(0.25)
                : Colors.Violet.WithAlpha(0.25);
            rect.LineColor = Colors.Transparent;
        }

        _avaPlot.Refresh();
    }

    private void StylePlot()
    {
        Plot plot = _avaPlot.Plot;
        plot.Title("Diameter entlang Centerline");
        plot.XLabel(VascularDiameterChartHelper.FormatXAxisLabel(
            _samples.Count > 0 ? _samples[^1].ArcLengthMm : 0));
        plot.YLabel(VascularDiameterChartHelper.FormatYAxisLabel());
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(_avaPlot);
        if (pt.Properties.IsLeftButtonPressed)
        {
            RaiseStation(pt.Position);
        }
    }

    private void OnPointerChanged(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(_avaPlot);
        if (pt.Properties.IsLeftButtonPressed)
        {
            RaiseStation(pt.Position);
        }
    }

    private void RaiseStation(Avalonia.Point position)
    {
        if (_pathRef is null || _samples.Count == 0)
        {
            return;
        }

        // ScottPlot's Avalonia display scale is 1, so DIPs map 1:1 to plot pixels.
        Coordinates coords = _avaPlot.Plot.GetCoordinates(
            (float)position.X, (float)position.Y);

        int station = VascularDiameterChartHelper.ResolveStationIndex(_pathRef.Path, coords.X);
        if (station >= 0)
        {
            StationRequested?.Invoke(station);
        }
    }

    /// <summary>
    /// Phase E: renders the current chart to a PNG and returns it as a base64 string for
    /// embedding in the HTML report. Returns null when rendering fails.
    /// </summary>
    public string? ExportPngBase64()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"kpacs-chart-{Guid.NewGuid():N}.png");
        try
        {
            _avaPlot.Plot.SavePng(tempPath, width: 1200, height: 600);
            byte[] png = File.ReadAllBytes(tempPath);
            return Convert.ToBase64String(png);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Lightweight binding to the centerline the chart is showing, so the panel can map
    /// arc length ↔ station index without depending on the workspace window.
    /// </summary>
    public sealed class CenterlinePathRef
    {
        public required Models.CenterlinePath Path { get; init; }

        public double? ArcLengthAt(int stationIndex) =>
            stationIndex >= 0 && stationIndex < Path.Points.Count
                ? Path.Points[stationIndex].ArcLengthMm
                : null;
    }
}
