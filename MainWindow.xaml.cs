using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HpPowerSwitcher.Services;

namespace HpPowerSwitcher;

public static class BrushCache
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> HexBrushes = new(StringComparer.OrdinalIgnoreCase);

    public static readonly SolidColorBrush RunningDot = CreateFrozen(34, 197, 94);
    public static readonly SolidColorBrush StoppedDot = CreateFrozen(100, 116, 139);
    public static readonly SolidColorBrush RunningBadgeBg = CreateFrozen(19, 47, 38);
    public static readonly SolidColorBrush StoppedBadgeBg = CreateFrozen(30, 41, 59);
    public static readonly SolidColorBrush RunningBadgeFg = CreateFrozen(134, 239, 172);
    public static readonly SolidColorBrush StoppedBadgeFg = CreateFrozen(148, 163, 184);
    public static readonly SolidColorBrush ActiveCardBg = CreateFrozen(23, 37, 64);
    public static readonly SolidColorBrush InactiveCardBg = FromHex("#131C31");
    public static readonly SolidColorBrush HoverCardBg = FromHex("#1A2642");
    public static readonly SolidColorBrush InactiveCardBorder = FromHex("#1E293B");

    public static SolidColorBrush FromHex(string hex)
    {
        return HexBrushes.GetOrAdd(hex, static h =>
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(h);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        });
    }

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class HpComponentRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public HpComponentStatus Model { get; private set; } = null!;
    public string Id => Model.Id;
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

    private string? _busyActivateText;
    private string? _busyDeactivateText;

    public string ActivateButtonText => _busyActivateText ?? (Model.Kind is HpComponentKind.UwpBackgroundWatchdog
        or HpComponentKind.ScheduledTaskWatchdog
        or HpComponentKind.RegistryRunWatchdog
        ? "Enable"
        : "Activate");

    public string DeactivateButtonText => _busyDeactivateText ?? (Model.Kind is HpComponentKind.UwpBackgroundWatchdog
        or HpComponentKind.ScheduledTaskWatchdog
        or HpComponentKind.RegistryRunWatchdog
        ? "Block & Disable"
        : "Stop & Disable");

    public SolidColorBrush StatusDotBrush { get; private set; } = null!;
    public SolidColorBrush CategoryBrush { get; private set; } = null!;
    public SolidColorBrush StateBadgeBg { get; private set; } = null!;
    public SolidColorBrush StateBadgeFg { get; private set; } = null!;

    public HpComponentRowViewModel(HpComponentStatus model)
    {
        UpdateFrom(model);
    }

    public void UpdateFrom(HpComponentStatus model)
    {
        Model = model;
        _busyActivateText = null;
        _busyDeactivateText = null;
        StatusDotBrush = model.IsRunning ? BrushCache.RunningDot : BrushCache.StoppedDot;
        CategoryBrush = BrushCache.FromHex(model.CategoryColorHex);
        StateBadgeBg = model.IsRunning ? BrushCache.RunningBadgeBg : BrushCache.StoppedBadgeBg;
        StateBadgeFg = model.IsRunning ? BrushCache.RunningBadgeFg : BrushCache.StoppedBadgeFg;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetBusyState(bool isActivating)
    {
        if (isActivating)
            _busyActivateText = "⏳ Starting...";
        else
            _busyDeactivateText = "⏳ Stopping...";

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivateButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeactivateButtonText)));
    }
}

public partial class MainWindow : Window
{
    private sealed record CardVisualRefs(
        Border CardBorder,
        TextBlock TitleBlock,
        Border ActiveBadge,
        SolidColorBrush AccentBrush);

    private readonly HpPowerService _powerService = new();
    private readonly HpBloatManagerService _bloatService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<HpThermalMode, CardVisualRefs> _cardVisuals = [];
    private readonly ObservableCollection<HpComponentRowViewModel> _componentRows = [];
    private readonly Dictionary<string, HpComponentRowViewModel> _rowLookup = new(StringComparer.OrdinalIgnoreCase);

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private HpThermalMode? _selectedBiosMode;
    private bool _isExiting;
    private bool _isActionBusy;
    private bool _isSidebarExpanded = true;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSensors();
        ListComponents.ItemsSource = _componentRows;
        InitializeTrayIcon();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            // Skip polling while hidden to tray or minimized to save CPU/WMI overhead
            if (!IsVisible || WindowState == WindowState.Minimized || _isActionBusy)
                return;

            await RefreshAllDataAsync(updateStatusBar: false);
        };

        Loaded += async (_, _) =>
        {
            await RefreshAllDataAsync(updateStatusBar: true, showVisualLoading: true, loadingText: "Loading...");
            _refreshTimer.Start();
        };

        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            BtnTitleMaximize.Content = "🗗";
            BtnTitleMaximize.ToolTip = "Restore Down";
            var borderThickness = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = new Thickness(borderThickness.Left, borderThickness.Top, borderThickness.Right, borderThickness.Bottom);
        }
        else
        {
            BtnTitleMaximize.Content = "🗖";
            BtnTitleMaximize.ToolTip = "Maximize";
            RootGrid.Margin = new Thickness(0);
        }
    }

    private void BtnTitleMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void BtnTitleMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnTitleClose_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e) =>
        SetSidebarExpanded(!_isSidebarExpanded);

    private void SetSidebarExpanded(bool expanded)
    {
        _isSidebarExpanded = expanded;
        ColSidebar.Width = new GridLength(expanded ? 210 : 54);
        TxtToggleLabel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PanelNavProfiles.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PanelNavServices.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PanelNavSensors.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        RadioNavProfiles.ToolTip = expanded ? null : "Profiles & Power Modes";
        RadioNavServices.ToolTip = expanded ? null : "HP Services & Watchdogs";
        RadioNavSensors.ToolTip = expanded ? null : "Live Sensors & Heat";
        BtnToggleSidebar.ToolTip = expanded ? "Collapse navigation" : "Expand navigation";
    }

    private void NavRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (MainTabControl == null) return;

        if (ReferenceEquals(sender, RadioNavProfiles))
            MainTabControl.SelectedIndex = 0;
        else if (ReferenceEquals(sender, RadioNavServices))
            MainTabControl.SelectedIndex = 1;
        else if (ReferenceEquals(sender, RadioNavSensors))
            MainTabControl.SelectedIndex = 2;

        UpdateSensorTimerState();
    }

    private async Task RefreshAllDataAsync(bool updateStatusBar, bool showVisualLoading = false, string loadingText = "Refreshing...")
    {
        if (!await _refreshGate.WaitAsync(0))
            return;

        if (showVisualLoading)
            SetLoadingIndicator(true, loadingText);

        try
        {
            var sw = showVisualLoading ? Stopwatch.StartNew() : null;

            // Run both hardware snapshot and service/process queries concurrently in the background
            var snapTask = _powerService.GetSnapshotAsync();
            var statusesTask = _bloatService.GetAllStatusesAsync();

            await Task.WhenAll(snapTask, statusesTask);

            // Ensure manual/action refreshes remain visible for at least 220ms so the spinner doesn't flash for 1 frame
            if (sw != null && sw.ElapsedMilliseconds < 220)
                await Task.Delay(220 - (int)sw.ElapsedMilliseconds);

            var snap = await snapTask;
            var statuses = await statusesTask;

            if (snap.CurrentBiosMode.HasValue)
                _selectedBiosMode = snap.CurrentBiosMode;

            TxtModelName.Text = snap.ModelName;
            TxtBiosInfo.Text = $"BIOS: {snap.BiosVersion}  •  AC Overlay: {HpPowerService.FormatOverlayName(snap.AcOverlayMode)}  •  Battery Overlay: {HpPowerService.FormatOverlayName(snap.DcOverlayMode)}";

            if (snap.IsAdmin)
            {
                BadgeAdmin.Background = BrushCache.FromHex("#14532D");
                TxtAdminBadge.Text = "ADMIN • BIOS WMI READY";
                TxtAdminBadge.Foreground = BrushCache.FromHex("#86EFAC");
                BtnElevate.Visibility = Visibility.Collapsed;
            }
            else
            {
                BadgeAdmin.Background = BrushCache.FromHex("#7C2D12");
                TxtAdminBadge.Text = "LIMITED (NON-ADMIN)";
                TxtAdminBadge.Foreground = BrushCache.FromHex("#FDBA74");
                BtnElevate.Visibility = Visibility.Visible;
            }

            TxtActiveThermalIndicator.Text = _selectedBiosMode?.ToString() ?? "Unknown";
            TxtBatteryIndicator.Text = snap.BatteryStatusText;

            UpdateOrBuildThermalModeCards(snap);
            UpdateWindowsPowerButtons(snap);
            ApplyStatusesToUi(statuses);

            OverlayInitialLoading.Visibility = Visibility.Collapsed;

            if (updateStatusBar)
                TxtStatusBar.Text = snap.StatusMessage;
        }
        catch (Exception ex)
        {
            TxtStatusBar.Text = $"Refresh error: {ex.Message}";
        }
        finally
        {
            if (showVisualLoading && !_isActionBusy)
                SetLoadingIndicator(false);

            _refreshGate.Release();
        }
    }

    private void SetLoadingIndicator(bool isLoading, string? badgeText = null)
    {
        if (isLoading)
        {
            if (!string.IsNullOrEmpty(badgeText))
                TxtRefreshBadge.Text = badgeText;
            BadgeRefreshing.Visibility = Visibility.Visible;
            BarLoading.Visibility = Visibility.Visible;
        }
        else
        {
            BadgeRefreshing.Visibility = Visibility.Collapsed;
            BarLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateOrBuildThermalModeCards(SystemPowerSnapshot snap)
    {
        var options = HpPowerService.GetAllModeOptions(snap.SupportedBiosModes);

        // Build cards only once so hover states and clicks never flicker during background refreshes
        if (_cardVisuals.Count == 0)
        {
            GridThermalCards.Children.Clear();
            foreach (var opt in options)
            {
                var accentBrush = BrushCache.FromHex(opt.AccentHex);

                var cardBorder = new Border
                {
                    Background = BrushCache.InactiveCardBg,
                    BorderBrush = BrushCache.InactiveCardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 11, 14, 11),
                    Margin = new Thickness(4),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = opt.Mode
                };

                cardBorder.MouseEnter += (s, _) =>
                {
                    if (s is Border b && (HpThermalMode)b.Tag != _selectedBiosMode)
                        b.Background = BrushCache.HoverCardBg;
                };
                cardBorder.MouseLeave += (s, _) =>
                {
                    if (s is Border b && (HpThermalMode)b.Tag != _selectedBiosMode)
                        b.Background = BrushCache.InactiveCardBg;
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
                var titleBlock = new TextBlock
                {
                    Text = opt.Title,
                    FontSize = 14.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };
                titlePanel.Children.Add(titleBlock);
                Grid.SetColumn(titlePanel, 0);
                topGrid.Children.Add(titlePanel);

                var activeBadge = new Border
                {
                    Background = accentBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = "ACTIVE",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushCache.FromHex("#0B1120")
                    }
                };
                Grid.SetColumn(activeBadge, 1);
                topGrid.Children.Add(activeBadge);

                stack.Children.Add(topGrid);
                stack.Children.Add(new TextBlock
                {
                    Text = opt.Subtitle,
                    FontSize = 11.5,
                    Foreground = BrushCache.FromHex("#94A3B8"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5, 0, 0)
                });

                cardBorder.Child = stack;
                GridThermalCards.Children.Add(cardBorder);
                _cardVisuals[opt.Mode] = new CardVisualRefs(cardBorder, titleBlock, activeBadge, accentBrush);
            }
        }

        // Update visual state in-place without recreating controls
        foreach (var opt in options)
        {
            if (!_cardVisuals.TryGetValue(opt.Mode, out var refs))
                continue;

            bool isActive = _selectedBiosMode == opt.Mode;
            refs.CardBorder.Opacity = opt.IsSupported ? 1.0 : 0.55;
            refs.CardBorder.BorderBrush = isActive ? refs.AccentBrush : BrushCache.InactiveCardBorder;
            refs.CardBorder.BorderThickness = new Thickness(isActive ? 2 : 1);
            refs.CardBorder.Background = isActive
                ? BrushCache.ActiveCardBg
                : (refs.CardBorder.IsMouseOver ? BrushCache.HoverCardBg : BrushCache.InactiveCardBg);
            refs.TitleBlock.Foreground = isActive ? refs.AccentBrush : System.Windows.Media.Brushes.White;
            refs.ActiveBadge.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
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
            btn.Background = BrushCache.FromHex("#1E293B");
            btn.BorderBrush = BrushCache.FromHex(accentHex);
            btn.BorderThickness = new Thickness(1.5);
            btn.Foreground = BrushCache.FromHex(accentHex);
        }
        else
        {
            btn.Background = BrushCache.FromHex("#0F172A");
            btn.BorderBrush = BrushCache.FromHex("#334155");
            btn.BorderThickness = new Thickness(1);
            btn.Foreground = BrushCache.FromHex("#E2E8F0");
        }
    }

    private async Task RefreshServicesAndProcessesAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            var statuses = await _bloatService.GetAllStatusesAsync();
            ApplyStatusesToUi(statuses);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyStatusesToUi(IReadOnlyList<HpComponentStatus> statuses)
    {
        var procAndServiceItems = statuses
            .Where(s => s.Kind is HpComponentKind.WindowsService or HpComponentKind.ProcessGroup)
            .ToList();

        double totalRamMb = procAndServiceItems.Where(s => s.IsRunning).Sum(s => s.MemoryMb);
        int runningCount = procAndServiceItems.Count(s => s.IsRunning);

        TxtHpRamIndicator.Text = $"{totalRamMb:F1} MB ({runningCount} active)";
        TxtHpRamIndicator.Foreground = totalRamMb > 250
            ? BrushCache.FromHex("#F87171")
            : (totalRamMb > 100 ? BrushCache.FromHex("#FBBF24") : BrushCache.FromHex("#4ADE80"));

        int bloatRunning = statuses.Count(s =>
            s.IsRunning && s.Category is HpSafetyCategory.AutoLauncherWatchdog
                or HpSafetyCategory.TelemetryAndBloat
                or HpSafetyCategory.RedundantBackground);

        TxtServiceSummary.Text = bloatRunning > 0
            ? $"• {bloatRunning} watchdog/bloat item(s) currently active"
            : "• All respawn watchdogs, telemetry & bloat disabled";

        // Update existing rows in-place so items never flicker or disappear during refreshes
        foreach (var status in statuses)
        {
            if (_rowLookup.TryGetValue(status.Id, out var existingRow))
            {
                existingRow.UpdateFrom(status);
            }
            else
            {
                var newRow = new HpComponentRowViewModel(status);
                _rowLookup[status.Id] = newRow;
                _componentRows.Add(newRow);
            }
        }
    }

    private async void ThermalCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: HpThermalMode mode })
        {
            await ApplyThermalModeAsync(mode);
        }
    }

    private async Task ApplyThermalModeAsync(HpThermalMode mode)
    {
        if (_isActionBusy) return;
        _isActionBusy = true;
        SetLoadingIndicator(true, $"Switching to {mode}...");

        try
        {
            // Optimistically highlight selected mode immediately for instant UI feedback
            var prevMode = _selectedBiosMode;
            _selectedBiosMode = mode;
            TxtActiveThermalIndicator.Text = mode.ToString();
            TxtStatusBar.Text = $"Switching HP BIOS thermal profile to {mode}...";

            bool syncWin = ChkSyncWinOverlay.IsChecked == true;
            var (ok, msg, _) = await _powerService.SetHpThermalModeAsync(mode, syncWin);
            if (!ok)
                _selectedBiosMode = prevMode;

            await RefreshAllDataAsync(updateStatusBar: false, showVisualLoading: true, loadingText: "Syncing BIOS...");
            TxtStatusBar.Text = msg;
        }
        finally
        {
            _isActionBusy = false;
            SetLoadingIndicator(false);
        }
    }

    private async void BtnWinEco_Click(object sender, RoutedEventArgs e) =>
        await ApplyWindowsOverlayAsync(WindowsOverlayPowerMode.BestPowerEfficiency);

    private async void BtnWinBalanced_Click(object sender, RoutedEventArgs e) =>
        await ApplyWindowsOverlayAsync(WindowsOverlayPowerMode.Balanced);

    private async void BtnWinPerf_Click(object sender, RoutedEventArgs e) =>
        await ApplyWindowsOverlayAsync(WindowsOverlayPowerMode.BestPerformance);

    private async Task ApplyWindowsOverlayAsync(WindowsOverlayPowerMode mode)
    {
        if (_isActionBusy) return;
        _isActionBusy = true;
        SetLoadingIndicator(true, "Updating Power Mode...");

        try
        {
            bool both = ChkApplyBothAcDc.IsChecked == true;
            bool isAc = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            var (_, msg, _) = await _powerService.SetOverlayPowerModeAsync(mode, applyToAc: both || isAc, applyToDc: both || !isAc);

            await RefreshAllDataAsync(updateStatusBar: false, showVisualLoading: true, loadingText: "Updating Power Mode...");
            TxtStatusBar.Text = msg;
        }
        finally
        {
            _isActionBusy = false;
            SetLoadingIndicator(false);
        }
    }

    private async void BtnActivateItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: HpComponentStatus item } btn)
        {
            _isActionBusy = true;
            btn.IsEnabled = false;
            if (_rowLookup.TryGetValue(item.Id, out var rowVm))
                rowVm.SetBusyState(isActivating: true);
            SetLoadingIndicator(true, $"Activating {item.DisplayName}...");
            TxtStatusBar.Text = $"Activating '{item.DisplayName}'...";

            try
            {
                var sw = Stopwatch.StartNew();
                var (_, msg) = await _bloatService.ActivateComponentAsync(item);
                await RefreshServicesAndProcessesAsync();
                if (sw.ElapsedMilliseconds < 250)
                    await Task.Delay(250 - (int)sw.ElapsedMilliseconds);
                TxtStatusBar.Text = msg;
            }
            finally
            {
                _isActionBusy = false;
                SetLoadingIndicator(false);
            }
        }
    }

    private async void BtnDeactivateItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: HpComponentStatus item } btn)
        {
            _isActionBusy = true;
            btn.IsEnabled = false;
            if (_rowLookup.TryGetValue(item.Id, out var rowVm))
                rowVm.SetBusyState(isActivating: false);
            SetLoadingIndicator(true, $"Disabling {item.DisplayName}...");
            TxtStatusBar.Text = $"Stopping & disabling '{item.DisplayName}'...";

            try
            {
                var sw = Stopwatch.StartNew();
                var (_, msg) = await _bloatService.DeactivateComponentAsync(item);
                await RefreshServicesAndProcessesAsync();
                if (sw.ElapsedMilliseconds < 250)
                    await Task.Delay(250 - (int)sw.ElapsedMilliseconds);
                TxtStatusBar.Text = msg;
            }
            finally
            {
                _isActionBusy = false;
                SetLoadingIndicator(false);
            }
        }
    }

    private async void BtnDisableAllBloat_Click(object sender, RoutedEventArgs e)
    {
        if (_isActionBusy) return;
        _isActionBusy = true;
        BtnDisableAllBloat.IsEnabled = false;
        object originalContent = BtnDisableAllBloat.Content;
        BtnDisableAllBloat.Content = "⏳ Stopping Bloat & Watchdogs...";
        SetLoadingIndicator(true, "Stopping watchdogs & bloat...");

        try
        {
            var sw = Stopwatch.StartNew();
            TxtStatusBar.Text = "Stopping and disabling watchdogs, telemetry & redundant HP background services...";
            var (_, _, summary) = await _bloatService.DisableAllTelemetryAndBloatAsync();
            await RefreshServicesAndProcessesAsync();
            if (sw.ElapsedMilliseconds < 300)
                await Task.Delay(300 - (int)sw.ElapsedMilliseconds);
            TxtStatusBar.Text = summary;
        }
        finally
        {
            BtnDisableAllBloat.Content = originalContent;
            BtnDisableAllBloat.IsEnabled = true;
            _isActionBusy = false;
            SetLoadingIndicator(false);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        object originalContent = BtnRefresh.Content;
        BtnRefresh.Content = "⏳ Refreshing...";

        try
        {
            await RefreshAllDataAsync(updateStatusBar: false, showVisualLoading: true, loadingText: "Refreshing...");
            TxtStatusBar.Text = $"Refreshed all hardware, watchdog & service indicators at {DateTime.Now:HH:mm:ss}.";
        }
        finally
        {
            BtnRefresh.Content = originalContent;
            BtnRefresh.IsEnabled = true;
        }
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
        menu.Items.Add("Open Power Switcher", null, (_, _) => Dispatcher.InvokeAsync(ShowMainWindow));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("⚡ Smart Sense", null, (_, _) => Dispatcher.InvokeAsync(() => ApplyThermalModeAsync(HpThermalMode.SmartSense)));
        menu.Items.Add("⚖ Balanced", null, (_, _) => Dispatcher.InvokeAsync(() => ApplyThermalModeAsync(HpThermalMode.Balanced)));
        menu.Items.Add("🚀 Performance", null, (_, _) => Dispatcher.InvokeAsync(() => ApplyThermalModeAsync(HpThermalMode.Performance)));
        menu.Items.Add("❄ Cool", null, (_, _) => Dispatcher.InvokeAsync(() => ApplyThermalModeAsync(HpThermalMode.Cool)));
        menu.Items.Add("🌙 Quiet", null, (_, _) => Dispatcher.InvokeAsync(() => ApplyThermalModeAsync(HpThermalMode.Quiet)));
        menu.Items.Add("🍃 Power Saver", null, (_, _) => Dispatcher.InvokeAsync(() => ApplyThermalModeAsync(HpThermalMode.PowerSaver)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("🧹 Stop Telemetry & Bloat", null, (_, _) => Dispatcher.InvokeAsync(() => BtnDisableAllBloat_Click(this, new RoutedEventArgs())));
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _isExiting = true;
            Dispatcher.InvokeAsync(Close);
        });

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.InvokeAsync(ShowMainWindow);
    }

    private async void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        await RefreshAllDataAsync(updateStatusBar: false);
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
        _sensorTimer?.Stop();
        _sensors.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
    }
}
