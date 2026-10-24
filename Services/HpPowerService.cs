using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

namespace HpPowerSwitcher.Services;

/// <summary>
/// Matches HP.SystemControl.AppData.SystemControlMode from HP.SystemControl.AppData.dll.
/// Sent as byte[0] of hpqBData to root\wmi:hpqBIntM (Command=2, CommandType=76 / 0x4C).
/// </summary>
public enum HpThermalMode
{
    Performance = 0,
    Balanced = 1,
    Cool = 2,
    Quiet = 3,
    PowerSaver = 4,
    SmartSense = 5,
    Silent = 6
}

public enum WindowsOverlayPowerMode
{
    BestPowerEfficiency,
    Balanced,
    BestPerformance
}

public sealed record ThermalModeOption(
    HpThermalMode Mode,
    string Title,
    string Subtitle,
    string IconGlyph,
    string AccentHex,
    bool IsSupported);

public sealed record SystemPowerSnapshot(
    string ModelName,
    string BiosVersion,
    bool IsAdmin,
    bool IsOnAcPower,
    int BatteryPercent,
    string BatteryStatusText,
    HpThermalMode? CurrentBiosMode,
    IReadOnlySet<HpThermalMode> SupportedBiosModes,
    WindowsOverlayPowerMode AcOverlayMode,
    WindowsOverlayPowerMode DcOverlayMode,
    string StatusMessage);

public sealed class HpPowerService
{
    // Windows 11 Power Mode Overlay GUIDs (from HP.SystemControl.Utility.BiDirectionalAPI)
    public static readonly Guid GuidBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid GuidBalanced = Guid.Empty; // 00000000-0000-0000-0000-000000000000
    public static readonly Guid GuidBestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    // HP BIOS WMI Constants (from HP.SystemControl.BiosWmi.WmiConstants)
    private static readonly byte[] HpSecuSign = [0x53, 0x45, 0x43, 0x55]; // "SECU"
    private const uint CmdRead = 1;
    private const uint CmdWrite = 2;
    private const uint CmdTypeSupportedModes = 13; // 0x0D
    private const uint CmdTypeSystemControl = 76;  // 0x4C

    // Capability bit mapping in Data[6] from 0x0D (from SystemControlCoreBase.modeOrderMap)
    private static readonly (int BitIndex, HpThermalMode Mode)[] ModeCapabilityBits =
    [
        (1, HpThermalMode.SmartSense),
        (2, HpThermalMode.Balanced),
        (3, HpThermalMode.Performance),
        (4, HpThermalMode.Cool),
        (5, HpThermalMode.Quiet),
        (6, HpThermalMode.PowerSaver),
        (7, HpThermalMode.Silent)
    ];

    private string? _cachedModel;
    private string? _cachedBios;
    private HashSet<HpThermalMode>? _cachedSupportedModes;

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetUserConfiguredACPowerMode(out Guid powerPlanGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid powerPlanGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetUserConfiguredACPowerMode(ref Guid powerPlanGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid powerPlanGuid);

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public SystemPowerSnapshot GetSnapshot()
    {
        EnsureHardwareMetadata();

        bool isAdmin = IsRunningAsAdministrator();
        var powerStatus = SystemInformation.PowerStatus;
        bool isAc = powerStatus.PowerLineStatus == PowerLineStatus.Online;
        int batteryPct = (int)Math.Round(powerStatus.BatteryLifePercent * 100.0);
        if (batteryPct < 0 || batteryPct > 100) batteryPct = 100;

        string batteryText = isAc
            ? (powerStatus.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Charging)
                ? $"{batteryPct}% (Charging on AC)"
                : $"{batteryPct}% (Plugged In)")
            : $"{batteryPct}% (On Battery)";

        var supported = GetSupportedBiosModes(isAdmin);
        var currentBios = GetCurrentBiosMode(isAdmin, out string biosStatus);

        var acMode = GetAcOverlayPowerMode();
        var dcMode = GetDcOverlayPowerMode();

        return new SystemPowerSnapshot(
            ModelName: _cachedModel ?? "HP Laptop",
            BiosVersion: _cachedBios ?? "Unknown",
            IsAdmin: isAdmin,
            IsOnAcPower: isAc,
            BatteryPercent: batteryPct,
            BatteryStatusText: batteryText,
            CurrentBiosMode: currentBios,
            SupportedBiosModes: supported,
            AcOverlayMode: acMode,
            DcOverlayMode: dcMode,
            StatusMessage: biosStatus);
    }

    public static IReadOnlyList<ThermalModeOption> GetAllModeOptions(IReadOnlySet<HpThermalMode> supportedModes)
    {
        return
        [
            new(HpThermalMode.SmartSense, "Smart Sense", "AI auto-adapts power & fan speed to workload", "⚡", "#38BDF8", supportedModes.Contains(HpThermalMode.SmartSense)),
            new(HpThermalMode.Balanced, "Balanced", "Recommended balance of performance and noise", "⚖", "#34D399", supportedModes.Contains(HpThermalMode.Balanced)),
            new(HpThermalMode.Performance, "Performance", "Maximum CPU/GPU wattage & active cooling", "🚀", "#F97316", supportedModes.Contains(HpThermalMode.Performance)),
            new(HpThermalMode.Cool, "Cool", "Keeps surface & lap temperatures lower", "❄", "#60A5FA", supportedModes.Contains(HpThermalMode.Cool)),
            new(HpThermalMode.Quiet, "Quiet", "Minimizes fan spin and acoustic noise", "🌙", "#A78BFA", supportedModes.Contains(HpThermalMode.Quiet)),
            new(HpThermalMode.PowerSaver, "Power Saver", "Reduces CPU package power for max battery", "🍃", "#4ADE80", supportedModes.Contains(HpThermalMode.PowerSaver))
        ];
    }

    public (bool Success, string Message, double ElapsedMs) SetHpThermalMode(
        HpThermalMode mode,
        bool syncWindowsOverlay = true)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            bool isAc = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            if (mode == HpThermalMode.PowerSaver && isAc)
            {
                // Note: HP BIOS allows setting PowerSaver or we can still send it and warn if BIOS ignores on AC
            }

            if (!IsRunningAsAdministrator())
            {
                sw.Stop();
                return (false, "Administrator privileges are required to write HP BIOS WMI thermal commands.", sw.Elapsed.TotalMilliseconds);
            }

            bool biosOk = ExecuteBiosWriteCommand(CmdTypeSystemControl, [(byte)mode, 0, 0, 0], out uint returnCode, out string error);
            if (!biosOk)
            {
                sw.Stop();
                return (false, $"BIOS WMI command failed (returnCode={returnCode}): {error}", sw.Elapsed.TotalMilliseconds);
            }

            if (syncWindowsOverlay)
            {
                var overlayTarget = MapThermalToWindowsOverlay(mode);
                SetOverlayPowerMode(overlayTarget, applyToAc: isAc, applyToDc: !isAc);
            }

            sw.Stop();
            return (true, $"Switched HP BIOS thermal profile to {mode} in {sw.Elapsed.TotalMilliseconds:F1} ms.", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, $"Error setting thermal mode: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    public (bool Success, string Message, double ElapsedMs) SetOverlayPowerMode(
        WindowsOverlayPowerMode mode,
        bool applyToAc,
        bool applyToDc)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            Guid guid = ModeToGuid(mode);
            uint rcAc = 0;
            uint rcDc = 0;

            if (applyToAc)
            {
                Guid g = guid;
                rcAc = PowerSetUserConfiguredACPowerMode(ref g);
            }

            if (applyToDc)
            {
                Guid g = guid;
                rcDc = PowerSetUserConfiguredDCPowerMode(ref g);
            }

            sw.Stop();
            if (rcAc == 0 && rcDc == 0)
            {
                string targetStr = (applyToAc && applyToDc) ? "AC & Battery" : (applyToAc ? "AC (Plugged In)" : "Battery (DC)");
                return (true, $"Windows Power Mode ({targetStr}) set to {FormatOverlayName(mode)} in {sw.Elapsed.TotalMilliseconds:F1} ms.", sw.Elapsed.TotalMilliseconds);
            }

            return (false, $"powrprof returned AC={rcAc}, DC={rcDc}", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, $"Failed to set Windows power mode: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    public static WindowsOverlayPowerMode MapThermalToWindowsOverlay(HpThermalMode mode) => mode switch
    {
        HpThermalMode.Performance => WindowsOverlayPowerMode.BestPerformance,
        HpThermalMode.PowerSaver => WindowsOverlayPowerMode.BestPowerEfficiency,
        _ => WindowsOverlayPowerMode.Balanced
    };

    public static string FormatOverlayName(WindowsOverlayPowerMode mode) => mode switch
    {
        WindowsOverlayPowerMode.BestPowerEfficiency => "Best Power Efficiency",
        WindowsOverlayPowerMode.Balanced => "Balanced",
        WindowsOverlayPowerMode.BestPerformance => "Best Performance",
        _ => mode.ToString()
    };

    private WindowsOverlayPowerMode GetAcOverlayPowerMode()
    {
        try
        {
            if (PowerGetUserConfiguredACPowerMode(out Guid g) == 0)
                return GuidToMode(g);
        }
        catch { }
        return WindowsOverlayPowerMode.Balanced;
    }

    private WindowsOverlayPowerMode GetDcOverlayPowerMode()
    {
        try
        {
            if (PowerGetUserConfiguredDCPowerMode(out Guid g) == 0)
                return GuidToMode(g);
        }
        catch { }
        return WindowsOverlayPowerMode.Balanced;
    }

    private static Guid ModeToGuid(WindowsOverlayPowerMode mode) => mode switch
    {
        WindowsOverlayPowerMode.BestPerformance => GuidBestPerformance,
        WindowsOverlayPowerMode.BestPowerEfficiency => GuidBestPowerEfficiency,
        _ => GuidBalanced
    };

    private static WindowsOverlayPowerMode GuidToMode(Guid g)
    {
        if (g == GuidBestPerformance) return WindowsOverlayPowerMode.BestPerformance;
        if (g == GuidBestPowerEfficiency) return WindowsOverlayPowerMode.BestPowerEfficiency;
        return WindowsOverlayPowerMode.Balanced;
    }

    private void EnsureHardwareMetadata()
    {
        if (_cachedModel != null && _cachedBios != null)
            return;

        try
        {
            using var csSearcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in csSearcher.Get())
            {
                _cachedModel = obj["Model"]?.ToString()?.Trim();
                break;
            }
        }
        catch
        {
            _cachedModel = "HP Spectre x360";
        }

        try
        {
            using var biosSearcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, Manufacturer FROM Win32_BIOS");
            foreach (ManagementObject obj in biosSearcher.Get())
            {
                _cachedBios = $"{obj["Manufacturer"]} {obj["SMBIOSBIOSVersion"]}".Trim();
                break;
            }
        }
        catch
        {
            _cachedBios = "Unknown BIOS";
        }
    }

    private IReadOnlySet<HpThermalMode> GetSupportedBiosModes(bool isAdmin)
    {
        if (_cachedSupportedModes is { Count: > 0 })
            return _cachedSupportedModes;

        var defaultSet = new HashSet<HpThermalMode>
        {
            HpThermalMode.SmartSense,
            HpThermalMode.Balanced,
            HpThermalMode.Performance,
            HpThermalMode.Cool,
            HpThermalMode.Quiet,
            HpThermalMode.PowerSaver
        };

        if (!isAdmin)
            return defaultSet;

        try
        {
            if (ExecuteBiosReadCommand(CmdTypeSupportedModes, "hpqBIOSInt128", out byte[]? data, out uint rc, out _)
                && rc == 0 && data is { Length: >= 7 })
            {
                byte capByte = data[6];
                var modes = new HashSet<HpThermalMode>();
                foreach (var (bitIndex, mode) in ModeCapabilityBits)
                {
                    if (IsBitSet(capByte, bitIndex))
                        modes.Add(mode);
                }

                if (modes.Count > 0)
                {
                    _cachedSupportedModes = modes;
                    return modes;
                }
            }
        }
        catch { }

        return defaultSet;
    }

    private static bool IsBitSet(byte b, int bitIndex)
    {
        // Matches HP.SystemControl.Utility.Features.SystemControl.SystemControlCoreBase.IsBitExists(byte b, int bit)
        return ((b >> bitIndex) & 1) == 1;
    }

    private static HpThermalMode? GetCurrentBiosMode(bool isAdmin, out string statusMessage)
    {
        if (!isAdmin)
        {
            statusMessage = "Running without Admin rights (BIOS WMI read requires elevation).";
            return null;
        }

        if (ExecuteBiosReadCommand(CmdTypeSystemControl, "hpqBIOSInt4", out byte[]? data, out uint rc, out string err))
        {
            if (rc == 0 && data is { Length: > 0 })
            {
                byte rawMode = data[0];
                if (Enum.IsDefined(typeof(HpThermalMode), (int)rawMode))
                {
                    var mode = (HpThermalMode)rawMode;
                    statusMessage = $"BIOS Thermal Mode active: {mode} (0x{rawMode:X2})";
                    return mode;
                }
                statusMessage = $"BIOS returned mode byte 0x{rawMode:X2}";
                return null;
            }
            statusMessage = $"BIOS WMI read returned code {rc}";
            return null;
        }

        statusMessage = $"BIOS WMI query error: {err}";
        return null;
    }

    private static bool ExecuteBiosReadCommand(
        uint commandType,
        string methodName,
        out byte[]? returnData,
        out uint returnCode,
        out string error)
    {
        returnData = null;
        returnCode = 0xFFFF;
        error = string.Empty;

        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM hpqBIntM"));
            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inDataClass = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null);
                    using ManagementBaseObject inData = inDataClass.CreateInstance();
                    inData["Sign"] = HpSecuSign;
                    inData["Command"] = CmdRead;
                    inData["CommandType"] = commandType;
                    inData["Size"] = 0u;

                    using ManagementBaseObject inParams = instance.GetMethodParameters(methodName);
                    inParams["InData"] = inData;

                    using ManagementBaseObject? outParams = instance.InvokeMethod(methodName, inParams, null);
                    if (outParams?["OutData"] is ManagementBaseObject outData)
                    {
                        returnCode = Convert.ToUInt32(outData["rwReturnCode"]);
                        returnData = outData["Data"] as byte[];
                        return true;
                    }
                }
            }

            error = "No hpqBIntM WMI instance found.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool ExecuteBiosWriteCommand(
        uint commandType,
        byte[] payload,
        out uint returnCode,
        out string error)
    {
        returnCode = 0xFFFF;
        error = string.Empty;

        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM hpqBIntM"));
            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inDataClass = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null);
                    using ManagementBaseObject inData = inDataClass.CreateInstance();
                    inData["Sign"] = HpSecuSign;
                    inData["Command"] = CmdWrite;
                    inData["CommandType"] = commandType;
                    inData["Size"] = (uint)payload.Length;
                    inData["hpqBData"] = payload;

                    // Try hpqBIOSInt0 first (0-byte return data, matchingSetThermalControlMode returnDataSize=0), fallback to hpqBIOSInt4
                    foreach (string methodName in new[] { "hpqBIOSInt0", "hpqBIOSInt4" })
                    {
                        using ManagementBaseObject inParams = instance.GetMethodParameters(methodName);
                        inParams["InData"] = inData;

                        using ManagementBaseObject? outParams = instance.InvokeMethod(methodName, inParams, null);
                        if (outParams?["OutData"] is ManagementBaseObject outData)
                        {
                            returnCode = Convert.ToUInt32(outData["rwReturnCode"]);
                            if (returnCode == 0)
                                return true;
                        }
                    }

                    return returnCode == 0;
                }
            }

            error = "No hpqBIntM WMI instance found.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
