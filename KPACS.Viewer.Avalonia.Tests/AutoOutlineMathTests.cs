using Avalonia;
using KPACS.Viewer.RoiDraft;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Fine-grained tests for the AutoOutlineMath grid→contour seam. Pure inputs, pure outputs —
/// no engine, no Avalonia control, no Dispatcher. This is the cheapest test surface in the repo.
/// </summary>
public class AutoOutlineMathTests
{
    private static bool[,] Square(int size, int fillFrom, int fillTo)
    {
        bool[,] mask = new bool[size, size];
        for (int y = fillFrom; y < fillTo; y++)
        {
            for (int x = fillFrom; x < fillTo; x++)
            {
                mask[x, y] = true;
            }
        }

        return mask;
    }

    [Fact]
    public void BuildMarchingSquaresSegments_SingleTruePixel_ProducesFourSegments()
    {
        bool[,] mask = { { true } };

        var segments = AutoOutlineMath.BuildMarchingSquaresSegments(mask);

        Assert.Equal(4, segments.Count);
    }

    [Fact]
    public void BuildMarchingSquaresSegments_EmptyMask_ProducesNoSegments()
    {
        bool[,] mask = new bool[4, 4];

        var segments = AutoOutlineMath.BuildMarchingSquaresSegments(mask);

        Assert.Empty(segments);
    }

    [Fact]
    public void TraceBoundary_SingleTruePixel_ReturnsClosedCounterClockwiseDiamond()
    {
        bool[,] mask = { { true } };

        Point[] contour = AutoOutlineMath.TraceBoundary(mask, maxPointCount: 20);

        Assert.Equal(4, contour.Length);
        // Counter-clockwise ⇒ positive signed area.
        Assert.True(AutoOutlineMath.ComputeSignedPolygonArea(contour) > 0);
    }

    [Fact]
    public void TraceBoundary_FilledSquare_ReturnsPositiveAreaContour()
    {
        bool[,] mask = Square(size: 10, fillFrom: 2, fillTo: 8);

        Point[] contour = AutoOutlineMath.TraceBoundary(mask, maxPointCount: 60);

        Assert.True(contour.Length >= 3);
        Assert.True(AutoOutlineMath.ComputeSignedPolygonArea(contour) > 0);
    }

    [Fact]
    public void TraceBoundary_EmptyMask_ReturnsNoPoints()
    {
        bool[,] mask = new bool[6, 6];

        Point[] contour = AutoOutlineMath.TraceBoundary(mask, maxPointCount: 40);

        Assert.Empty(contour);
    }

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0, 1.0)] // unit square, CCW ⇒ +1
    [InlineData(0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0, 0.0, -1.0)] // same square reversed ⇒ -1
    public void ComputeSignedPolygonArea_KnownSquares_MatchExpected(
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double expected)
    {
        Point[] points = [new(x0, y0), new(x1, y1), new(x2, y2), new(x3, y3)];

        double area = AutoOutlineMath.ComputeSignedPolygonArea(points);

        Assert.Equal(expected, area, precision: 9);
    }

    [Fact]
    public void ComputeSignedPolygonArea_TooFewPoints_ReturnsZero()
    {
        Assert.Equal(0, AutoOutlineMath.ComputeSignedPolygonArea([new Point(0, 0), new Point(1, 1)]));
    }

    [Fact]
    public void ResampleContour_SmallContour_IsLeftUnchangedButCounterClockwise()
    {
        // Clockwise triangle → should be flipped to CCW, count preserved (3 <= target).
        Point[] clockwise = [new(0, 0), new(0, 1), new(1, 0)];

        Point[] result = AutoOutlineMath.ResampleContour(clockwise, maxPointCount: 20);

        Assert.Equal(3, result.Length);
        Assert.True(AutoOutlineMath.ComputeSignedPolygonArea(result) > 0);
    }

    [Fact]
    public void ResampleContour_LargeContour_ClampsToMaxPointCount()
    {
        // A dense 100-point circle.
        Point[] circle = new Point[100];
        for (int i = 0; i < circle.Length; i++)
        {
            double angle = 2 * Math.PI * i / circle.Length;
            circle[i] = new Point(Math.Cos(angle), Math.Sin(angle));
        }

        Point[] result = AutoOutlineMath.ResampleContour(circle, maxPointCount: 20);

        Assert.Equal(20, result.Length);
    }

    [Fact]
    public void EnsureCounterClockwise_FlipsNegativeAreaPolygon()
    {
        Point[] clockwise = [new(0, 0), new(0, 1), new(1, 1), new(1, 0)];

        Point[] result = AutoOutlineMath.EnsureCounterClockwise(clockwise);

        Assert.True(AutoOutlineMath.ComputeSignedPolygonArea(result) > 0);
    }

    [Fact]
    public void GetPointDistance_ThreeFourTriangle_ReturnsFive()
    {
        Assert.Equal(5.0, AutoOutlineMath.GetPointDistance(new Point(0, 0), new Point(3, 4)), precision: 9);
    }
}
