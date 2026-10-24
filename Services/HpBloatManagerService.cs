using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HpPowerSwitcher.Services;

public enum HpComponentKind
{
    WindowsService,
    ProcessGroup,
    UwpBackgroundWatchdog,
    ScheduledTaskWatchdog,
    RegistryRunWatchdog
}

public enum HpSafetyCategory
{
    AutoLauncherWatchdog,
    TelemetryAndBloat,
    RedundantBackground,
    OptionalService,
    CoreHardware
}

public sealed record HpComponentStatus(
    string Id,
    string DisplayName,
    string Description,
    HpComponentKind Kind,
    HpSafetyCategory Category,
    bool IsRunning,
    string StateText,
    string StartModeText,
    double MemoryMb,
    double CpuSeconds,
    int ProcessCount,
    bool CanActivate,
    bool CanDeactivate)
{
    public string CategoryBadge => Category switch
    {
        HpSafetyCategory.AutoLauncherWatchdog => "Respawn Watchdog / Auto-Launcher",
        HpSafetyCategory.TelemetryAndBloat => "Telemetry / Safe to Disable",
        HpSafetyCategory.RedundantBackground => "Redundant Bloat",
        HpSafetyCategory.OptionalService => "Optional",
        HpSafetyCategory.CoreHardware => "Core Hardware",
        _ => "Other"
    };

    public string CategoryColorHex => Category switch
    {
        HpSafetyCategory.AutoLauncherWatchdog => "#C084FC",
        HpSafetyCategory.TelemetryAndBloat => "#F87171",
        HpSafetyCategory.RedundantBackground => "#FB923C",
        HpSafetyCategory.OptionalService => "#FBBF24",
        HpSafetyCategory.CoreHardware => "#38BDF8",
        _ => "#94A3B8"
    };

    public string MemoryDisplay => Kind is HpComponentKind.UwpBackgroundWatchdog
        or HpComponentKind.ScheduledTaskWatchdog
        or HpComponentKind.RegistryRunWatchdog
        ? "Trigger"
        : (MemoryMb > 0.05 ? $"{MemoryMb:F1} MB" : "0 MB");

    public string CpuDisplay => CpuSeconds > 0.1 ? $"{CpuSeconds:F0}s CPU" : "";
}

public sealed class HpBloatManagerService
{
    private sealed record ServiceDefinition(
        string ServiceName,
        string FriendlyName,
        string Purpose,
        HpSafetyCategory Category);

    private sealed record ProcessGroupDefinition(
        string GroupId,
        string FriendlyName,
        string Purpose,
        HpSafetyCategory Category,
        string[] ProcessNames,
        string? TaskNamePattern,
        string? LaunchExecutablePath);

    private static readonly string[] HpUwpPackageFamilies =
    [
        "AD2F1837.myHP_v10z8vjag6ke6",
        "AD2F1837.HPThermalControl_v10z8vjag6ke6",
        "AD2F1837.OMENCommandCenter_v10z8vjag6ke6",
        "AD2F1837.HPEnhance_v10z8vjag6ke6",
        "AD2F1837.HPSupportAssistant_v10z8vjag6ke6",
        "AD2F1837.HPPrinterControl_v10z8vjag6ke6",
        "AD2F1837.HPPCHardwareDiagnosticsWindows_v10z8vjag6ke6"
    ];

    private static readonly (string PackageFamily, string StartupTaskId)[] HpUwpStartupTasks =
    [
        ("AD2F1837.myHP_v10z8vjag6ke6", "HPAudioSwitch"),
        ("AD2F1837.HPPCHardwareDiagnosticsWindows_v10z8vjag6ke6", "HPCDXSupportWnsRegistration"),
        ("AD2F1837.OMENCommandCenter_v10z8vjag6ke6", "OmenBackground")
    ];

    private static readonly string[] HpRegistryRunNames =
    [
        "HPCC_InstallationBooster",
        "HPSEU_Host_Launcher"
    ];

    private static readonly string[] HpScheduledTaskFolders =
    [
        @"\",
        @"\HP\HP Print Scan Doctor",
        @"\Hewlett-Packard\HP Support Assistant"
    ];

    private static readonly ServiceDefinition[] KnownServices =
    [
        new("HPSysInfoCap", "HP System Info & Service Watchdog", "Watchdog inside SysInfoCap.exe actively restarts HPDiagsCap, HPNetworkCap & HPAppHelperCap and sends analytics. Not needed by HpPowerSwitcher (only needed if you open myHP).", HpSafetyCategory.AutoLauncherWatchdog),
        new("HPAppHelperCap", "HP App Helper & Process Launcher", "Hosts LauncherService.dll which spawns background HP apps (myHP, ContextAware, SystemControl) from system services.", HpSafetyCategory.AutoLauncherWatchdog),
        new("HpTouchpointAnalyticsService", "HP Insights Analytics (Telemetry)", "Collects and uploads background device telemetry to HP servers.", HpSafetyCategory.TelemetryAndBloat),
        new("HPPrintScanDoctorService", "HP Print Scan Doctor Service", "Background troubleshooting service for HP printers/scanners.", HpSafetyCategory.TelemetryAndBloat),
        new("HPAppPrintScanDoctorService", "HP App Print Scan Doctor Service", "Secondary print/scan diagnostics service from myHP.", HpSafetyCategory.TelemetryAndBloat),
        new("HPDiagsCap", "HP Diagnostics HSA Service", "Hardware diagnostics & telemetry capability service (restarted by HPSysInfoCap if not disabled).", HpSafetyCategory.TelemetryAndBloat),
        new("HPNetworkCap", "HP Network HSA Service", "Cloud registration & Network Booster traffic service (restarted by HPSysInfoCap if not disabled).", HpSafetyCategory.TelemetryAndBloat),
        new("HP Comm Recover", "HP Comm Recovery Service", "Network/Bluetooth connection reset helper service.", HpSafetyCategory.OptionalService),
        new("HPDCService", "HP Display Control Service", "Portrait Displays color profile service.", HpSafetyCategory.CoreHardware)
    ];

    private static readonly ProcessGroupDefinition[] KnownProcessGroups =
    [
        new(
            GroupId: "proc_omen",
            FriendlyName: "OMEN Gaming Hub Background & Tasks",
            Purpose: "Runs 50+ named pipes & game monitors continuously (~210 MB RAM, 770s+ CPU) on non-OMEN Spectre laptops via OmenOverlay/OmenInstallMonitor tasks & HpseuHostLauncher.",
            Category: HpSafetyCategory.RedundantBackground,
            ProcessNames: ["OmenCommandCenterBackground", "OmenInstallMonitor", "OverlayHelper", "OMENOverlay"],
            TaskNamePattern: "Omen",
            LaunchExecutablePath: null),
        new(
            GroupId: "proc_hpcc",
            FriendlyName: "Legacy HP Thermal Control Background",
            Purpose: "Older HP Command Center background daemons (HPCC.Bg.BackgroundApp / Sys) respawned by HpccLauncher & UWP power events.",
            Category: HpSafetyCategory.RedundantBackground,
            ProcessNames: ["HPCC.Bg.BackgroundApp", "HPCC.Bg.BackgroundSys", "HpccLauncher"],
            TaskNamePattern: null,
            LaunchExecutablePath: null),
        new(
            GroupId: "proc_myhp",
            FriendlyName: "myHP App & ContextAware Daemons",
            Purpose: "WebView2 myHP UI (HP.HPX), ContextAware, and SystemControl background processes respawned by UWP VerifyAutoLaunchTask.",
            Category: HpSafetyCategory.RedundantBackground,
            ProcessNames: ["HP.HPX", "HP.ContextAware", "HP.SystemControl.Background", "HP.SystemControl.SysBackground"],
            TaskNamePattern: null,
            LaunchExecutablePath: null),
        new(
            GroupId: "proc_media",
            FriendlyName: "HP Media Network, Print Monitor & Lighting",
            Purpose: "Background media sharing, webcam ring-light overlay, and Printer Health Monitor processes.",
            Category: HpSafetyCategory.TelemetryAndBloat,
            ProcessNames: ["HPMediaNetwork", "HPEnhancedLighting.Bg", "HPPrinterHealthMonitor", "HPWarrantyChecker", "HPCDXSupport"],
            TaskNamePattern: null,
            LaunchExecutablePath: @"C:\Program Files\HP\HP Media Network\HPMediaNetwork.exe")
    ];

    public Task<IReadOnlyList<HpComponentStatus>> GetAllStatusesAsync() =>
        Task.Run(GetAllStatusesCore);

    public Task<(bool Success, string Message)> ActivateComponentAsync(HpComponentStatus item) =>
        Task.Run(() => ActivateComponentCore(item));

    public Task<(bool Success, string Message)> DeactivateComponentAsync(HpComponentStatus item) =>
        Task.Run(() => DeactivateComponentCore(item));

    public Task<(int Count, double FreedMb, string Summary)> DisableAllTelemetryAndBloatAsync() =>
        Task.Run(DisableAllTelemetryAndBloatCore);

    private IReadOnlyList<HpComponentStatus> GetAllStatusesCore()
    {
        var results = new List<HpComponentStatus>(3 + KnownProcessGroups.Length + KnownServices.Length);

        // 1. Auto-Launcher & Respawn Watchdog Controls
        results.Add(GetUwpBackgroundWatchdogStatus());
        results.Add(GetScheduledTaskWatchdogStatus());
        results.Add(GetRegistryRunWatchdogStatus());

        // 2. Targeted Process Groups
        var (_, _, omenTasksEnabled) = GetHpScheduledTasksSnapshot();

        foreach (var group in KnownProcessGroups)
        {
            double memMb = 0;
            double cpuSec = 0;
            int count = 0;

            foreach (string procName in group.ProcessNames)
            {
                Process[]? matches = null;
                try
                {
                    matches = Process.GetProcessesByName(procName);
                    foreach (var p in matches)
                    {
                        count++;
                        try { memMb += p.WorkingSet64 / (1024.0 * 1024.0); } catch { }
                        try { cpuSec += p.TotalProcessorTime.TotalSeconds; } catch { }
                    }
                }
                catch { }
                finally
                {
                    if (matches != null)
                    {
                        foreach (var p in matches)
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
            }

            bool isRunning = count > 0;
            string startMode = group.TaskNamePattern == "Omen"
                ? (omenTasksEnabled ? "Task: Enabled" : "Task: Disabled")
                : "Background App";

            bool canActivate = group.TaskNamePattern == "Omen"
                ? (!isRunning || !omenTasksEnabled)
                : (!isRunning && group.LaunchExecutablePath != null);

            bool canDeactivate = group.TaskNamePattern == "Omen"
                ? (isRunning || omenTasksEnabled)
                : isRunning;

            results.Add(new HpComponentStatus(
                Id: group.GroupId,
                DisplayName: group.FriendlyName,
                Description: group.Purpose,
                Kind: HpComponentKind.ProcessGroup,
                Category: group.Category,
                IsRunning: isRunning,
                StateText: isRunning ? $"Running ({count} proc)" : "Stopped",
                StartModeText: startMode,
                MemoryMb: memMb,
                CpuSeconds: cpuSec,
                ProcessCount: count,
                CanActivate: canActivate,
                CanDeactivate: canDeactivate));
        }

        // 3. Fast, 100% deterministic native SCM + Registry query for all KnownServices (never times out or drops items like WMI)
        IntPtr scmHandle = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        try
        {
            foreach (var svc in KnownServices)
            {
                var (exists, stateText, startModeText, isRunning, isDisabled, pid) =
                    QueryServiceNative(scmHandle, svc.ServiceName);

                if (!exists)
                    continue;

                double memMb = 0;
                double cpuSec = 0;

                if (isRunning && pid > 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        try { memMb = p.WorkingSet64 / (1024.0 * 1024.0); } catch { }
                        try { cpuSec += p.TotalProcessorTime.TotalSeconds; } catch { }
                    }
                    catch { }
                }

                results.Add(new HpComponentStatus(
                    Id: svc.ServiceName,
                    DisplayName: svc.FriendlyName,
                    Description: $"{svc.Purpose} [Service: {svc.ServiceName}]",
                    Kind: HpComponentKind.WindowsService,
                    Category: svc.Category,
                    IsRunning: isRunning,
                    StateText: stateText,
                    StartModeText: $"Startup: {startModeText}",
                    MemoryMb: memMb,
                    CpuSeconds: cpuSec,
                    ProcessCount: isRunning ? 1 : 0,
                    CanActivate: !isRunning || isDisabled,
                    CanDeactivate: isRunning || !isDisabled));
            }
        }
        finally
        {
            if (scmHandle != IntPtr.Zero)
                CloseServiceHandle(scmHandle);
        }

        return results;
    }

    private static (bool Exists, string StateText, string StartModeText, bool IsRunning, bool IsDisabled, int Pid) QueryServiceNative(IntPtr scmHandle, string serviceName)
    {
        int startValue = -1;
        try
        {
            using var regKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
            if (regKey == null)
                return (false, "Not Installed", "None", false, true, 0);

            startValue = Convert.ToInt32(regKey.GetValue("Start") ?? 3);
        }
        catch
        {
            return (false, "Unknown", "Unknown", false, false, 0);
        }

        string startModeText = startValue switch
        {
            2 => "Auto",
            3 => "Manual",
            4 => "Disabled",
            _ => "Manual"
        };
        bool isDisabled = startValue == 4;

        string stateText = "Stopped";
        bool isRunning = false;
        int pid = 0;

        if (scmHandle != IntPtr.Zero)
        {
            IntPtr svcHandle = OpenServiceW(scmHandle, serviceName, SERVICE_QUERY_STATUS);
            if (svcHandle != IntPtr.Zero)
            {
                try
                {
                    if (QueryServiceStatusEx(
                        svcHandle,
                        SC_STATUS_PROCESS_INFO,
                        out SERVICE_STATUS_PROCESS status,
                        Marshal.SizeOf<SERVICE_STATUS_PROCESS>(),
                        out _))
                    {
                        pid = (int)status.dwProcessId;
                        isRunning = status.dwCurrentState == SERVICE_RUNNING;
                        stateText = status.dwCurrentState switch
                        {
                            SERVICE_RUNNING => "Running",
                            SERVICE_START_PENDING => "Starting",
                            SERVICE_STOP_PENDING => "Stopping",
                            SERVICE_PAUSED => "Paused",
                            _ => "Stopped"
                        };
                    }
                }
                finally
                {
                    CloseServiceHandle(svcHandle);
                }
            }
        }

        return (true, stateText, startModeText, isRunning, isDisabled, pid);
    }

    private static HpComponentStatus GetUwpBackgroundWatchdogStatus()
    {
        int allowedApps = 0;
        int totalApps = HpUwpPackageFamilies.Length;

        try
        {
            using var bgRoot = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", writable: false);
            foreach (string pfn in HpUwpPackageFamilies)
            {
                bool isDisabled = false;
                if (bgRoot != null)
                {
                    using var appKey = bgRoot.OpenSubKey(pfn, writable: false);
                    if (appKey != null)
                    {
                        int disabled = Convert.ToInt32(appKey.GetValue("Disabled") ?? 0);
                        int disabledByUser = Convert.ToInt32(appKey.GetValue("DisabledByUser") ?? 0);
                        isDisabled = disabled == 1 || disabledByUser == 1;
                    }
                }
                if (!isDisabled)
                    allowedApps++;
            }
        }
        catch { }

        int enabledStartupTasks = 0;
        try
        {
            using var appDataRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData", writable: false);
            if (appDataRoot != null)
            {
                foreach (var (pfn, taskId) in HpUwpStartupTasks)
                {
                    using var taskKey = appDataRoot.OpenSubKey($@"{pfn}\{taskId}", writable: false);
                    if (taskKey != null)
                    {
                        int state = Convert.ToInt32(taskKey.GetValue("State") ?? 0);
                        if (state == 2)
                            enabledStartupTasks++;
                    }
                }
            }
        }
        catch { }

        bool isWatchdogActive = allowedApps > 0 || enabledStartupTasks > 0;
        return new HpComponentStatus(
            Id: "watchdog_uwp_bg",
            DisplayName: "HP UWP Background & SystemEvent Respawners",
            Description: "Controls Windows BackgroundAccessApplications & StartupTasks for myHP (35+ timer/event triggers like VerifyAutoLaunchTask), HPThermalControl, OMEN, and HP Support Assistant so Windows stops respawning them on AC/unlock/timers.",
            Kind: HpComponentKind.UwpBackgroundWatchdog,
            Category: HpSafetyCategory.AutoLauncherWatchdog,
            IsRunning: isWatchdogActive,
            StateText: isWatchdogActive ? $"Active ({allowedApps}/{totalApps})" : "Blocked",
            StartModeText: isWatchdogActive ? "UWP Triggers: On" : "UWP Triggers: Off",
            MemoryMb: 0,
            CpuSeconds: 0,
            ProcessCount: 0,
            CanActivate: allowedApps < totalApps,
            CanDeactivate: isWatchdogActive);
    }

    private static HpComponentStatus GetScheduledTaskWatchdogStatus()
    {
        var (total, enabled, _) = GetHpScheduledTasksSnapshot();
        bool isActive = enabled > 0;

        return new HpComponentStatus(
            Id: "watchdog_sched_tasks",
            DisplayName: "HP & OMEN Scheduled Task Respawners",
            Description: "Controls all 8 HP Scheduled Tasks (OmenInstallMonitor*, OmenOverlay*, Printer Health Monitor*, WarrantyChecker*) that use Windows event, timer, and logon triggers to silently re-open closed HP background processes.",
            Kind: HpComponentKind.ScheduledTaskWatchdog,
            Category: HpSafetyCategory.AutoLauncherWatchdog,
            IsRunning: isActive,
            StateText: isActive ? $"Enabled ({enabled}/{total})" : "All Disabled",
            StartModeText: $"Tasks: {enabled}/{total} On",
            MemoryMb: 0,
            CpuSeconds: 0,
            ProcessCount: 0,
            CanActivate: enabled < total,
            CanDeactivate: enabled > 0);
    }

    private static HpComponentStatus GetRegistryRunWatchdogStatus()
    {
        int enabledCount = 0;
        int total = HpRegistryRunNames.Length;

        try
        {
            using var approvedKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: false);
            foreach (string name in HpRegistryRunNames)
            {
                bool isDisabled = false;
                if (approvedKey?.GetValue(name) is byte[] bytes && bytes.Length > 0)
                {
                    isDisabled = (bytes[0] & 1) == 1;
                }
                if (!isDisabled)
                    enabledCount++;
            }
        }
        catch { }

        bool isActive = enabledCount > 0;
        return new HpComponentStatus(
            Id: "watchdog_reg_run",
            DisplayName: "HP Registry Logon Launchers (HpccLauncher & HpseuHostLauncher)",
            Description: "Controls HKCU Run launchers: HpseuHostLauncher.exe (spawns OmenCommandCenterBackground & HPSystemEventUtility) and HpccLauncher.exe (spawns Legacy HP Thermal Control).",
            Kind: HpComponentKind.RegistryRunWatchdog,
            Category: HpSafetyCategory.AutoLauncherWatchdog,
            IsRunning: isActive,
            StateText: isActive ? $"Enabled ({enabledCount}/{total})" : "Disabled",
            StartModeText: isActive ? "Startup: Enabled" : "Startup: Disabled",
            MemoryMb: 0,
            CpuSeconds: 0,
            ProcessCount: 0,
            CanActivate: enabledCount < total,
            CanDeactivate: enabledCount > 0);
    }

    private (bool Success, string Message) ActivateComponentCore(HpComponentStatus item)
    {
        try
        {
            switch (item.Kind)
            {
                case HpComponentKind.UwpBackgroundWatchdog:
                {
                    int count = SetHpUwpBackgroundAccess(enable: true);
                    return (true, $"Re-enabled UWP background & startup triggers for {count} HP app package(s).");
                }

                case HpComponentKind.ScheduledTaskWatchdog:
                {
                    int toggled = SetAllHpScheduledTasksEnabled(pattern: null, enable: true, startNow: false);
                    return (true, $"Enabled {toggled} HP/OMEN scheduled task trigger(s).");
                }

                case HpComponentKind.RegistryRunWatchdog:
                {
                    int count = SetHpRegistryRunLaunchersEnabled(enable: true);
                    return (true, $"Enabled {count} HP Registry logon launcher(s) (HpccLauncher & HpseuHostLauncher).");
                }

                case HpComponentKind.WindowsService:
                {
                    bool ok = SetServiceStartModeAndStateNative(item.Id, startType: SERVICE_AUTO_START, startService: true);
                    return ok
                        ? (true, $"Enabled and started service '{item.DisplayName}'.")
                        : (false, $"Failed to start service '{item.Id}' (ensure app is running as Administrator).");
                }

                case HpComponentKind.ProcessGroup:
                default:
                {
                    var group = KnownProcessGroups.FirstOrDefault(g => g.GroupId == item.Id);
                    if (group?.TaskNamePattern != null)
                    {
                        int toggled = SetAllHpScheduledTasksEnabled(group.TaskNamePattern, enable: true, startNow: true);
                        return (true, $"Enabled and started {toggled} scheduled task(s) for '{item.DisplayName}'.");
                    }

                    if (group?.LaunchExecutablePath != null && System.IO.File.Exists(group.LaunchExecutablePath))
                    {
                        Process.Start(new ProcessStartInfo(group.LaunchExecutablePath) { UseShellExecute = true });
                        return (true, $"Started '{item.DisplayName}'.");
                    }
                    return (false, $"'{item.DisplayName}' is launched on-demand when you open the corresponding HP app.");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to activate '{item.DisplayName}': {ex.Message}");
        }
    }

    private (bool Success, string Message) DeactivateComponentCore(HpComponentStatus item)
    {
        try
        {
            switch (item.Kind)
            {
                case HpComponentKind.UwpBackgroundWatchdog:
                {
                    int count = SetHpUwpBackgroundAccess(enable: false);
                    return (true, $"Blocked UWP background systemEvent/timer respawners for {count} HP app package(s).");
                }

                case HpComponentKind.ScheduledTaskWatchdog:
                {
                    int toggled = SetAllHpScheduledTasksEnabled(pattern: null, enable: false, startNow: false);
                    return (true, $"Stopped and disabled all {toggled} HP & OMEN scheduled task respawners.");
                }

                case HpComponentKind.RegistryRunWatchdog:
                {
                    int count = SetHpRegistryRunLaunchersEnabled(enable: false);
                    return (true, $"Disabled {count} HP Registry logon launcher(s) (HpccLauncher & HpseuHostLauncher).");
                }

                case HpComponentKind.WindowsService:
                {
                    SetServiceStartModeAndStateNative(item.Id, startType: SERVICE_DISABLED, startService: false);
                    ClearServiceFailureAutoRestart(item.Id);
                    return (true, $"Stopped, disabled, and cleared auto-restart recovery for '{item.DisplayName}'.");
                }

                case HpComponentKind.ProcessGroup:
                default:
                {
                    var group = KnownProcessGroups.FirstOrDefault(g => g.GroupId == item.Id);
                    if (group == null)
                        return (false, "Unknown process group.");

                    int tasksDisabled = 0;
                    if (group.TaskNamePattern != null)
                    {
                        tasksDisabled = SetAllHpScheduledTasksEnabled(group.TaskNamePattern, enable: false, startNow: false);
                    }

                    int killed = 0;
                    foreach (string procName in group.ProcessNames)
                    {
                        foreach (var p in Process.GetProcessesByName(procName))
                        {
                            try
                            {
                                p.Kill(entireProcessTree: true);
                                killed++;
                            }
                            catch { }
                            finally
                            {
                                p.Dispose();
                            }
                        }
                    }

                    string taskMsg = tasksDisabled > 0 ? $" and disabled {tasksDisabled} startup task(s)" : "";
                    return (true, $"Terminated {killed} process(es){taskMsg} for '{item.DisplayName}'.");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop/disable '{item.DisplayName}': {ex.Message}");
        }
    }

    private static bool SetServiceStartModeAndStateNative(string serviceName, uint startType, bool startService)
    {
        IntPtr scmHandle = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scmHandle == IntPtr.Zero)
            return false;

        try
        {
            uint desiredAccess = SERVICE_CHANGE_CONFIG | SERVICE_QUERY_STATUS | (startService ? SERVICE_START : SERVICE_STOP);
            IntPtr svcHandle = OpenServiceW(scmHandle, serviceName, desiredAccess);
            if (svcHandle == IntPtr.Zero)
                return false;

            try
            {
                ChangeServiceConfigW(
                    svcHandle,
                    SERVICE_NO_CHANGE,
                    startType,
                    SERVICE_NO_CHANGE,
                    null, null, IntPtr.Zero, null, null, null, null);

                if (startService)
                {
                    StartServiceW(svcHandle, 0, IntPtr.Zero);
                    WaitForServiceState(svcHandle, SERVICE_RUNNING, timeoutMs: 2500);
                }
                else
                {
                    ControlService(svcHandle, SERVICE_CONTROL_STOP, out _);
                    WaitForServiceState(svcHandle, SERVICE_STOPPED, timeoutMs: 2500);
                }

                return true;
            }
            finally
            {
                CloseServiceHandle(svcHandle);
            }
        }
        finally
        {
            CloseServiceHandle(scmHandle);
        }
    }

    private static void WaitForServiceState(IntPtr svcHandle, uint targetState, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (QueryServiceStatusEx(
                svcHandle,
                SC_STATUS_PROCESS_INFO,
                out SERVICE_STATUS_PROCESS status,
                Marshal.SizeOf<SERVICE_STATUS_PROCESS>(),
                out _) && status.dwCurrentState == targetState)
            {
                return;
            }
            Thread.Sleep(60);
        }
    }

    private (int Count, double FreedMb, string Summary) DisableAllTelemetryAndBloatCore()
    {
        var items = GetAllStatusesCore()
            .Where(i => i.Category is HpSafetyCategory.AutoLauncherWatchdog
                or HpSafetyCategory.TelemetryAndBloat
                or HpSafetyCategory.RedundantBackground)
            .OrderBy(i => i.Category == HpSafetyCategory.AutoLauncherWatchdog ? 0 : 1)
            .ToList();

        int stoppedCount = 0;
        double freedMb = 0;

        foreach (var item in items)
        {
            if (!item.CanDeactivate) continue;
            var (ok, _) = DeactivateComponentCore(item);
            if (ok)
            {
                stoppedCount++;
                freedMb += item.MemoryMb;
            }
        }

        return (stoppedCount, freedMb, $"Blocked all HP respawn watchdogs & stopped {stoppedCount} bloat/telemetry components (freed ~{freedMb:F1} MB RAM).");
    }

    private static int SetHpUwpBackgroundAccess(bool enable)
    {
        int count = 0;
        int regVal = enable ? 0 : 1;

        try
        {
            using var bgRoot = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", writable: true);
            if (bgRoot != null)
            {
                foreach (string pfn in HpUwpPackageFamilies)
                {
                    using var appKey = bgRoot.CreateSubKey(pfn, writable: true);
                    if (appKey != null)
                    {
                        appKey.SetValue("Disabled", regVal, RegistryValueKind.DWord);
                        appKey.SetValue("DisabledByUser", regVal, RegistryValueKind.DWord);
                        appKey.SetValue("SleepDisabled", regVal, RegistryValueKind.DWord);
                        count++;
                    }
                }
            }
        }
        catch { }

        try
        {
            using var appDataRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData", writable: true);
            if (appDataRoot != null)
            {
                int targetState = enable ? 2 : 1;
                foreach (var (pfn, taskId) in HpUwpStartupTasks)
                {
                    using var taskKey = appDataRoot.OpenSubKey($@"{pfn}\{taskId}", writable: true);
                    taskKey?.SetValue("State", targetState, RegistryValueKind.DWord);
                }
            }
        }
        catch { }

        return count;
    }

    private static int SetHpRegistryRunLaunchersEnabled(bool enable)
    {
        int count = 0;
        try
        {
            using var approvedKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
            if (approvedKey != null)
            {
                byte[] data = new byte[12];
                if (enable)
                {
                    data[0] = 0x02;
                }
                else
                {
                    data[0] = 0x03;
                    byte[] ft = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
                    Buffer.BlockCopy(ft, 0, data, 4, 8);
                }

                foreach (string name in HpRegistryRunNames)
                {
                    approvedKey.SetValue(name, data, RegistryValueKind.Binary);
                    count++;
                }
            }
        }
        catch { }

        if (!enable)
        {
            foreach (string procName in new[] { "HpccLauncher", "HpseuHostLauncher" })
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    finally { p.Dispose(); }
                }
            }
        }

        return count;
    }

    private static (int TotalHpTasks, int EnabledHpTasks, bool OmenEnabled) GetHpScheduledTasksSnapshot()
    {
        int total = 0;
        int enabled = 0;
        bool omenEnabled = false;

        try
        {
            Type? schedType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedType == null) return (8, 0, false);

            dynamic? service = Activator.CreateInstance(schedType);
            if (service == null) return (8, 0, false);

            service.Connect();

            foreach (string folderPath in HpScheduledTaskFolders)
            {
                try
                {
                    dynamic folder = service.GetFolder(folderPath);
                    dynamic tasks = folder.GetTasks(1);
                    int count = (int)tasks.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic task = tasks[i];
                        string name = (string)task.Name;

                        bool isHpTask = folderPath != @"\" || name.Contains("Omen", StringComparison.OrdinalIgnoreCase);
                        if (!isHpTask) continue;

                        total++;
                        bool isTaskEnabled = (bool)task.Enabled;
                        if (isTaskEnabled)
                        {
                            enabled++;
                            if (name.Contains("Omen", StringComparison.OrdinalIgnoreCase))
                                omenEnabled = true;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return (total, enabled, omenEnabled);
    }

    private static int SetAllHpScheduledTasksEnabled(string? pattern, bool enable, bool startNow)
    {
        int toggled = 0;
        try
        {
            Type? schedType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedType == null) return 0;

            dynamic? service = Activator.CreateInstance(schedType);
            if (service == null) return 0;

            service.Connect();

            foreach (string folderPath in HpScheduledTaskFolders)
            {
                try
                {
                    dynamic folder = service.GetFolder(folderPath);
                    dynamic tasks = folder.GetTasks(1);
                    int count = (int)tasks.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic task = tasks[i];
                        string name = (string)task.Name;

                        bool isHpTask = folderPath != @"\" || name.Contains("Omen", StringComparison.OrdinalIgnoreCase);
                        if (!isHpTask) continue;

                        if (pattern != null && !name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!enable)
                        {
                            try { task.Stop(0); } catch { }
                        }

                        task.Enabled = enable;

                        if (enable && startNow && name.Contains("OmenOverlay-", StringComparison.OrdinalIgnoreCase))
                        {
                            try { task.Run(null); } catch { }
                        }

                        toggled++;
                    }
                }
                catch { }
            }
        }
        catch { }

        return toggled;
    }

    private static void ClearServiceFailureAutoRestart(string serviceName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("sc.exe", $"failure \"{serviceName}\" reset= 0 actions= \"\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(1500);
        }
        catch { }
    }

    // Native Win32 SCM P/Invoke declarations for sub-millisecond, non-timeout service queries & control
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_DISABLED = 0x00000004;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const int SC_STATUS_PROCESS_INFO = 0;

    private const uint SERVICE_STOPPED = 0x00000001;
    private const uint SERVICE_START_PENDING = 0x00000002;
    private const uint SERVICE_STOP_PENDING = 0x00000003;
    private const uint SERVICE_RUNNING = 0x00000004;
    private const uint SERVICE_PAUSED = 0x00000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr hService,
        int infoLevel,
        out SERVICE_STATUS_PROCESS lpBuffer,
        int cbBufSize,
        out int pcbBytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfigW(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);
}
