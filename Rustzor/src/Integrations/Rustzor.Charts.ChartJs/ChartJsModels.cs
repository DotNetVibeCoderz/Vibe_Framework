namespace Rustzor.Charts.ChartJs;

/// <summary>A Chart.js dataset. Unset colours come from the Rustzor palette.</summary>
public sealed class ChartJsDataset
{
    public string Label { get; set; } = "";
    public IReadOnlyList<double> Data { get; set; } = Array.Empty<double>();
    /// <summary>One colour, or one per point for pie and doughnut charts.</summary>
    public string? BackgroundColor { get; set; }
    public IReadOnlyList<string>? BackgroundColors { get; set; }
    public string? BorderColor { get; set; }
    public double BorderWidth { get; set; } = 2;
    /// <summary>Fill under a line.</summary>
    public bool Fill { get; set; }
    /// <summary>Line smoothing, 0 (straight) to 0.5.</summary>
    public double Tension { get; set; } = 0.3;
    /// <summary>Draws this dataset as a different type (mixed charts): "line" over "bar".</summary>
    public string? Type { get; set; }
    public double PointRadius { get; set; } = 3;
    /// <summary>Points for scatter and bubble charts: x, y and radius.</summary>
    public IReadOnlyList<(double X, double Y, double R)>? Points { get; set; }
}

/// <summary>A clicked element: the dataset and point, and the value under the pointer.</summary>
public readonly record struct ChartJsClick(int DatasetIndex, int Index, string Label, double Value);
