using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HpPowerSwitcher.Controls;
using HpPowerSwitcher.Services;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using RadioButton = System.Windows.Controls.RadioButton;

namespace HpPowerSwitcher;

/// <summary>
/// Live sensor page with multi-metric tabs, CPU per-core mini screens, and dedicated GPU, Disk, Wi-Fi, and NPU monitors.
/// Design for low overhead:
///  - sampling only runs while the Sensors page is visible (window shown, not minimized);
///  - one sample at a time on a thread-pool thread (ticks are skipped while busy);
///  - slow sensors are throttled inside <see cref="HardwareSensorService"/>;
///  - charts redraw only after a sample arrives and bucket points per pixel.
/// </summary>
public partial class MainWindow
{
    private const int SensorTabIndex = 2;

    private readonly HardwareSensorService _sensors = new();
    private readonly Stopwatch _sensorClock = Stopwatch.StartNew();
    private DispatcherTimer? _sensorTimer;
    private bool _sensorBusy;
    private double _sensorIntervalSec = 1;
    private double _sensorWindowSec = 60;
    private bool _sensorSourceShown;

    // Series
    private ChartSeries _serTemp = null!, _serLoad = null!, _serFreq = null!,
        _serFan1 = null!, _serFan2 = null!, _serMem = null!, _serBatt = null!;
    private ChartSeries _serGpu = null!, _serDisk = null!,
        _serWifiRx = null!, _serWifiTx = null!, _serNpu = null!;
    private ChartSeries[] _serCpuCores = [];

    // Charts collection
    private LiveChart[] _allCharts = [];
    private readonly List<LiveChart> _dynamicMiniCharts = [];

    // CPU Cores UI elements
    private int _selectedCpuIndex = -1; // -1 = Overall, 0..N-1 = Core index
    private Border[] _miniCpuCards = [];
    private TextBlock[] _miniCpuLoadTexts = [];
    private RadioButton[] _cpuTabRadios = [];

    private static readonly SolidColorBrush TempOk = BrushCache.FromHex("#4ADE80");
    private static readonly SolidColorBrush TempWarm = BrushCache.FromHex("#FBBF24");
    private static readonly SolidColorBrush TempHot = BrushCache.FromHex("#F87171");
    private static readonly SolidColorBrush BorderDefault = BrushCache.FromHex("#1E293B");
    private static readonly SolidColorBrush BorderSelected = BrushCache.FromHex("#38BDF8");
    private static readonly SolidColorBrush BgDefault = BrushCache.FromHex("#0F172A");
    private static readonly SolidColorBrush BgSelected = BrushCache.FromHex("#17253D");

    private void InitializeSensors()
    {
        // 1. Core series
        _serTemp = new ChartSeries("Temp", Color.FromRgb(0xF8, 0x71, 0x71));
        _serLoad = new ChartSeries("Load", Color.FromRgb(0x38, 0xBD, 0xF8));
        _serFreq = new ChartSeries("Freq", Color.FromRgb(0xA7, 0x8B, 0xFA));
        _serFan1 = new ChartSeries("Fan 1", Color.FromRgb(0x34, 0xD3, 0x99));
        _serFan2 = new ChartSeries("Fan 2", Color.FromRgb(0x2D, 0xD4, 0xBF), fillArea: false);
        _serMem = new ChartSeries("Used", Color.FromRgb(0xFB, 0x92, 0x3C));
        _serBatt = new ChartSeries("Power", Color.FromRgb(0xFA, 0xCC, 0x15));

        // GPU, Disk, Wi-Fi, NPU series
        _serGpu = new ChartSeries("GPU", Color.FromRgb(0x38, 0xBD, 0xF8));
        _serDisk = new ChartSeries("Disk Active", Color.FromRgb(0x4A, 0xDE, 0x80));
        _serWifiRx = new ChartSeries("Download", Color.FromRgb(0x38, 0xBD, 0xF8));
        _serWifiTx = new ChartSeries("Upload", Color.FromRgb(0xC0, 0x84, 0xFC), fillArea: false);
        _serNpu = new ChartSeries("NPU", Color.FromRgb(0xA8, 0x55, 0xF7));

        // 2. Setup overview charts
        Setup(ChartTemp, "°C", "F0", axisMin: 20, axisMax: 100, series: [_serTemp]);
        Setup(ChartLoad, "%", "F0", axisMin: 0, axisMax: 100, series: [_serLoad]);
        Setup(ChartFreq, "MHz", "F0", axisMin: 0, axisMax: null, series: [_serFreq], minSpan: 1000);
        Setup(ChartFan, "RPM", "F0", axisMin: 0, axisMax: null, series: [_serFan1, _serFan2], minSpan: 3000);
        Setup(ChartMem, "%", "F0", axisMin: 0, axisMax: 100, series: [_serMem]);
        Setup(ChartBatt, "W", "F1", axisMin: 0, axisMax: null, series: [_serBatt], minSpan: 15);
        Setup(ChartGpu, "%", "F0", axisMin: 0, axisMax: 100, series: [_serGpu]);
        Setup(ChartDisk, "%", "F0", axisMin: 0, axisMax: 100, series: [_serDisk]);
        Setup(ChartWifi, "MB/s", "F2", axisMin: 0, axisMax: null, series: [_serWifiRx, _serWifiTx], minSpan: 1);
        Setup(ChartNpu, "%", "F0", axisMin: 0, axisMax: 100, series: [_serNpu]);

        // 3. Setup detailed charts
        Setup(ChartSelectedCpu, "%", "F0", axisMin: 0, axisMax: 100, series: [_serLoad]);
        Setup(ChartGpuDetail, "%", "F0", axisMin: 0, axisMax: 100, series: [_serGpu]);
        Setup(ChartDiskDetail, "%", "F0", axisMin: 0, axisMax: 100, series: [_serDisk]);
        Setup(ChartWifiDetail, "MB/s", "F2", axisMin: 0, axisMax: null, series: [_serWifiRx, _serWifiTx], minSpan: 1);
        Setup(ChartNpuDetail, "%", "F0", axisMin: 0, axisMax: 100, series: [_serNpu]);
        Setup(ChartFanDetail, "RPM", "F0", axisMin: 0, axisMax: null, series: [_serFan1, _serFan2], minSpan: 3000);
        Setup(ChartBattDetail, "W", "F1", axisMin: 0, axisMax: null, series: [_serBatt], minSpan: 15);

        // 4. Populate per-core UI (tabs + mini screens)
        InitCpuCoresUi();

        _allCharts = [
            ChartTemp, ChartLoad, ChartFreq, ChartFan, ChartMem, ChartBatt,
            ChartGpu, ChartDisk, ChartWifi, ChartNpu,
            ChartSelectedCpu,
            ChartGpuDetail, ChartDiskDetail, ChartWifiDetail, ChartNpuDetail,
            ChartFanDetail, ChartBattDetail
        ];

        ApplyChartTiming();

        _sensorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_sensorIntervalSec)
        };
        _sensorTimer.Tick += SensorTimer_Tick;

        IsVisibleChanged += (_, _) => UpdateSensorTimerState();
        StateChanged += (_, _) => UpdateSensorTimerState();
        UpdateSensorTimerState();

        static void Setup(LiveChart c, string unit, string fmt, double axisMin, double? axisMax,
            ChartSeries[] series, double minSpan = 1)
        {
            c.Unit = unit;
            c.ValueFormat = fmt;
            c.AxisMin = axisMin;
            c.AxisMax = axisMax;
            c.MinAutoSpan = minSpan;
            c.Series.AddRange(series);
        }
    }

    private void InitCpuCoresUi()
    {
        int coreCount = Environment.ProcessorCount;
        _serCpuCores = new ChartSeries[coreCount];
        _miniCpuCards = new Border[coreCount + 1];
        _miniCpuLoadTexts = new TextBlock[coreCount + 1];
        _cpuTabRadios = new RadioButton[coreCount + 1];
        _dynamicMiniCharts.Clear();
        PanelCpuTabs.Children.Clear();
        GridCpuMiniScreens.Children.Clear();

        TxtCoreCountHeader.Text = $" ({coreCount} Logical Processors)";

        // A. Tabs between CPU usages:
        // 1. Overall Radio Tab
        var radioOverall = new RadioButton
        {
            Content = "Overall",
            Tag = -1,
            GroupName = "CpuUsageTabs",
            Style = (Style)FindResource("SegBtnStyle"),
            IsChecked = true,
            Margin = new Thickness(2, 0, 2, 0)
        };
        radioOverall.Checked += CpuTab_Checked;
        PanelCpuTabs.Children.Add(radioOverall);
        _cpuTabRadios[0] = radioOverall;

        // 2. Per-core Radio Tabs
        for (int i = 0; i < coreCount; i++)
        {
            _serCpuCores[i] = new ChartSeries($"Core {i}", Color.FromRgb(0x38, 0xBD, 0xF8));

            var radio = new RadioButton
            {
                Content = $"Core {i}",
                Tag = i,
                GroupName = "CpuUsageTabs",
                Style = (Style)FindResource("SegBtnStyle"),
                Margin = new Thickness(2, 0, 2, 0)
            };
            radio.Checked += CpuTab_Checked;
            PanelCpuTabs.Children.Add(radio);
            _cpuTabRadios[i + 1] = radio;
        }

        // B. Mini Screens:
        // Card 0: Overall mini screen
        var cardOverall = CreateMiniCpuCard(-1, "Overall CPU", _serLoad);
        GridCpuMiniScreens.Children.Add(cardOverall);
        _miniCpuCards[0] = cardOverall;

        // Cards 1..N: Each CPU Core
        for (int i = 0; i < coreCount; i++)
        {
            var card = CreateMiniCpuCard(i, $"CPU {i}", _serCpuCores[i]);
            GridCpuMiniScreens.Children.Add(card);
            _miniCpuCards[i + 1] = card;
        }

        SelectCpuCore(-1);
    }

    private Border CreateMiniCpuCard(int coreIndex, string title, ChartSeries series)
    {
        var border = new Border
        {
            Background = coreIndex == _selectedCpuIndex ? BgSelected : BgDefault,
            BorderBrush = coreIndex == _selectedCpuIndex ? BorderSelected : BorderDefault,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(3, 3, 3, 3),
            Width = 144,
            Cursor = Cursors.Hand,
            Tag = coreIndex,
            ToolTip = coreIndex < 0 ? "Click to view Overall CPU load" : $"Click to inspect Core {coreIndex} on the big graphic"
        };

        var stack = new StackPanel();

        var grid = new Grid();
        var txtTitle = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushCache.FromHex("#94A3B8"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var txtLoad = new TextBlock
        {
            Text = "--",
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushCache.FromHex("#38BDF8"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        grid.Children.Add(txtTitle);
        grid.Children.Add(txtLoad);
        stack.Children.Add(grid);

        int textIndex = coreIndex < 0 ? 0 : coreIndex + 1;
        _miniCpuLoadTexts[textIndex] = txtLoad;

        var chart = new LiveChart
        {
            Height = 44,
            Margin = new Thickness(0, 4, 0, 0),
            Unit = "%",
            ValueFormat = "F0",
            AxisMin = 0,
            AxisMax = 100,
            MinAutoSpan = 1
        };
        chart.Series.Add(series);
        stack.Children.Add(chart);
        _dynamicMiniCharts.Add(chart);

        border.Child = stack;

        border.MouseLeftButtonUp += (s, e) =>
        {
            SelectCpuCore(coreIndex);
        };

        return border;
    }

    private void SelectCpuCore(int coreIndex)
    {
        _selectedCpuIndex = coreIndex;

        // Update Big Graphic Series
        ChartSelectedCpu.Series.Clear();
        if (coreIndex < 0)
        {
            ChartSelectedCpu.Series.Add(_serLoad);
            TxtSelectedCpuTitle.Text = "CPU - Overall Load";
            TxtSelectedCpuBadge.Text = "TOTAL UTILIZATION";
        }
        else
        {
            if (coreIndex < _serCpuCores.Length)
                ChartSelectedCpu.Series.Add(_serCpuCores[coreIndex]);
            TxtSelectedCpuTitle.Text = $"CPU - Logical Processor {coreIndex}";
            TxtSelectedCpuBadge.Text = $"CORE {coreIndex}";
        }
        ChartSelectedCpu.Refresh();

        // Update tab chip selection
        int radioIdx = coreIndex < 0 ? 0 : coreIndex + 1;
        if (radioIdx < _cpuTabRadios.Length && _cpuTabRadios[radioIdx] != null)
        {
            _cpuTabRadios[radioIdx].IsChecked = true;
        }

        // Update border highlights on mini cards
        for (int i = 0; i < _miniCpuCards.Length; i++)
        {
            if (_miniCpuCards[i] == null) continue;
            bool isSel = (i == 0 && coreIndex < 0) || (i == coreIndex + 1);
            _miniCpuCards[i].BorderBrush = isSel ? BorderSelected : BorderDefault;
            _miniCpuCards[i].Background = isSel ? BgSelected : BgDefault;
        }
    }

    private void CpuTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int coreIndex } && _selectedCpuIndex != coreIndex)
        {
            SelectCpuCore(coreIndex);
        }
    }

    private void SensorSubNav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || ViewSensorOverview == null) return;

        ViewSensorOverview.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        ViewSensorCpu.Visibility = tag == "Cpu" ? Visibility.Visible : Visibility.Collapsed;
        ViewSensorGpu.Visibility = tag == "Gpu" ? Visibility.Visible : Visibility.Collapsed;
        ViewSensorDisk.Visibility = tag == "Disk" ? Visibility.Visible : Visibility.Collapsed;
        ViewSensorWifi.Visibility = tag == "Wifi" ? Visibility.Visible : Visibility.Collapsed;
        ViewSensorNpu.Visibility = tag == "Npu" ? Visibility.Visible : Visibility.Collapsed;
        ViewSensorCooling.Visibility = tag == "Cooling" ? Visibility.Visible : Visibility.Collapsed;

        RefreshCharts();
    }

    // Overview Card Click Handlers -> Drill down to specific tab
    private void CardCpu_Click(object sender, MouseButtonEventArgs e) => RadioSubNavCpu.IsChecked = true;
    private void CardGpu_Click(object sender, MouseButtonEventArgs e) => RadioSubNavGpu.IsChecked = true;
    private void CardDisk_Click(object sender, MouseButtonEventArgs e) => RadioSubNavDisk.IsChecked = true;
    private void CardWifi_Click(object sender, MouseButtonEventArgs e) => RadioSubNavWifi.IsChecked = true;
    private void CardNpu_Click(object sender, MouseButtonEventArgs e) => RadioSubNavNpu.IsChecked = true;
    private void CardCooling_Click(object sender, MouseButtonEventArgs e) => RadioSubNavCooling.IsChecked = true;

    private bool IsSensorPageActive =>
        _sensorTimer != null
        && IsVisible
        && WindowState != WindowState.Minimized
        && MainTabControl?.SelectedIndex == SensorTabIndex
        && _sensorIntervalSec > 0;

    /// <summary>Starts/stops the sampling timer so nothing runs when the page can't be seen.</summary>
    private void UpdateSensorTimerState()
    {
        if (_sensorTimer == null) return;

        if (IsSensorPageActive)
        {
            _sensorTimer.Interval = TimeSpan.FromSeconds(_sensorIntervalSec);
            if (!_sensorTimer.IsEnabled)
            {
                _sensorTimer.Start();
                SensorTimer_Tick(null, EventArgs.Empty); // immediate first sample
            }
        }
        else
        {
            _sensorTimer.Stop();
        }
    }

    private void ApplyChartTiming()
    {
        foreach (var c in _allCharts)
        {
            c.WindowSeconds = _sensorWindowSec;
            c.GapSeconds = Math.Max(5, _sensorIntervalSec * 4);
        }
        foreach (var c in _dynamicMiniCharts)
        {
            c.WindowSeconds = _sensorWindowSec;
            c.GapSeconds = Math.Max(5, _sensorIntervalSec * 4);
        }
    }

    private void RefreshCharts()
    {
        double now = _sensorClock.Elapsed.TotalSeconds;
        foreach (var c in _allCharts)
        {
            c.Now = now;
            c.Refresh();
        }
        foreach (var c in _dynamicMiniCharts)
        {
            c.Now = now;
            c.Refresh();
        }
    }

    private void SensorRate_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _sensorIntervalSec = double.Parse(tag, CultureInfo.InvariantCulture);

        if (_sensorTimer == null) return;
        _sensorTimer.Stop();
        ApplyChartTiming();
        UpdateSensorTimerState();

        if (_sensorIntervalSec <= 0)
            TxtSensorInfo.Text = "Sampling paused - no sensor polling is running.";
    }

    private void SensorWindow_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _sensorWindowSec = double.Parse(tag, CultureInfo.InvariantCulture);
        if (_allCharts.Length == 0) return;
        ApplyChartTiming();
        RefreshCharts();
    }

    private void BtnClearSensors_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in new[] { _serTemp, _serLoad, _serFreq, _serFan1, _serFan2, _serMem, _serBatt, _serGpu, _serDisk, _serWifiRx, _serWifiTx, _serNpu })
            s.Clear();
        foreach (var s in _serCpuCores)
            s.Clear();
        RefreshCharts();
    }

    private async void SensorTimer_Tick(object? sender, EventArgs e)
    {
        if (_sensorBusy) return;
        _sensorBusy = true;
        try
        {
            var sample = await Task.Run(_sensors.Sample);
            if (!IsSensorPageActive) return; // user left the page while sampling

            ApplySensorSample(sample, _sensorClock.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            TxtSensorInfo.Text = $"Sensor error: {ex.Message}";
        }
        finally
        {
            _sensorBusy = false;
        }
    }

    private void ApplySensorSample(in SensorSample s, double t)
    {
        // 1. Push samples to time-series
        _serTemp.Push(t, s.CpuTempC);
        _serLoad.Push(t, s.CpuLoadPct);
        _serFreq.Push(t, s.CpuFreqMhz);
        _serFan1.Push(t, s.Fan1Rpm);
        _serFan2.Push(t, s.Fan2Rpm);
        _serMem.Push(t, s.MemLoadPct);
        _serBatt.Push(t, s.BatteryWatts);
        _serGpu.Push(t, s.GpuLoadPct);
        _serDisk.Push(t, s.DiskActivePct);

        double wifiRxMb = s.WifiRxBytesPerSec / (1024.0 * 1024.0);
        double wifiTxMb = s.WifiTxBytesPerSec / (1024.0 * 1024.0);
        _serWifiRx.Push(t, wifiRxMb);
        _serWifiTx.Push(t, wifiTxMb);

        _serNpu.Push(t, s.NpuLoadPct);

        // 2. Push per-core samples
        if (s.CpuCoreLoads != null)
        {
            for (int i = 0; i < s.CpuCoreLoads.Length && i < _serCpuCores.Length; i++)
            {
                _serCpuCores[i].Push(t, s.CpuCoreLoads[i]);
                if (i + 1 < _miniCpuLoadTexts.Length && _miniCpuLoadTexts[i + 1] != null)
                {
                    _miniCpuLoadTexts[i + 1].Text = Fmt(s.CpuCoreLoads[i], "0", " %");
                }
            }
        }
        if (_miniCpuLoadTexts.Length > 0 && _miniCpuLoadTexts[0] != null)
        {
            _miniCpuLoadTexts[0].Text = Fmt(s.CpuLoadPct, "0", " %");
        }

        // 3. Update Big Graphic selected CPU metrics
        if (_selectedCpuIndex < 0)
        {
            TxtSelectedCpuLoad.Text = Fmt(s.CpuLoadPct, "0", " %");
            TxtSelectedCpuFreq.Text = double.IsNaN(s.CpuFreqMhz) ? "--" : $"{s.CpuFreqMhz / 1000.0:0.00} GHz";
        }
        else if (s.CpuCoreLoads != null && _selectedCpuIndex < s.CpuCoreLoads.Length)
        {
            TxtSelectedCpuLoad.Text = Fmt(s.CpuCoreLoads[_selectedCpuIndex], "0", " %");
            TxtSelectedCpuFreq.Text = double.IsNaN(s.CpuFreqMhz) ? "--" : $"{s.CpuFreqMhz / 1000.0:0.00} GHz";
        }

        // 4. Update Overview text labels
        TxtTemp.Text = Fmt(s.CpuTempC, "0", " °C");
        TxtTemp.Foreground = double.IsNaN(s.CpuTempC) ? TempWarm
            : s.CpuTempC >= 85 ? TempHot : s.CpuTempC >= 70 ? TempWarm : TempOk;
        TxtLoad.Text = Fmt(s.CpuLoadPct, "0", " %");
        TxtFreq.Text = double.IsNaN(s.CpuFreqMhz) ? "--" : $"{s.CpuFreqMhz / 1000.0:0.00} GHz";

        // GPU
        TxtGpu.Text = Fmt(s.GpuLoadPct, "0", " %");
        TxtGpuSub.Text = s.GpuName;
        TxtGpuDetailLoad.Text = Fmt(s.GpuLoadPct, "0", " %");
        TxtGpuDetailName.Text = string.IsNullOrEmpty(s.GpuName) ? "GPU Graphics & Compute" : s.GpuName;

        // Disk
        TxtDisk.Text = Fmt(s.DiskActivePct, "0", " %");
        TxtDiskSub.Text = $"R: {FormatBytesPerSec(s.DiskReadBytesPerSec)}  •  W: {FormatBytesPerSec(s.DiskWriteBytesPerSec)}";
        TxtDiskDetailActive.Text = Fmt(s.DiskActivePct, "0", " %");
        TxtDiskDetailRead.Text = FormatBytesPerSec(s.DiskReadBytesPerSec);
        TxtDiskDetailWrite.Text = FormatBytesPerSec(s.DiskWriteBytesPerSec);

        // Wi-Fi
        double totalWifiBps = s.WifiRxBytesPerSec + s.WifiTxBytesPerSec;
        TxtWifi.Text = FormatBytesPerSec(totalWifiBps);
        TxtWifiSub.Text = $"↓ {FormatBytesPerSec(s.WifiRxBytesPerSec)}  ↑ {FormatBytesPerSec(s.WifiTxBytesPerSec)}";
        TxtWifiDetailDownload.Text = FormatBytesPerSec(s.WifiRxBytesPerSec);
        TxtWifiDetailUpload.Text = FormatBytesPerSec(s.WifiTxBytesPerSec);
        TxtWifiDetailName.Text = string.IsNullOrEmpty(s.WifiName) ? "Wi-Fi Network Adapter" : s.WifiName;
        TxtWifiDetailLink.Text = s.WifiLinkSpeedBps > 0 ? $"{s.WifiLinkSpeedBps / 1_000_000.0:0} Mbps" : "--";

        // NPU
        TxtNpu.Text = Fmt(s.NpuLoadPct, "0", " %");
        TxtNpuSub.Text = s.NpuName;
        TxtNpuDetailLoad.Text = Fmt(s.NpuLoadPct, "0", " %");
        TxtNpuDetailName.Text = string.IsNullOrEmpty(s.NpuName) ? "Neural Processing Unit (NPU)" : s.NpuName;

        // Fans
        string fanStr = double.IsNaN(s.Fan1Rpm) ? "N/A"
            : (double.IsNaN(s.Fan2Rpm) || s.Fan2Rpm <= 0) ? $"{s.Fan1Rpm:0} RPM"
            : $"{s.Fan1Rpm:0} / {s.Fan2Rpm:0} RPM";
        TxtFan.Text = fanStr;
        TxtFanSub.Text = double.IsNaN(s.Fan1Rpm) ? _sensors.FanStatus : "";
        TxtFanDetail.Text = fanStr;
        TxtFanDetailSub.Text = _sensors.FanStatus;

        // Memory
        TxtMem.Text = Fmt(s.MemLoadPct, "0", " %");
        TxtMemSub.Text = double.IsNaN(s.MemUsedGb) ? "" : $"{s.MemUsedGb:0.0} / {s.MemTotalGb:0.0} GB";

        // Battery
        string battStr = Fmt(s.BatteryWatts, "0.0", " W");
        string battSub = double.IsNaN(s.BatteryWatts) ? "" : (s.BatteryCharging ? "charging" : "discharging");
        TxtBatt.Text = battStr;
        TxtBattSub.Text = battSub;
        TxtBattDetail.Text = battStr;
        TxtBattDetailSub.Text = battSub;

        if (!_sensorSourceShown || _sensorTickCounter++ % 15 == 0)
        {
            _sensorSourceShown = true;
            TxtSensorInfo.Text = $"{_sensors.SourceSummary}  •  sample cost {s.SampleMs:0.0} ms";
        }

        RefreshCharts();
    }

    private static string FormatBytesPerSec(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || bytesPerSec <= 0) return "0 KB/s";
        if (bytesPerSec >= 1024.0 * 1024.0 * 1024.0)
            return $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):0.0} GB/s";
        if (bytesPerSec >= 1024.0 * 1024.0)
            return $"{bytesPerSec / (1024.0 * 1024.0):0.0} MB/s";
        if (bytesPerSec >= 1024.0)
            return $"{bytesPerSec / 1024.0:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    private int _sensorTickCounter;

    private static string Fmt(double v, string format, string suffix) =>
        double.IsNaN(v) ? "--" : v.ToString(format, CultureInfo.InvariantCulture) + suffix;
}
