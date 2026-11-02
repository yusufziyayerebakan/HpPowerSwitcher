using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace HpPowerSwitcher.Controls;

/// <summary>Bounded history sized for the selected time window and sampling rate.</summary>
public sealed class ChartSeries : SampleHistory
{
    public string Name { get; }
    public SolidColorBrush Brush { get; }
    public ChartSeries(string name, Color color, int capacity = 62) : base(capacity)
    {
        Name = name;
        Brush = new SolidColorBrush(color);
    }
}

/// <summary>Native WinUI paths. No rendering loop: redraw only on samples, resize, or selection.</summary>
public sealed class LiveChart : UserControl
{
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Microsoft.UI.Xaml.Shapes.Line[] _gridLines = new Microsoft.UI.Xaml.Shapes.Line[3];
    private readonly TextBlock _scale = new() { FontSize = 10, Opacity = 0.7, IsHitTestVisible = false };
    private readonly TextBlock _hover = new() { FontSize = 11, IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly List<Microsoft.UI.Xaml.Shapes.Path> _paths = [];
    public List<ChartSeries> Series { get; } = [];
    public double AxisMin { get; set; }
    public double? AxisMax { get; set; }
    public double WindowSeconds { get; set; } = 60;
    public double GapSeconds { get; set; } = 5;
    public string Unit { get; set; } = "";
    public string ValueFormat { get; set; } = "0";
    public double Now { get; set; }

    public LiveChart()
    {
        var root = new Grid { Background = BrushCache.FromHex("#0D1825"), CornerRadius = new CornerRadius(10) };
        for (int i = 0; i < _gridLines.Length; i++)
        {
            var line = new Microsoft.UI.Xaml.Shapes.Line { Stroke = BrushCache.FromHex("#203343"), StrokeThickness = 0.6, IsHitTestVisible = false };
            _gridLines[i] = line;
            _canvas.Children.Add(line);
        }
        root.Children.Add(_canvas);
        _scale.Margin = new Thickness(6, 2, 0, 0);
        _hover.Margin = new Thickness(6, 0, 6, 4);
        _hover.VerticalAlignment = VerticalAlignment.Bottom;
        root.Children.Add(_scale); root.Children.Add(_hover);
        Content = root;
        SizeChanged += (_, _) => Refresh();
        PointerMoved += (_, args) =>
        {
            double width = ActualWidth;
            if (width <= 0) return;
            double time = Now - WindowSeconds + Math.Clamp(args.GetCurrentPoint(this).Position.X / width, 0, 1) * WindowSeconds;
            var labels = new List<string>(Series.Count);
            foreach (var series in Series)
            {
                if (series.Count == 0) continue;
                int index = Math.Min(series.LowerBound(time), series.Count - 1);
                if (index > 0 && Math.Abs(series.TimeAt(index - 1) - time) < Math.Abs(series.TimeAt(index) - time)) index--;
                if (Math.Abs(series.TimeAt(index) - time) > GapSeconds) continue;
                float value = series.ValueAt(index);
                if (float.IsFinite(value)) labels.Add($"{series.Name}: {value.ToString(ValueFormat)} {Unit}");
            }
            _hover.Text = string.Join(" · ", labels);
        };
        PointerExited += (_, _) => _hover.Text = "";
    }

    public void Refresh()
    {
        double width = ActualWidth, height = ActualHeight - 24;
        if (width <= 0 || height <= 0 || Visibility != Visibility.Visible) return;
        double start = Now - WindowSeconds;
        for (int i = 0; i < _gridLines.Length; i++)
        {
            _gridLines[i].X2 = width;
            _gridLines[i].Y1 = _gridLines[i].Y2 = 20 + (height - 20) * (i + 1) / 4;
        }
        double max = AxisMax ?? AxisMin + 1;
        if (AxisMax == null)
            foreach (var series in Series)
                for (int i = series.LowerBound(start); i < series.Count; i++)
                    if (float.IsFinite(series.ValueAt(i))) max = Math.Max(max, series.ValueAt(i) * 1.1);
        double span = Math.Max(1, max - AxisMin);
        _scale.Text = $"{AxisMin:0}–{max:0.#} {Unit} · {WindowSeconds / 60:0.#} min";
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, ActualHeight) };
        while (_paths.Count < Series.Count)
        {
            var path = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = 1.8, IsHitTestVisible = false };
            _paths.Add(path); _canvas.Children.Add(path);
        }
        for (int s = 0; s < _paths.Count; s++)
        {
            var path = _paths[s];
            if (s >= Series.Count) { path.Data = null; continue; }
            var series = Series[s];
            path.Stroke = series.Brush;
            var geometry = new PathGeometry();
            PathFigure? figure = null;
            PolyLineSegment? segment = null;
            double previousTime = double.NaN;
            int previousBucket = -1;
            // Bound output to one point per ~2 pixels regardless of retained history size.
            for (int i = Math.Max(0, series.LowerBound(start) - 1); i < series.Count; i++)
            {
                double time = series.TimeAt(i);
                float value = series.ValueAt(i);
                if (!float.IsFinite(value)) { figure = null; previousBucket = -1; continue; }
                double x = (time - start) / WindowSeconds * width;
                int bucket = (int)(x / 2);
                var point = new Point(x, 20 + (1 - Math.Clamp((value - AxisMin) / span, 0, 1)) * (height - 20));
                if (figure == null || time - previousTime > GapSeconds)
                {
                    figure = new PathFigure { StartPoint = point, IsClosed = false, IsFilled = false };
                    segment = new PolyLineSegment();
                    figure.Segments.Add(segment); geometry.Figures.Add(figure);
                }
                else if (bucket != previousBucket || i == series.Count - 1) segment!.Points.Add(point);
                previousTime = time; previousBucket = bucket;
            }
            path.Data = geometry;
        }
    }
}
