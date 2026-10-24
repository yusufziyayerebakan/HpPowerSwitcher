using System.Diagnostics;
using System.Management;

namespace HpPowerSwitcher.Services;

public enum HpComponentKind
{
    WindowsService,
    ProcessGroup
}

public enum HpSafetyCategory
{
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
        HpSafetyCategory.TelemetryAndBloat => "Telemetry / Safe to Disable",
        HpSafetyCategory.RedundantBackground => "Redundant Bloat",
        HpSafetyCategory.OptionalService => "Optional",
        HpSafetyCategory.CoreHardware => "Core Hardware",
        _ => "Other"
    };

    public string CategoryColorHex => Category switch
    {
        HpSafetyCategory.TelemetryAndBloat => "#F87171",
        HpSafetyCategory.RedundantBackground => "#FB923C",
        HpSafetyCategory.OptionalService => "#FBBF24",
        HpSafetyCategory.CoreHardware => "#38BDF8",
        _ => "#94A3B8"
    };

    public string MemoryDisplay => MemoryMb > 0.05 ? $"{MemoryMb:F1} MB" : "0 MB";
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

    private static readonly ServiceDefinition[] KnownServices =
    [
        new("HpTouchpointAnalyticsService", "HP Insights Analytics (Telemetry)", "Collects and uploads background device telemetry to HP servers.", HpSafetyCategory.TelemetryAndBloat),
        new("HPPrintScanDoctorService", "HP Print Scan Doctor Service", "Background troubleshooting service for HP printers/scanners.", HpSafetyCategory.TelemetryAndBloat),
        new("HPAppPrintScanDoctorService", "HP App Print Scan Doctor Service", "Secondary print/scan diagnostics service from myHP.", HpSafetyCategory.TelemetryAndBloat),
        new("HPDiagsCap", "HP Diagnostics HSA Service", "Hardware diagnostics & telemetry capability service.", HpSafetyCategory.OptionalService),
        new("HPNetworkCap", "HP Network HSA Service", "Network Booster background traffic monitor.", HpSafetyCategory.OptionalService),
        new("HP Comm Recover", "HP Comm Recovery Service", "Network/Bluetooth connection reset helper service.", HpSafetyCategory.OptionalService),
        new("HPSysInfoCap", "HP System Info HSA Service", "Used by myHP / HP Command Center for WMI/RPC queries.", HpSafetyCategory.CoreHardware),
        new("HPAppHelperCap", "HP App Helper HSA Service", "Used by HP hotkeys and system event utilities.", HpSafetyCategory.CoreHardware),
        new("HPDCService", "HP Display Control Service", "Portrait Displays color profile service.", HpSafetyCategory.CoreHardware)
    ];

    private static readonly ProcessGroupDefinition[] KnownProcessGroups =
    [
        new(
            GroupId: "proc_omen",
            FriendlyName: "OMEN Gaming Hub Background & Tasks",
            Purpose: "Runs 50+ named pipes & game monitors continuously (~210 MB RAM, 770s+ CPU) on non-OMEN Spectre laptops via OmenOverlay/OmenInstallMonitor scheduled tasks.",
            Category: HpSafetyCategory.RedundantBackground,
            ProcessNames: ["OmenCommandCenterBackground", "OmenInstallMonitor"],
            TaskNamePattern: "Omen",
            LaunchExecutablePath: null),
        new(
            GroupId: "proc_hpcc",
            FriendlyName: "Legacy HP Thermal Control Background",
            Purpose: "Older HP Command Center background daemons (HPCC.Bg.BackgroundApp / Sys) polling sensors.",
            Category: HpSafetyCategory.RedundantBackground,
            ProcessNames: ["HPCC.Bg.BackgroundApp", "HPCC.Bg.BackgroundSys"],
            TaskNamePattern: null,
            LaunchExecutablePath: null),
        new(
            GroupId: "proc_myhp",
            FriendlyName: "myHP App & ContextAware Daemons",
            Purpose: "WebView2 myHP UI (HP.HPX), ContextAware, and SystemControl background processes.",
            Category: HpSafetyCategory.OptionalService,
            ProcessNames: ["HP.HPX", "HP.ContextAware", "HP.SystemControl.Background", "HP.SystemControl.SysBackground"],
            TaskNamePattern: null,
            LaunchExecutablePath: null),
        new(
            GroupId: "proc_media",
            FriendlyName: "HP Media Network & Enhanced Lighting",
            Purpose: "Background media sharing and webcam ring-light overlay processes.",
            Category: HpSafetyCategory.TelemetryAndBloat,
            ProcessNames: ["HPMediaNetwork", "HPEnhancedLighting.Bg"],
            TaskNamePattern: null,
            LaunchExecutablePath: @"C:\Program Files\HP\HP Media Network\HPMediaNetwork.exe")
    ];

    public IReadOnlyList<HpComponentStatus> GetAllStatuses()
    {
        var results = new List<HpComponentStatus>();

        // Map all running processes by PID and Name for fast RAM/CPU lookup
        var allProcs = Process.GetProcesses();
        var procById = new Dictionary<int, Process>();
        var procsByName = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in allProcs)
        {
            try
            {
                procById[p.Id] = p;
                if (!procsByName.TryGetValue(p.ProcessName, out var list))
                {
                    list = [];
                    procsByName[p.ProcessName] = list;
                }
                list.Add(p);
            }
            catch { }
        }

        bool omenTasksEnabled = AreScheduledTasksEnabled("Omen");

        // 1. Process Groups first (since OMEN & myHP are huge memory/CPU hogs)
        foreach (var group in KnownProcessGroups)
        {
            double memMb = 0;
            double cpuSec = 0;
            int count = 0;

            foreach (string procName in group.ProcessNames)
            {
                if (procsByName.TryGetValue(procName, out var matches))
                {
                    foreach (var p in matches)
                    {
                        count++;
                        try { memMb += p.WorkingSet64 / (1024.0 * 1024.0); } catch { }
                        try { cpuSec += p.TotalProcessorTime.TotalSeconds; } catch { }
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

        // 2. Windows Services via WMI Win32_Service
        var wmiServices = new Dictionary<string, (string State, string StartMode, int Pid)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, State, StartMode, ProcessId FROM Win32_Service");
            foreach (ManagementObject obj in searcher.Get())
            {
                string? name = obj["Name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                string state = obj["State"]?.ToString() ?? "Unknown";
                string startMode = obj["StartMode"]?.ToString() ?? "Unknown";
                int pid = Convert.ToInt32(obj["ProcessId"] ?? 0);
                wmiServices[name] = (state, startMode, pid);
            }
        }
        catch { }

        foreach (var svc in KnownServices)
        {
            if (!wmiServices.TryGetValue(svc.ServiceName, out var info))
                continue;

            bool isRunning = string.Equals(info.State, "Running", StringComparison.OrdinalIgnoreCase);
            double memMb = 0;
            double cpuSec = 0;

            if (isRunning && info.Pid > 0 && procById.TryGetValue(info.Pid, out var p))
            {
                try { memMb = p.WorkingSet64 / (1024.0 * 1024.0); } catch { }
                try { cpuSec = p.TotalProcessorTime.TotalSeconds; } catch { }
            }

            results.Add(new HpComponentStatus(
                Id: svc.ServiceName,
                DisplayName: svc.FriendlyName,
                Description: $"{svc.Purpose} [Service: {svc.ServiceName}]",
                Kind: HpComponentKind.WindowsService,
                Category: svc.Category,
                IsRunning: isRunning,
                StateText: info.State,
                StartModeText: $"Startup: {info.StartMode}",
                MemoryMb: memMb,
                CpuSeconds: cpuSec,
                ProcessCount: isRunning ? 1 : 0,
                CanActivate: !isRunning || string.Equals(info.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase),
                CanDeactivate: isRunning || !string.Equals(info.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var p in allProcs)
        {
            try { p.Dispose(); } catch { }
        }

        return results;
    }

    public (bool Success, string Message) ActivateComponent(HpComponentStatus item)
    {
        try
        {
            if (item.Kind == HpComponentKind.WindowsService)
            {
                using var svcObj = new ManagementObject($"Win32_Service.Name='{item.Id}'");
                svcObj.Get();
                svcObj.InvokeMethod("ChangeStartMode", ["Automatic"]);
                uint rc = Convert.ToUInt32(svcObj.InvokeMethod("StartService", null));
                if (rc is 0 or 10) // 0 = OK, 10 = Service Already Running
                    return (true, $"Enabled and started service '{item.DisplayName}'.");
                return (false, $"Service '{item.Id}' returned code {rc} when starting.");
            }
            else
            {
                var group = KnownProcessGroups.FirstOrDefault(g => g.GroupId == item.Id);
                if (group?.TaskNamePattern != null)
                {
                    int toggled = SetScheduledTasksEnabled(group.TaskNamePattern, enable: true, startNow: true);
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
        catch (Exception ex)
        {
            return (false, $"Failed to activate '{item.DisplayName}': {ex.Message}");
        }
    }

    public (bool Success, string Message) DeactivateComponent(HpComponentStatus item)
    {
        try
        {
            if (item.Kind == HpComponentKind.WindowsService)
            {
                using var svcObj = new ManagementObject($"Win32_Service.Name='{item.Id}'");
                svcObj.Get();
                svcObj.InvokeMethod("StopService", null);
                svcObj.InvokeMethod("ChangeStartMode", ["Disabled"]);
                return (true, $"Stopped and disabled service '{item.DisplayName}'.");
            }
            else
            {
                var group = KnownProcessGroups.FirstOrDefault(g => g.GroupId == item.Id);
                if (group == null)
                    return (false, "Unknown process group.");

                int tasksDisabled = 0;
                if (group.TaskNamePattern != null)
                {
                    tasksDisabled = SetScheduledTasksEnabled(group.TaskNamePattern, enable: false, startNow: false);
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
        catch (Exception ex)
        {
            return (false, $"Failed to stop/disable '{item.DisplayName}': {ex.Message}");
        }
    }

    public (int Count, double FreedMb, string Summary) DisableAllTelemetryAndBloat()
    {
        var items = GetAllStatuses()
            .Where(i => i.Category is HpSafetyCategory.TelemetryAndBloat or HpSafetyCategory.RedundantBackground)
            .ToList();

        int stoppedCount = 0;
        double freedMb = 0;

        foreach (var item in items)
        {
            if (!item.CanDeactivate) continue;
            var (ok, _) = DeactivateComponent(item);
            if (ok)
            {
                stoppedCount++;
                freedMb += item.MemoryMb;
            }
        }

        // Also disable HP Print Scan Doctor & HP Support Assistant telemetry scheduled tasks if enabled
        SetScheduledTasksEnabled("Printer Health Monitor", enable: false, startNow: false);

        return (stoppedCount, freedMb, $"Stopped & disabled {stoppedCount} telemetry/bloat components (freed ~{freedMb:F1} MB RAM).");
    }

    private static bool AreScheduledTasksEnabled(string namePattern)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\TaskScheduler");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT TaskName, State FROM MSFT_ScheduledTask"));
            foreach (ManagementObject task in searcher.Get())
            {
                string? name = task["TaskName"]?.ToString();
                if (name != null && name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                {
                    // State: 1 = Disabled, 3 = Ready, 4 = Running
                    uint state = Convert.ToUInt32(task["State"] ?? 0);
                    if (state != 1)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static int SetScheduledTasksEnabled(string namePattern, bool enable, bool startNow)
    {
        int count = 0;
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\TaskScheduler");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_ScheduledTask"));
            foreach (ManagementObject task in searcher.Get())
            {
                using (task)
                {
                    string? name = task["TaskName"]?.ToString();
                    if (name == null || !name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (enable)
                    {
                        task.InvokeMethod("Enable", null);
                        if (startNow && name.Contains("OmenOverlay-", StringComparison.OrdinalIgnoreCase))
                        {
                            try { task.InvokeMethod("Start", null); } catch { }
                        }
                    }
                    else
                    {
                        try { task.InvokeMethod("Stop", null); } catch { }
                        task.InvokeMethod("Disable", null);
                    }
                    count++;
                }
            }
        }
        catch { }
        return count;
    }
}
