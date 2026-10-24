using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Cursors = System.Windows.Input.Cursors;

namespace HpPowerSwitcher.Controls;

/// <summary>A time/value series backed by a fixed-size ring buffer (zero allocations when pushing).</summary>
public sealed class ChartSeries
{
    private readonly double[] _times;
    private readonly float[] _values;
    private int _head;

    public string Name { get; }
    public Color Color { get; }
    public Pen LinePen { get; }
    public Brush AreaBrush { get; }
    public bool FillArea { get; }
    public int Count { get; private set; }
    public int Capacity => _times.Length;

    public ChartSeries(string name, Color color, bool fillArea = true, int capacity = 4096)
    {
        Name = name;
        Color = color;
        FillArea = fillArea;
        _times = new double[capacity];
        _values = new float[capacity];

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        LinePen = new Pen(brush, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        LinePen.Freeze();

        var grad = new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(110, color.R, color.G, color.B), 0),
             new GradientStop(Color.FromArgb(8, color.R, color.G, color.B), 1)],
            new Point(0, 0), new Point(0, 1));
        grad.Freeze();
        AreaBrush = grad;
    }

    public void Push(double time, double value)
    {
        _times[_head] = time;
        _values[_head] = (float)value;
        _head = (_head + 1) % _times.Length;
        if (Count < _times.Length) Count++;
    }

    public void Clear()
    {
        _head = 0;
        Count = 0;
    }

    /// <summary>Index 0 is the oldest sample.</summary>
    public double TimeAt(int i) => _times[(_head - Count + i + _times.Length * 2) % _times.Length];

    public float ValueAt(int i) => _values[(_head - Count + i + _times.Length * 2) % _times.Length];

    /// <summary>First logical index whose time is &gt;= t.</summary>
    public int LowerBound(double t)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (TimeAt(mid) < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

/// <summary>
/// Lightweight retained-nothing chart: draws only in <see cref="OnRender"/>, only when
/// <see cref="Refresh"/> (or mouse hover) invalidates it. Points are bucketed per pixel column
/// so cost is bounded by control width regardless of history length.
/// </summary>
public sealed class LiveChart : FrameworkElement
{
    private const double PadLeft = 42, PadRight = 10, PadTop = 12, PadBottom = 18;
    private const double BucketPx = 1.5;

    private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20)));
    private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)), 1));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, 0x33, 0x41, 0x55)), 1));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(160, 0xCB, 0xD5, 0xE1)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
    private static readonly Brush StatBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
    private static readonly Brush TipBg = Freeze(new SolidColorBrush(Color.FromArgb(235, 0x0F, 0x17, 0x2A)));
    private static readonly Brush TipText = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)));
    private static readonly Typeface LabelFace = new("Segoe UI Variable Display, Segoe UI");

    private readonly List<Point> _segment = new(512);
    private readonly Dictionary<string, FormattedText> _textCache = new();
    private double _hoverX = double.NaN;

    // bucket accumulator
    private int _bucketId = int.MinValue;
    private double _bucketSumX, _bucketSumV;
    private int _bucketCount;

    public List<ChartSeries> Series { get; } = [];

    /// <summary>Lower axis bound.</summary>
    public double AxisMin { get; set; }

    /// <summary>Upper axis bound; null = auto-scale (rounded to a "nice" number).</summary>
    public double? AxisMax { get; set; }

    /// <summary>Minimum span used by auto-scale so small values don't look noisy.</summary>
    public double MinAutoSpan { get; set; } = 1;

    public string Unit { get; set; } = "";
    public string ValueFormat { get; set; } = "F0";

    /// <summary>Visible time window, seconds.</summary>
    public double WindowSeconds { get; set; } = 60;

    /// <summary>Current time (right edge), seconds on the same clock as samples.</summary>
    public double Now { get; set; }

    /// <summary>Samples further apart than this break the line (e.g. while sampling was paused).</summary>
    public double GapSeconds { get; set; } = 10;

    public LiveChart()
    {
        SnapsToDevicePixels = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        Focusable = false;
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        _hoverX = double.NaN;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height);

    private FormattedText Text(string s, double size, Brush brush, bool bold = false)
    {
        string key = $"{s}|{size}|{(bold ? 1 : 0)}|{brush.GetHashCode()}";
        if (_textCache.TryGetValue(key, out var ft)) return ft;
        if (_textCache.Count > 80) _textCache.Clear();

        var face = bold ? new Typeface(LabelFace.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : LabelFace;
        ft = new FormattedText(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, face, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _textCache[key] = ft;
        return ft;
    }

    private static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / p;
        double m = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
        return m * p;
    }

    private static string WindowLabel(double s) =>
        s >= 60 ? $"-{s / 60:0}m" : $"-{s:0}s";

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 50) return;

        dc.DrawRoundedRectangle(BgBrush, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1), 8, 8);

        var plot = new Rect(PadLeft, PadTop, w - PadLeft - PadRight, h - PadTop - PadBottom);
        double tMin = Now - WindowSeconds;

        // ---- pass 1: data extents + stats of the first series --------------------------------
        double dataMax = double.MinValue;
        bool anyData = false;
        double statMin = double.MaxValue, statMax = double.MinValue, statSum = 0;
        int statN = 0;

        for (int s = 0; s < Series.Count; s++)
        {
            var ser = Series[s];
            for (int i = ser.LowerBound(tMin); i < ser.Count; i++)
            {
                float v = ser.ValueAt(i);
                if (float.IsNaN(v)) continue;
                anyData = true;
                if (v > dataMax) dataMax = v;
                if (s == 0)
                {
                    if (v < statMin) statMin = v;
                    if (v > statMax) statMax = v;
                    statSum += v;
                    statN++;
                }
            }
        }

        double lo = AxisMin;
        double hi = AxisMax ?? NiceCeil(Math.Max(anyData ? dataMax * 1.12 : 0, lo + MinAutoSpan));
        if (hi <= lo) hi = lo + 1;

        // ---- grid & axis labels --------------------------------------------------------------
        for (int g = 0; g <= 4; g++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * g / 4.0) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        for (int g = 0; g <= 2; g++)
        {
            double val = lo + (hi - lo) * g / 2.0;
            var ft = Text(val.ToString(hi - lo < 10 ? "0.#" : "0", CultureInfo.InvariantCulture), 9.5, LabelBrush);
            double y = plot.Bottom - plot.Height * g / 2.0;
            dc.DrawText(ft, new Point(PadLeft - 6 - ft.Width, Math.Clamp(y - ft.Height / 2, 0, h - ft.Height)));
        }
        var tl = Text(WindowLabel(WindowSeconds), 9.5, LabelBrush);
        var tr = Text("now", 9.5, LabelBrush);
        dc.DrawText(tl, new Point(plot.Left, h - PadBottom + 3));
        dc.DrawText(tr, new Point(plot.Right - tr.Width, h - PadBottom + 3));

        if (!anyData)
        {
            var nd = Text("No data / sensor unavailable", 11.5, LabelBrush);
            dc.DrawText(nd, new Point(plot.Left + (plot.Width - nd.Width) / 2, plot.Top + (plot.Height - nd.Height) / 2));
            return;
        }

        // ---- series geometry -----------------------------------------------------------------
        foreach (var ser in Series)
        {
            var line = new StreamGeometry();
            var area = ser.FillArea ? new StreamGeometry() : null;
            using (var lc = line.Open())
            using (var ac = area?.Open())
            {
                _segment.Clear();
                ResetBucket();
                double prevT = double.NaN;

                for (int i = ser.LowerBound(tMin); i < ser.Count; i++)
                {
                    double t = ser.TimeAt(i);
                    float v = ser.ValueAt(i);

                    if (float.IsNaN(v) || (!double.IsNaN(prevT) && t - prevT > GapSeconds))
                    {
                        FlushBucket(plot);
                        EmitSegment(lc, ac, plot);
                        if (float.IsNaN(v)) { prevT = double.NaN; continue; }
                    }
                    prevT = t;

                    double x = plot.Left + (t - tMin) / WindowSeconds * plot.Width;
                    double y = plot.Bottom - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * plot.Height;
                    int id = (int)(x / BucketPx);
                    if (id != _bucketId)
                    {
                        FlushBucket(plot);
                        _bucketId = id;
                    }
                    _bucketSumX += x;
                    _bucketSumV += y;
                    _bucketCount++;
                }

                FlushBucket(plot);
                EmitSegment(lc, ac, plot);
            }

            line.Freeze();
            if (area != null)
            {
                area.Freeze();
                dc.DrawGeometry(ser.AreaBrush, null, area);
            }
            dc.DrawGeometry(null, ser.LinePen, line);

            // latest value marker
            for (int i = ser.Count - 1; i >= 0 && i >= ser.Count - 3; i--)
            {
                float v = ser.ValueAt(i);
                double t = ser.TimeAt(i);
                if (float.IsNaN(v) || t < tMin) continue;
                double x = plot.Left + (t - tMin) / WindowSeconds * plot.Width;
                double y = plot.Bottom - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * plot.Height;
                dc.DrawEllipse(ser.LinePen.Brush, null, new Point(Math.Min(x, plot.Right), y), 3, 3);
                break;
            }
        }

        // ---- stats ---------------------------------------------------------------------------
        if (statN > 0)
        {
            string f = ValueFormat;
            var st = Text($"min {statMin.ToString(f, CultureInfo.InvariantCulture)} · avg {(statSum / statN).ToString(f, CultureInfo.InvariantCulture)} · max {statMax.ToString(f, CultureInfo.InvariantCulture)} {Unit}", 9.5, StatBrush);
            dc.DrawText(st, new Point(plot.Right - st.Width - 4, 0));
        }

        // ---- hover readout -------------------------------------------------------------------
        if (!double.IsNaN(_hoverX) && _hoverX >= plot.Left && _hoverX <= plot.Right && Series.Count > 0)
        {
            double tHover = tMin + (_hoverX - plot.Left) / plot.Width * WindowSeconds;
            dc.DrawLine(HoverPen, new Point(_hoverX, plot.Top), new Point(_hoverX, plot.Bottom));

            var lines = new List<(string, Brush)>();
            double age = Now - tHover;
            lines.Add(($"{age:0.#}s ago", StatBrush));
            foreach (var ser in Series)
            {
                int idx = ser.LowerBound(tHover);
                if (idx >= ser.Count) idx = ser.Count - 1;
                if (idx > 0 && idx < ser.Count && Math.Abs(ser.TimeAt(idx - 1) - tHover) < Math.Abs(ser.TimeAt(idx) - tHover))
                    idx--;
                if (idx < 0 || Math.Abs(ser.TimeAt(idx) - tHover) > GapSeconds) continue;
                float v = ser.ValueAt(idx);
                if (float.IsNaN(v)) continue;
                lines.Add(($"{ser.Name}: {v.ToString(ValueFormat, CultureInfo.InvariantCulture)} {Unit}", ser.LinePen.Brush));
            }

            double tw = 0, th = 0;
            var fts = new List<FormattedText>(lines.Count);
            foreach (var (s, b) in lines)
            {
                var ft = new FormattedText(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, LabelFace, 10.5, b,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                fts.Add(ft);
                tw = Math.Max(tw, ft.Width);
                th += ft.Height;
            }

            double bx = _hoverX + 12 + tw + 12 > w ? _hoverX - 12 - tw - 12 : _hoverX + 12;
            dc.DrawRoundedRectangle(TipBg, BorderPen, new Rect(bx, plot.Top + 4, tw + 12, th + 8), 5, 5);
            double ty = plot.Top + 8;
            foreach (var ft in fts)
            {
                dc.DrawText(ft, new Point(bx + 6, ty));
                ty += ft.Height;
            }
        }
    }

    private void ResetBucket()
    {
        _bucketId = int.MinValue;
        _bucketSumX = _bucketSumV = 0;
        _bucketCount = 0;
    }

    private void FlushBucket(Rect plot)
    {
        if (_bucketCount > 0)
            _segment.Add(new Point(_bucketSumX / _bucketCount, _bucketSumV / _bucketCount));
        _bucketSumX = _bucketSumV = 0;
        _bucketCount = 0;
        _bucketId = int.MinValue;
    }

    private void EmitSegment(StreamGeometryContext lc, StreamGeometryContext? ac, Rect plot)
    {
        if (_segment.Count == 1)
        {
            // lone sample: render as a short flat tick so it is visible
            var p = _segment[0];
            _segment.Add(new Point(p.X + 1, p.Y));
        }

        if (_segment.Count >= 2)
        {
            lc.BeginFigure(_segment[0], false, false);
            for (int i = 1; i < _segment.Count; i++)
                lc.LineTo(_segment[i], true, true);

            if (ac != null)
            {
                ac.BeginFigure(new Point(_segment[0].X, plot.Bottom), true, true);
                for (int i = 0; i < _segment.Count; i++)
                    ac.LineTo(_segment[i], true, false);
                ac.LineTo(new Point(_segment[^1].X, plot.Bottom), true, false);
            }
        }

        _segment.Clear();
    }
}
