using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using HpPowerSwitcher.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HpPowerSwitcher;

public static class BrushCache
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);
    public static SolidColorBrush FromHex(string hex)
    {
        if (Brushes.TryGetValue(hex, out var brush)) return brush;
        uint rgb = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        Brushes.Add(hex, brush);
        return brush;
    }
}

public sealed class HpComponentRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public HpComponentStatus Model { get; private set; }
    private bool _busy;
    public string DisplayName => Model.DisplayName;
    public string Description => Model.Description;
    public string CategoryBadge => Model.CategoryBadge;
    public SolidColorBrush CategoryBrush { get; }
    public string StatusDisplay => $"{Model.StateText} · {Model.StartModeText} · {Model.MemoryDisplay}" +
        (string.IsNullOrEmpty(Model.CpuDisplay) ? "" : $" · {Model.CpuDisplay}");
    private bool IsWatchdog => Model.Kind is HpComponentKind.UwpBackgroundWatchdog
        or HpComponentKind.ScheduledTaskWatchdog or HpComponentKind.RegistryRunWatchdog;
    public string ActivateText => _busy ? "Working…" : IsWatchdog ? "Enable" : "Activate";
    public string DeactivateText => _busy ? "Working…" : IsWatchdog ? "Block & disable" : "Stop & disable";
    public bool CanActivate => !_busy && Model.CanActivate;
    public bool CanDeactivate => !_busy && Model.CanDeactivate;
    public HpComponentRowViewModel(HpComponentStatus model)
    {
        Model = model;
        CategoryBrush = BrushCache.FromHex(model.CategoryColorHex);
    }
    public void Update(HpComponentStatus model)
    {
        if (Model == model) return;
        Model = model;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public void SetBusy(bool busy)
    {
        _busy = busy;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public partial class MainWindow : Window
{
    private readonly HpPowerService _powerService = new();
    private readonly HpBloatManagerService _bloatService = new();
    private readonly SemaphoreSlim _dataGate = new(1, 1);
    private readonly ObservableCollection<HpComponentRowViewModel> _rows = [];
    private readonly Dictionary<string, HpComponentRowViewModel> _rowLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<HpThermalMode, Button> _modeButtons = [];
    private readonly Dictionary<HpThermalMode, Border> _modeBadges = [];
    private readonly DispatcherQueueTimer _refreshTimer;
    private Services.TrayIcon? _tray;
    private string _page = "Profiles";
    private bool _ready, _closing, _allowClose, _actionBusy;
    private SystemPowerSnapshot? _snapshot;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 166, 188, 207);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 35, 56, 73);
        GridThermalCards.SizeChanged += (_, _) => UpdateProfileLayout();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(1060 * scale), (int)(workArea.Width * 0.95)),
            Math.Min((int)(790 * scale), (int)(workArea.Height * 0.95))));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        ListComponents.ItemsSource = _rows;
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(8);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        AppWindow.Changed += (_, _) => UpdatePolling();
        AppWindow.Closing += Window_Closing;
        RootGrid.Loaded += async (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            Navigation.SelectedItem = Navigation.MenuItems[0];
            try
            {
                _tray = new Services.TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), ShowMainWindow,
                    async mode => await RunActionAsync(async () =>
                        (await _powerService.SetHpThermalModeAsync(mode, ChkSyncWinOverlay.IsChecked == true)).Message),
                    async () => await DisableBloatAsync(), () => { _allowClose = false; _exitRequested = true; Close(); });
            }
            catch (Exception ex) { TxtStatusBar.Text = $"Tray unavailable: {ex.Message}"; }
            await RefreshAsync();
            UpdatePolling();
        };
    }

    private bool _exitRequested;
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    private bool WindowVisible => AppWindow.IsVisible && !_closing &&
        (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State != OverlappedPresenterState.Minimized);

    private void UpdatePolling()
    {
        if (!_ready) return;
        if (WindowVisible && !_actionBusy && _page != "Sensors") _refreshTimer.Start();
        else _refreshTimer.Stop();
        UpdateSensorPolling();
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string page }) return;
        _page = page;
        TxtPageEyebrow.Text = page == "Profiles" ? "DEVICE CONTROL / POWER" : page == "Services" ? "DEVICE CONTROL / BACKGROUND" : "DEVICE CONTROL / TELEMETRY";
        TxtPageTitle.Text = page == "Profiles" ? "Power & performance" : page == "Services" ? "Keep the essentials." : "Your device, in detail.";
        TxtPageDescription.Text = page == "Profiles" ? "Find your balance. Make every charge count." : page == "Services" ? "Take control of what runs behind the scenes." : "A live look at performance, temperature, and power.";
        ProfilesView.Visibility = page == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        ServicesView.Visibility = page == "Services" ? Visibility.Visible : Visibility.Collapsed;
        SensorHost.Visibility = page == "Sensors" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Sensors") EnsureSensorPage();
        UpdatePolling();
        if (_ready && page != "Sensors") await RefreshAsync();
    }

    // Only the visible page queries its data. A single gate serializes polling and all mutations.
    private async Task RefreshAsync()
    {
        if (!WindowVisible || _actionBusy || !await _dataGate.WaitAsync(0)) return;
        try { await RefreshCoreAsync(); }
        catch (Exception ex) { if (!_closing) TxtStatusBar.Text = $"Refresh error: {ex.Message}"; }
        finally { _dataGate.Release(); }
    }

    private async Task RefreshCoreAsync(bool refreshServices = false)
    {
        string page = _page;
        if (page == "Profiles" || _snapshot == null)
        {
            var snap = await _powerService.GetSnapshotAsync();
            if (_closing) return;
            _snapshot = snap;
            TxtModelName.Text = snap.ModelName;
            TxtBiosInfo.Text = $"BIOS {snap.BiosVersion} · AC: {HpPowerService.FormatOverlayName(snap.AcOverlayMode)} · Battery: {HpPowerService.FormatOverlayName(snap.DcOverlayMode)}";
            TxtAdminBadge.Text = snap.IsAdmin ? "Administrator" : "Limited access";
            TxtBatteryIndicator.Text = snap.BatteryStatusText;
            TxtActiveThermalIndicator.Text = snap.CurrentBiosMode?.ToString() ?? "Unavailable";
            UpdatePowerControls(snap);
            TxtStatusBar.Text = snap.StatusMessage;
        }
        if (page == "Services" || refreshServices)
        {
            var statuses = await _bloatService.GetAllStatusesAsync();
            if (_closing) return;
            ApplyStatuses(statuses);
        }
    }

    private void UpdatePowerControls(SystemPowerSnapshot snap)
    {
        if (_modeButtons.Count == 0)
        {
            int index = 0;
            int columns = GridThermalCards.ColumnDefinitions.Count;
            foreach (var option in HpPowerService.GetAllModeOptions(snap.SupportedBiosModes))
            {
                var (glyph, color, description) = option.Mode switch
                {
                    HpThermalMode.SmartSense => ("\uE945", "#86E8CB", "Automatically adapts to your workload."),
                    HpThermalMode.Balanced => ("\uE9D9", "#A9BCFF", "Everyday speed. Comfortable thermals."),
                    HpThermalMode.Performance => ("\uE7C1", "#FFB98C", "More power for demanding work."),
                    HpThermalMode.Cool => ("\uE9CA", "#8ACFFF", "Lower temperatures. Cooler surfaces."),
                    HpThermalMode.Quiet => ("\uE74F", "#C8ABF5", "Less fan noise. Fewer distractions."),
                    _ => ("\uE83E", "#BDD895", "A little less power. More battery life.")
                };
                var stack = new StackPanel { Spacing = 8 };
                var top = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                var icon = new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(11),
                    Background = BrushCache.FromHex("#20313F"), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new FontIcon { Glyph = glyph, FontSize = 18, Foreground = BrushCache.FromHex(color) }
                };
                var badge = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Background = BrushCache.FromHex("#244B48"), CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6, 3, 6, 3), Visibility = Visibility.Collapsed,
                    Child = new TextBlock { Text = "ACTIVE", FontSize = 8, CharacterSpacing = 90,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = BrushCache.FromHex("#A2F3D9") }
                };
                top.Children.Add(icon); top.Children.Add(badge);
                _modeBadges[option.Mode] = badge;
                stack.Children.Add(top);
                stack.Children.Add(new TextBlock { Text = option.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                stack.Children.Add(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = BrushCache.FromHex("#99AFC2"), MinHeight = 34 });
                var button = new Button
                {
                    Content = stack, Tag = option.Mode, Style = (Style)Application.Current.Resources["ProfileCardStyle"],
                    IsEnabled = option.IsSupported
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, option.Title);
                ToolTipService.SetToolTip(button, option.IsSupported ? option.Subtitle : "Not reported as supported by this BIOS");
                button.Click += async (sender, _) =>
                {
                    if (sender is Button { Tag: HpThermalMode mode })
                        await RunActionAsync(async () =>
                            (await _powerService.SetHpThermalModeAsync(mode, ChkSyncWinOverlay.IsChecked == true)).Message);
                };
                if (index % columns == 0) GridThermalCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(button, index % columns);
                Grid.SetRow(button, index++ / columns);
                GridThermalCards.Children.Add(button);
                _modeButtons[option.Mode] = button;
            }
        }
        foreach (var (mode, button) in _modeButtons)
        {
            bool selected = mode == snap.CurrentBiosMode;
            button.BorderBrush = BrushCache.FromHex(selected ? "#86E8CB" : "#263747");
            button.Background = BrushCache.FromHex(selected ? "#17343B" : "#121E2B");
            _modeBadges[mode].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
        var active = snap.IsOnAcPower ? snap.AcOverlayMode : snap.DcOverlayMode;
        TxtWinOverlaySub.Text = $"{(snap.IsOnAcPower ? "Plugged in" : "On battery")} · {HpPowerService.FormatOverlayName(active)}";
        StyleOverlay(BtnWinEco, active == WindowsOverlayPowerMode.BestPowerEfficiency);
        StyleOverlay(BtnWinBalanced, active == WindowsOverlayPowerMode.Balanced);
        StyleOverlay(BtnWinPerf, active == WindowsOverlayPowerMode.BestPerformance);
    }

    private static void StyleOverlay(Button button, bool selected)
    {
        button.Background = BrushCache.FromHex(selected ? "#21423F" : "#0D1824");
        button.BorderBrush = BrushCache.FromHex(selected ? "#71BCA8" : "#293D4F");
        button.Foreground = BrushCache.FromHex(selected ? "#A6F3DC" : "#9FB4C6");
    }

    private void UpdateProfileLayout()
    {
        int columns = GridThermalCards.ActualWidth >= 680 ? 3 : GridThermalCards.ActualWidth >= 390 ? 2 : 1;
        if (GridThermalCards.ColumnDefinitions.Count == columns) return;
        GridThermalCards.ColumnDefinitions.Clear();
        for (int i = 0; i < columns; i++) GridThermalCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        GridThermalCards.RowDefinitions.Clear();
        for (int i = 0; i < GridThermalCards.Children.Count; i++)
        {
            if (i % columns == 0) GridThermalCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)GridThermalCards.Children[i], i % columns);
            Grid.SetRow((FrameworkElement)GridThermalCards.Children[i], i / columns);
        }
    }
    private void ApplyStatuses(IReadOnlyList<HpComponentStatus> statuses)
    {
        double memory = 0;
        int active = 0;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses)
        {
            present.Add(status.Id);
            if (status.Kind is HpComponentKind.WindowsService or HpComponentKind.ProcessGroup && status.IsRunning)
            { memory += status.MemoryMb; active++; }
            if (_rowLookup.TryGetValue(status.Id, out var row)) row.Update(status);
            else { row = new HpComponentRowViewModel(status); _rowLookup.Add(status.Id, row); _rows.Add(row); }
        }
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!present.Contains(_rows[i].Model.Id)) { _rowLookup.Remove(_rows[i].Model.Id); _rows.RemoveAt(i); }
        TxtServiceSummary.Text = $"HP processes and services: {memory:F1} MB · {active} active";
    }

    private async Task RunActionAsync(Func<Task<string>> action, bool services = false)
    {
        if (_actionBusy || _closing) return;
        _actionBusy = true;
        UpdatePolling();
        BarLoading.Visibility = Visibility.Visible;
        BarLoading.IsIndeterminate = true;
        Navigation.IsEnabled = false;
        BtnRefresh.IsEnabled = false;
        await _dataGate.WaitAsync();
        try
        {
            if (_closing) return;
            string message = await action();
            await RefreshCoreAsync(services);
            if (!_closing) TxtStatusBar.Text = message;
        }
        catch (Exception ex) { if (!_closing) TxtStatusBar.Text = $"Action failed: {ex.Message}"; }
        finally
        {
            _dataGate.Release();
            _actionBusy = false;
            if (!_closing)
            {
                Navigation.IsEnabled = true;
                BtnRefresh.IsEnabled = true;
                BarLoading.IsIndeterminate = false;
                BarLoading.Visibility = Visibility.Collapsed;
                UpdatePolling();
            }
        }
    }

    private async void Overlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<WindowsOverlayPowerMode>(tag, out var mode)) return;
        bool both = ChkApplyBothAcDc.IsChecked == true;
        await RunActionAsync(async () =>
        {
            bool ac = PowerStatus.Read().IsOnAc;
            return (await _powerService.SetOverlayPowerModeAsync(mode, both || ac, both || !ac)).Message;
        });
    }
    private async void ActivateItem_Click(object sender, RoutedEventArgs e) => await ChangeComponentAsync(sender, true);
    private async void DeactivateItem_Click(object sender, RoutedEventArgs e) => await ChangeComponentAsync(sender, false);
    private async Task ChangeComponentAsync(object sender, bool activate)
    {
        if (_actionBusy || sender is not Button { Tag: HpComponentRowViewModel row }) return;
        row.SetBusy(true);
        try
        {
            await RunActionAsync(async () => (activate ? await _bloatService.ActivateComponentAsync(row.Model)
                : await _bloatService.DeactivateComponentAsync(row.Model)).Message, true);
        }
        finally { row.SetBusy(false); }
    }
    private async void BtnDisableAllBloat_Click(object sender, RoutedEventArgs e) => await DisableBloatAsync();
    private Task DisableBloatAsync() => RunActionAsync(async () => (await _bloatService.DisableAllTelemetryAndBloatAsync()).Summary, true);
    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (_page == "Sensors") StartSensorSample();
    }
    private async void ShowMainWindow()
    {
        if (_closing) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
        UpdatePolling();
        await RefreshAsync();
    }
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        if (!_exitRequested && ChkMinimizeToTray.IsChecked == true && _tray != null)
        { AppWindow.Hide(); UpdatePolling(); return; }
        _closing = true;
        _refreshTimer.Stop();
        _sensorTimer?.Stop();
        // Never dispose counters while a worker is still sampling them.
        await _sensorTask;
        await _dataGate.WaitAsync();
        try
        {
            if (_sensors != null) await Task.Run(_sensors.Dispose);
            _tray?.Dispose();
            _allowClose = true;
            Close();
        }
        finally { _dataGate.Release(); }
    }
}
