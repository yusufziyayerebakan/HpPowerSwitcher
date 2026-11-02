using System.Diagnostics;
using System.Globalization;
using HpPowerSwitcher.Controls;
using HpPowerSwitcher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HpPowerSwitcher;

public partial class MainWindow
{
    private HardwareSensorService? _sensors;
    private DispatcherQueueTimer? _sensorTimer;
    private Task _sensorTask = Task.CompletedTask;
    private readonly Stopwatch _sensorClock = Stopwatch.StartNew();
    private bool _sensorBusy;
    private double _sensorInterval = 1, _sensorWindow = 60;
    private double _historyInterval = 1;
    private ChartSeries[] _series = [], _coreSeries = [];
    private readonly List<SensorVisual> _sensorVisuals = [];
    private readonly List<(Button Button, TextBlock Value)> _coreCards = [];
    private Grid? _sensorCards;
    private ScrollViewer? _sensorViewport;
    private TextBlock? _sensorInfo;
    private ComboBox? _sensorSection;
    private SensorSample? _lastSample;
    private int _selectedCore = -1;
    private sealed record SensorVisual(int Metric, TextBlock Value, TextBlock Subtitle, LiveChart Chart);
    private static readonly string[] MetricNames = ["CPU temperature", "CPU utilization", "CPU frequency", "Cooling fans",
        "Memory", "Battery power", "GPU", "Disk activity", "Wi-Fi", "NPU"];
    private static readonly string[] MetricColors = ["#F59B9E", "#86E8CB", "#B5ADFA", "#8CDBC9", "#F0B68D",
        "#E7D18D", "#98CFFF", "#B2D79B", "#8BCCDF", "#C7AEED", "#74B1A7", "#C7AEED"];

    private void EnsureSensorPage()
    {
        if (_sensorTimer != null) return;
        _series = Enumerable.Range(0, 12).Select(i => new ChartSeries(i < 10 ? MetricNames[i] : i == 10 ? "Fan 2" : "Upload",
            BrushCache.FromHex(MetricColors[i]).Color)).ToArray();
        _coreSeries = Enumerable.Range(0, Environment.ProcessorCount).Select(i =>
            new ChartSeries($"CPU {i}", BrushCache.FromHex("#60CDFF").Color)).ToArray();
        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < 3; i++) toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sensorSection = new ComboBox { Header = "Sensor view", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Overview", "CPU cores", "GPU", "Disk", "Wi-Fi", "NPU", "Cooling & power" }, SelectedIndex = 0 };
        var rate = new ComboBox { Header = "Refresh", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "0.5 seconds", "1 second", "2 seconds", "5 seconds", "Paused" }, SelectedIndex = 1 };
        var history = new ComboBox { Header = "History", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "1 minute", "5 minutes", "15 minutes", "30 minutes" }, SelectedIndex = 0 };
        foreach (var combo in new[] { _sensorSection, rate, history })
        {
            combo.Background = BrushCache.FromHex("#121E2B"); combo.BorderBrush = BrushCache.FromHex("#2B4052");
            combo.CornerRadius = new CornerRadius(10); combo.FontSize = 12; combo.Padding = new Thickness(12, 9, 12, 9);
        }
        Grid.SetColumn(rate, 1); Grid.SetColumn(history, 2);
        toolbar.Children.Add(_sensorSection); toolbar.Children.Add(rate); toolbar.Children.Add(history);
        root.Children.Add(toolbar);
        _sensorCards = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        _sensorCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sensorCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sensorViewport = new ScrollViewer { Content = _sensorCards, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _sensorViewport.ViewChanged += (_, _) => RefreshVisibleCharts();
        Grid.SetRow(_sensorViewport, 1); root.Children.Add(_sensorViewport);
        _sensorInfo = new TextBlock { Text = "Sensors run only while this page is visible.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7 };
        Grid.SetRow(_sensorInfo, 2); root.Children.Add(_sensorInfo);
        SensorHost.Content = root;
        _sensorSection.SelectionChanged += (_, _) => BuildSensorView();
        rate.SelectionChanged += (_, _) =>
        {
            _sensorInterval = new double[] { 0.5, 1, 2, 5, 0 }[rate.SelectedIndex];
            if (_sensorInterval > 0) _historyInterval = _sensorInterval;
            _sensorTimer?.Stop();
            ResizeHistories(); UpdateSensorPolling();
            if (_sensorInterval == 0) _sensorInfo.Text = "Sampling paused.";
        };
        history.SelectionChanged += (_, _) =>
        {
            _sensorWindow = new double[] { 60, 300, 900, 1800 }[history.SelectedIndex];
            ResizeHistories(); RefreshVisibleCharts();
        };
        _sensorTimer = DispatcherQueue.CreateTimer();
        _sensorTimer.Tick += (_, _) => StartSensorSample();
        BuildSensorView();
    }

    private void ResizeHistories()
    {
        // Keep the selected duration at the current rate, instead of 4096 samples in every series.
        int capacity = (int)Math.Ceiling(_sensorWindow / _historyInterval) + 2;
        foreach (var series in _series) series.SetCapacity(capacity);
        foreach (var series in _coreSeries) series.SetCapacity(capacity);
    }

    private void BuildSensorView()
    {
        if (_sensorCards == null || _sensorSection == null) return;
        _sensorCards.Children.Clear(); _sensorCards.RowDefinitions.Clear();
        _sensorVisuals.Clear(); _coreCards.Clear();
        int section = _sensorSection.SelectedIndex;
        int[] metrics = section switch
        {
            1 => [1], 2 => [6], 3 => [7], 4 => [8], 5 => [9], 6 => [3, 5],
            _ => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]
        };
        bool overview = section == 0;
        for (int i = 0; i < metrics.Length; i++)
        {
            int metric = metrics[i];
            var stack = new StackPanel { Spacing = 8 };
            var title = new TextBlock { Text = MetricNames[metric].ToUpperInvariant(), FontSize = 10, CharacterSpacing = 100, Foreground = BrushCache.FromHex("#9AB0C3"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var value = new TextBlock { Text = "—", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _series[metric].Brush };
            var subtitle = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = BrushCache.FromHex("#8EA5B9") };
            var chart = new LiveChart { Height = overview ? 100 : 230, AxisMax = metric is 1 or 4 or 6 or 7 or 9 ? 100 : null,
                Unit = metric switch { 0 => "°C", 2 => "MHz", 3 => "RPM", 5 => "W", 8 => "MB/s", _ => "%" },
                ValueFormat = metric is 5 or 8 ? "0.0" : "0" };
            chart.Series.Add(metric == 1 && section == 1 && _selectedCore >= 0 ? _coreSeries[_selectedCore] : _series[metric]);
            if (metric == 3) chart.Series.Add(_series[10]);
            if (metric == 8) chart.Series.Add(_series[11]);
            stack.Children.Add(title); stack.Children.Add(value); stack.Children.Add(subtitle); stack.Children.Add(chart);
            var card = new Border { Child = stack, Padding = new Thickness(18), CornerRadius = new CornerRadius(16),
                Background = BrushCache.FromHex("#121E2B"),
                BorderBrush = BrushCache.FromHex("#263747"), BorderThickness = new Thickness(1) };
            int row = overview ? i / 2 : i;
            if (row >= _sensorCards.RowDefinitions.Count) _sensorCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(card, row);
            if (overview) Grid.SetColumn(card, i % 2); else Grid.SetColumnSpan(card, 2);
            _sensorCards.Children.Add(card);
            _sensorVisuals.Add(new SensorVisual(metric, value, subtitle, chart));
            if (overview)
                card.Tapped += (_, _) => _sensorSection.SelectedIndex = metric switch { 1 or 0 or 2 => 1, 6 => 2, 7 => 3, 8 => 4, 9 => 5, 3 or 5 => 6, _ => 0 };
        }
        if (section == 1) BuildCoreCards();
        if (_lastSample is { } sample) UpdateSensorValues(sample);
        RefreshVisibleCharts();
    }

    private void BuildCoreCards()
    {
        // Core charts exist only in the CPU view; selecting a core reuses the large chart.
        var panel = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int i = 0; i < 4; i++) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = -1; i < _coreSeries.Length; i++)
        {
            int core = i, index = i + 1;
            if (index % 4 == 0) panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var value = new TextBlock { Text = "—", FontSize = 14 };
            var chart = new LiveChart { Height = 55, AxisMax = 100, Unit = "%" };
            chart.Series.Add(core < 0 ? _series[1] : _coreSeries[core]);
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = core < 0 ? "Overall CPU" : $"CPU {core}", FontSize = 12 });
            stack.Children.Add(value); stack.Children.Add(chart);
            var button = new Button { Content = stack, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8),
                Style = (Style)Application.Current.Resources["ProfileCardStyle"], BorderBrush = BrushCache.FromHex(core == _selectedCore ? "#86E8CB" : "#263747") };
            button.Click += (_, _) =>
            {
                _selectedCore = core;
                var large = _sensorVisuals[0].Chart;
                large.Series.Clear(); large.Series.Add(core < 0 ? _series[1] : _coreSeries[core]);
                for (int c = 0; c < _coreCards.Count; c++)
                    _coreCards[c].Button.BorderBrush = BrushCache.FromHex(c == core + 1 ? "#86E8CB" : "#263747");
                if (_lastSample is { } sample) UpdateSensorValues(sample);
                RefreshVisibleCharts();
            };
            Grid.SetRow(button, index / 4); Grid.SetColumn(button, index % 4);
            panel.Children.Add(button); _coreCards.Add((button, value));
            _sensorVisuals.Add(new SensorVisual(-2 - core, value, new TextBlock(), chart));
        }
        int row = _sensorCards!.RowDefinitions.Count;
        _sensorCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(panel, row); Grid.SetColumnSpan(panel, 2); _sensorCards.Children.Add(panel);
    }

    private bool SensorActive => _ready && WindowVisible && _page == "Sensors" && _sensorInterval > 0;
    private void UpdateSensorPolling()
    {
        if (_sensorTimer == null) return;
        if (WindowVisible && _page == "Sensors" && _sensorVisuals.Count == 0) BuildSensorView();
        if (SensorActive)
        {
            _sensorTimer.Interval = TimeSpan.FromSeconds(_sensorInterval);
            if (!_sensorTimer.IsRunning) { _sensorTimer.Start(); StartSensorSample(); }
        }
        else
        {
            _sensorTimer.Stop();
            if (!WindowVisible || _page != "Sensors")
            {
                _sensorCards?.Children.Clear();
                _sensorVisuals.Clear();
                _coreCards.Clear();
            }
        }
    }
    private void StartSensorSample()
    {
        if (!SensorActive || _sensorBusy) return;
        _sensorBusy = true;
        if (_lastSample == null) _sensorInfo!.Text = "Initializing sensor sources…";
        _sensorTask = SampleAndApplyAsync();
    }
    private async Task SampleAndApplyAsync()
    {
        try
        {
            _sensors ??= new HardwareSensorService();
            var sample = await Task.Run(_sensors.Sample);
            if (!SensorActive) return;
            double time = _sensorClock.Elapsed.TotalSeconds;
            _lastSample = sample;
            TxtBatteryIndicator.Text = PowerStatus.Read().Description;
            double[] values = [sample.CpuTempC, sample.CpuLoadPct, sample.CpuFreqMhz, sample.Fan1Rpm, sample.MemLoadPct,
                sample.BatteryWatts, sample.GpuLoadPct, sample.DiskActivePct, sample.WifiRxBytesPerSec / 1048576,
                sample.NpuLoadPct, sample.Fan2Rpm, sample.WifiTxBytesPerSec / 1048576];
            for (int i = 0; i < _series.Length; i++) _series[i].Push(time, values[i]);
            for (int i = 0; i < _coreSeries.Length && i < sample.CpuCoreLoads.Length; i++) _coreSeries[i].Push(time, sample.CpuCoreLoads[i]);
            UpdateSensorValues(sample); RefreshVisibleCharts();
            _sensorInfo!.Text = $"{_sensors.SourceSummary} · sample {sample.SampleMs:0.0} ms";
        }
        catch (Exception ex) { if (!_closing) _sensorInfo!.Text = $"Sensor error: {ex.Message}"; }
        finally { _sensorBusy = false; }
    }
    private void RefreshVisibleCharts()
    {
        foreach (var visual in _sensorVisuals)
        {
            if (_sensorViewport is { ActualHeight: > 0 })
            {
                var bounds = visual.Chart.TransformToVisual(_sensorViewport).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, visual.Chart.ActualWidth, visual.Chart.ActualHeight));
                if (bounds.Bottom < 0 || bounds.Top > _sensorViewport.ActualHeight) continue;
            }
            visual.Chart.Now = _sensorClock.Elapsed.TotalSeconds;
            visual.Chart.WindowSeconds = _sensorWindow;
            visual.Chart.GapSeconds = Math.Max(5, _historyInterval * 4);
            visual.Chart.Refresh();
        }
    }
    private void UpdateSensorValues(in SensorSample s)
    {
        foreach (var visual in _sensorVisuals)
        {
            int metric = visual.Metric;
            if (metric < 0)
            {
                int core = -metric - 2;
                visual.Value.Text = Fmt(core < 0 ? s.CpuLoadPct : core < s.CpuCoreLoads.Length ? s.CpuCoreLoads[core] : double.NaN, "%");
                continue;
            }
            visual.Value.Text = metric switch
            {
                0 => Fmt(s.CpuTempC, "°C"),
                1 => Fmt(_sensorSection!.SelectedIndex == 1 && _selectedCore >= 0 && _selectedCore < s.CpuCoreLoads.Length ? s.CpuCoreLoads[_selectedCore] : s.CpuLoadPct, "%"),
                2 => Fmt(s.CpuFreqMhz / 1000, "GHz", "0.00"),
                3 => $"{Fmt(s.Fan1Rpm, "RPM")} / {Fmt(s.Fan2Rpm, "RPM")}",
                4 => Fmt(s.MemLoadPct, "%"), 5 => Fmt(s.BatteryWatts, "W", "0.0"),
                6 => Fmt(s.GpuLoadPct, "%"), 7 => Fmt(s.DiskActivePct, "%"),
                8 => FormatBytesPerSec(s.WifiRxBytesPerSec + s.WifiTxBytesPerSec), 9 => Fmt(s.NpuLoadPct, "%"), _ => "—"
            };
            visual.Subtitle.Text = metric switch
            {
                0 => "CPU thermal sensor", 1 => _selectedCore >= 0 && _sensorSection!.SelectedIndex == 1 ? $"Logical processor {_selectedCore}" : $"{s.CpuCoreLoads.Length} logical processors",
                3 => _sensors?.FanStatus ?? "", 4 => $"{s.MemUsedGb:0.0} / {s.MemTotalGb:0.0} GB",
                5 => double.IsNaN(s.BatteryWatts) ? "Unavailable" : s.BatteryCharging ? "Charging" : "Discharging",
                6 => s.GpuName, 7 => $"Read {FormatBytesPerSec(s.DiskReadBytesPerSec)} · Write {FormatBytesPerSec(s.DiskWriteBytesPerSec)}",
                8 => $"{s.WifiName} · ↓ {FormatBytesPerSec(s.WifiRxBytesPerSec)} · ↑ {FormatBytesPerSec(s.WifiTxBytesPerSec)} · {s.WifiLinkSpeedBps / 1000000:0} Mbps link",
                9 => s.NpuName, _ => ""
            };
        }
    }
    private static string Fmt(double value, string unit, string format = "0") =>
        double.IsFinite(value) ? $"{value.ToString(format, CultureInfo.InvariantCulture)} {unit}" : "Unavailable";
    private static string FormatBytesPerSec(double bytes) => !double.IsFinite(bytes) ? "Unavailable" :
        bytes >= 1048576 ? $"{bytes / 1048576:0.0} MB/s" : $"{Math.Max(0, bytes) / 1024:0} KB/s";
}
