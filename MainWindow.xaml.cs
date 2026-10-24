using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HpPowerSwitcher.Services;

namespace HpPowerSwitcher;

public sealed class HpComponentRowViewModel
{
    public HpComponentStatus Model { get; }
    public string DisplayName => Model.DisplayName;
    public string Description => string.IsNullOrEmpty(Model.CpuDisplay)
        ? Model.Description
        : $"{Model.Description} ({Model.CpuDisplay})";
    public string CategoryBadge => Model.CategoryBadge;
    public string MemoryDisplay => Model.MemoryDisplay;
    public string StartModeText => Model.StartModeText;
    public string StateText => Model.StateText;
    public bool CanActivate => Model.CanActivate;
    public bool CanDeactivate => Model.CanDeactivate;

    public SolidColorBrush StatusDotBrush { get; }
    public SolidColorBrush CategoryBrush { get; }
    public SolidColorBrush StateBadgeBg { get; }
    public SolidColorBrush StateBadgeFg { get; }

    public HpComponentRowViewModel(HpComponentStatus model)
    {
        Model = model;
        StatusDotBrush = new SolidColorBrush(model.IsRunning
            ? System.Windows.Media.Color.FromRgb(34, 197, 94)   // #22C55E Green
            : System.Windows.Media.Color.FromRgb(100, 116, 139)); // #64748B Slate

        CategoryBrush = BrushFromHex(model.CategoryColorHex);

        StateBadgeBg = new SolidColorBrush(model.IsRunning
            ? System.Windows.Media.Color.FromRgb(19, 47, 38)
            : System.Windows.Media.Color.FromRgb(30, 41, 59));

        StateBadgeFg = new SolidColorBrush(model.IsRunning
            ? System.Windows.Media.Color.FromRgb(134, 239, 172)
            : System.Windows.Media.Color.FromRgb(148, 163, 184));
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(c);
    }
}

public partial class MainWindow : Window
{
    private readonly HpPowerService _powerService = new();
    private readonly HpBloatManagerService _bloatService = new();
    private readonly DispatcherTimer _refreshTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private HpThermalMode? _selectedBiosMode;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _refreshTimer.Tick += (_, _) => RefreshAllData(updateStatusBar: false);

        Loaded += (_, _) =>
        {
            RefreshAllData(updateStatusBar: true);
            _refreshTimer.Start();
        };

        Closing += MainWindow_Closing;
    }

    private void RefreshAllData(bool updateStatusBar)
    {
        try
        {
            var snap = _powerService.GetSnapshot();
            if (snap.CurrentBiosMode.HasValue)
                _selectedBiosMode = snap.CurrentBiosMode;

            TxtModelName.Text = snap.ModelName;
            TxtBiosInfo.Text = $"BIOS: {snap.BiosVersion}  •  AC Overlay: {HpPowerService.FormatOverlayName(snap.AcOverlayMode)}  •  Battery Overlay: {HpPowerService.FormatOverlayName(snap.DcOverlayMode)}";

            if (snap.IsAdmin)
            {
                BadgeAdmin.Background = BrushFromHex("#14532D");
                TxtAdminBadge.Text = "ADMIN • BIOS WMI READY";
                TxtAdminBadge.Foreground = BrushFromHex("#86EFAC");
                BtnElevate.Visibility = Visibility.Collapsed;
            }
            else
            {
                BadgeAdmin.Background = BrushFromHex("#7C2D12");
                TxtAdminBadge.Text = "LIMITED (NON-ADMIN)";
                TxtAdminBadge.Foreground = BrushFromHex("#FDBA74");
                BtnElevate.Visibility = Visibility.Visible;
            }

            TxtActiveThermalIndicator.Text = _selectedBiosMode?.ToString() ?? "Unknown";
            TxtBatteryIndicator.Text = snap.BatteryStatusText;

            RenderThermalModeCards(snap);
            UpdateWindowsPowerButtons(snap);
            RefreshServicesAndProcesses();

            if (updateStatusBar)
                TxtStatusBar.Text = snap.StatusMessage;
        }
        catch (Exception ex)
        {
            TxtStatusBar.Text = $"Refresh error: {ex.Message}";
        }
    }

    private void RenderThermalModeCards(SystemPowerSnapshot snap)
    {
        GridThermalCards.Children.Clear();
        var options = HpPowerService.GetAllModeOptions(snap.SupportedBiosModes);

        foreach (var opt in options)
        {
            bool isActive = _selectedBiosMode == opt.Mode;
            var accentColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(opt.AccentHex);
            var accentBrush = new SolidColorBrush(accentColor);

            var cardBorder = new Border
            {
                Background = isActive
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 37, 64))
                    : BrushFromHex("#131C31"),
                BorderBrush = isActive ? accentBrush : BrushFromHex("#1E293B"),
                BorderThickness = new Thickness(isActive ? 2 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Opacity = opt.IsSupported ? 1.0 : 0.55,
                Tag = opt.Mode
            };

            cardBorder.MouseEnter += (s, _) =>
            {
                if (s is Border b && (HpThermalMode)b.Tag != _selectedBiosMode)
                    b.Background = BrushFromHex("#1A2642");
            };
            cardBorder.MouseLeave += (s, _) =>
            {
                if (s is Border b && (HpThermalMode)b.Tag != _selectedBiosMode)
                    b.Background = BrushFromHex("#131C31");
            };
            cardBorder.MouseLeftButtonUp += ThermalCard_Click;

            var stack = new StackPanel();
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titlePanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titlePanel.Children.Add(new TextBlock
            {
                Text = opt.IconGlyph,
                FontSize = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            titlePanel.Children.Add(new TextBlock
            {
                Text = opt.Title,
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                Foreground = isActive ? accentBrush : System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(titlePanel, 0);
            topGrid.Children.Add(titlePanel);

            if (isActive)
            {
                var activeBadge = new Border
                {
                    Background = accentBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "ACTIVE",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushFromHex("#0B1120")
                    }
                };
                Grid.SetColumn(activeBadge, 1);
                topGrid.Children.Add(activeBadge);
            }

            stack.Children.Add(topGrid);
            stack.Children.Add(new TextBlock
            {
                Text = opt.Subtitle,
                FontSize = 11.5,
                Foreground = BrushFromHex("#94A3B8"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            });

            cardBorder.Child = stack;
            GridThermalCards.Children.Add(cardBorder);
        }
    }

    private void UpdateWindowsPowerButtons(SystemPowerSnapshot snap)
    {
        var activeOverlay = snap.IsOnAcPower ? snap.AcOverlayMode : snap.DcOverlayMode;
        TxtWinOverlaySub.Text = snap.IsOnAcPower
            ? $"Current (Plugged In): {HpPowerService.FormatOverlayName(activeOverlay)}"
            : $"Current (On Battery): {HpPowerService.FormatOverlayName(activeOverlay)}";

        HighlightOverlayButton(BtnWinEco, activeOverlay == WindowsOverlayPowerMode.BestPowerEfficiency, "#4ADE80");
        HighlightOverlayButton(BtnWinBalanced, activeOverlay == WindowsOverlayPowerMode.Balanced, "#38BDF8");
        HighlightOverlayButton(BtnWinPerf, activeOverlay == WindowsOverlayPowerMode.BestPerformance, "#F97316");
    }

    private static void HighlightOverlayButton(System.Windows.Controls.Button btn, bool isSelected, string accentHex)
    {
        if (isSelected)
        {
            btn.Background = BrushFromHex("#1E293B");
            btn.BorderBrush = BrushFromHex(accentHex);
            btn.BorderThickness = new Thickness(1.5);
            btn.Foreground = BrushFromHex(accentHex);
        }
        else
        {
            btn.Background = BrushFromHex("#0F172A");
            btn.BorderBrush = BrushFromHex("#334155");
            btn.BorderThickness = new Thickness(1);
            btn.Foreground = BrushFromHex("#E2E8F0");
        }
    }

    private void RefreshServicesAndProcesses()
    {
        var statuses = _bloatService.GetAllStatuses();
        double totalRamMb = statuses.Where(s => s.IsRunning).Sum(s => s.MemoryMb);
        int runningCount = statuses.Count(s => s.IsRunning);

        TxtHpRamIndicator.Text = $"{totalRamMb:F1} MB ({runningCount} active)";
        TxtHpRamIndicator.Foreground = totalRamMb > 250
            ? BrushFromHex("#F87171")
            : (totalRamMb > 100 ? BrushFromHex("#FBBF24") : BrushFromHex("#4ADE80"));

        int bloatRunning = statuses.Count(s =>
            s.IsRunning && s.Category is HpSafetyCategory.TelemetryAndBloat or HpSafetyCategory.RedundantBackground);

        TxtServiceSummary.Text = bloatRunning > 0
            ? $"• {bloatRunning} telemetry/bloat item(s) currently running"
            : "• All telemetry & redundant bloat stopped";

        ListComponents.ItemsSource = statuses.Select(s => new HpComponentRowViewModel(s)).ToList();
    }

    private void ThermalCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: HpThermalMode mode })
        {
            ApplyThermalMode(mode);
        }
    }

    private void ApplyThermalMode(HpThermalMode mode)
    {
        bool syncWin = ChkSyncWinOverlay.IsChecked == true;
        var (ok, msg, _) = _powerService.SetHpThermalMode(mode, syncWin);
        if (ok)
            _selectedBiosMode = mode;

        RefreshAllData(updateStatusBar: false);
        TxtStatusBar.Text = msg;
    }

    private void BtnWinEco_Click(object sender, RoutedEventArgs e) =>
        ApplyWindowsOverlay(WindowsOverlayPowerMode.BestPowerEfficiency);

    private void BtnWinBalanced_Click(object sender, RoutedEventArgs e) =>
        ApplyWindowsOverlay(WindowsOverlayPowerMode.Balanced);

    private void BtnWinPerf_Click(object sender, RoutedEventArgs e) =>
        ApplyWindowsOverlay(WindowsOverlayPowerMode.BestPerformance);

    private void ApplyWindowsOverlay(WindowsOverlayPowerMode mode)
    {
        bool both = ChkApplyBothAcDc.IsChecked == true;
        bool isAc = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        var (_, msg, _) = _powerService.SetOverlayPowerMode(mode, applyToAc: both || isAc, applyToDc: both || !isAc);

        RefreshAllData(updateStatusBar: false);
        TxtStatusBar.Text = msg;
    }

    private void BtnActivateItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: HpComponentStatus item })
        {
            var (_, msg) = _bloatService.ActivateComponent(item);
            RefreshServicesAndProcesses();
            TxtStatusBar.Text = msg;
        }
    }

    private void BtnDeactivateItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: HpComponentStatus item })
        {
            var (_, msg) = _bloatService.DeactivateComponent(item);
            RefreshServicesAndProcesses();
            TxtStatusBar.Text = msg;
        }
    }

    private void BtnDisableAllBloat_Click(object sender, RoutedEventArgs e)
    {
        var (_, _, summary) = _bloatService.DisableAllTelemetryAndBloat();
        RefreshServicesAndProcesses();
        TxtStatusBar.Text = summary;
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshAllData(updateStatusBar: false);
        TxtStatusBar.Text = $"Refreshed all hardware & service indicators at {DateTime.Now:HH:mm:ss}.";
    }

    private void BtnElevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                _isExiting = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            TxtStatusBar.Text = $"Elevation cancelled: {ex.Message}";
        }
    }

    private void InitializeTrayIcon()
    {
        Icon trayIco = SystemIcons.Shield;
        try
        {
            if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                trayIco = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? SystemIcons.Shield;
            }
        }
        catch { }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayIco,
            Text = "HP Spectre Power & Service Control",
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Power Switcher", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("⚡ Smart Sense", null, (_, _) => Dispatcher.Invoke(() => ApplyThermalMode(HpThermalMode.SmartSense)));
        menu.Items.Add("⚖ Balanced", null, (_, _) => Dispatcher.Invoke(() => ApplyThermalMode(HpThermalMode.Balanced)));
        menu.Items.Add("🚀 Performance", null, (_, _) => Dispatcher.Invoke(() => ApplyThermalMode(HpThermalMode.Performance)));
        menu.Items.Add("❄ Cool", null, (_, _) => Dispatcher.Invoke(() => ApplyThermalMode(HpThermalMode.Cool)));
        menu.Items.Add("🌙 Quiet", null, (_, _) => Dispatcher.Invoke(() => ApplyThermalMode(HpThermalMode.Quiet)));
        menu.Items.Add("🍃 Power Saver", null, (_, _) => Dispatcher.Invoke(() => ApplyThermalMode(HpThermalMode.PowerSaver)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("🧹 Stop Telemetry & Bloat", null, (_, _) => Dispatcher.Invoke(() => BtnDisableAllBloat_Click(this, new RoutedEventArgs())));
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _isExiting = true;
            Dispatcher.Invoke(Close);
        });

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting && ChkMinimizeToTray.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _refreshTimer.Stop();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(c);
    }
}
