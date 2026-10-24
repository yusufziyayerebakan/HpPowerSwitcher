using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HpPowerSwitcher.Services;

/// <summary>One telemetry reading. Unavailable values are <see cref="double.NaN"/>.</summary>
public readonly record struct SensorSample(
    double CpuTempC,
    double CpuLoadPct,
    double CpuFreqMhz,
    double Fan1Rpm,
    double Fan2Rpm,
    double MemLoadPct,
    double MemUsedGb,
    double MemTotalGb,
    double BatteryWatts,
    bool BatteryCharging,
    double SampleMs,
    double[] CpuCoreLoads,
    double GpuLoadPct,
    double DiskActivePct,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec,
    double WifiRxBytesPerSec,
    double WifiTxBytesPerSec,
    double WifiLinkSpeedBps,
    string WifiName,
    double NpuLoadPct,
    string NpuName,
    string GpuName);

/// <summary>
/// Low-overhead sensor reader. Fast sensors (CPU load/frequency/memory/thermal-zone counters,
/// per-core loads, disk, wifi, and PDH GPU/NPU) are read on every call; slow sensors
/// (HP BIOS fan WMI, battery WMI) are throttled to <see cref="SlowIntervalMs"/> and
/// automatically disabled after repeated failures.
/// </summary>
public sealed class HardwareSensorService : IDisposable
{
    private const int SlowIntervalMs = 1000;
    private const int MaxSlowFailures = 3;
    private const uint CmdTypeFanLevels = 0x2D; // HP BIOS: fan speeds, units of 100 RPM

    private bool _initialized;
    private PerformanceCounter? _cpuUtility;
    private PerformanceCounter? _cpuPerformance;
    private PerformanceCounter? _cpuFrequency;
    private PerformanceCounter?[] _coreCounters = [];
    private readonly List<PerformanceCounter> _thermalCounters = [];

    // Disk counters
    private PerformanceCounter? _diskTime;
    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;

    // Wi-Fi / Network tracking
    private NetworkInterface? _activeWifi;
    private long _lastWifiRx, _lastWifiTx;
    private long _lastWifiTicks;
    private double _lastWifiRxSpeed, _lastWifiTxSpeed;
    private string _wifiName = "Wi-Fi";
    private double _wifiLinkSpeed;

    // PDH GPU & NPU tracking
    private IntPtr _pdhQuery = IntPtr.Zero;
    private IntPtr _gpuCounter = IntPtr.Zero;
    private bool _pdhInitialized;
    private double _lastGpuLoad = double.NaN;
    private double _lastNpuLoad = double.NaN;
    private string _gpuName = "GPU";
    private string _npuName = "NPU";

    private long _lastSlowTicks;
    private double _fan1 = double.NaN, _fan2 = double.NaN;
    private double _batteryWatts = double.NaN;
    private bool _batteryCharging;
    private double _wmiTemp = double.NaN;

    private int _fanFailures, _batteryFailures, _acpiFailures;
    private (uint Cmd, string Method)? _fanCombo;
    private ManagementObjectSearcher? _batterySearcher;
    private ManagementObjectSearcher? _acpiSearcher;

    /// <summary>Human readable summary of which sensor sources are active (valid after first sample).</summary>
    public string SourceSummary { get; private set; } = "Initializing sensors...";

    /// <summary>Why fan data is (un)available.</summary>
    public string FanStatus { get; private set; } = "waiting for first reading";

    public SensorSample Sample()
    {
        long start = Stopwatch.GetTimestamp();
        EnsureInitialized();

        // 1. Overall CPU Load & Frequency
        double load = Read(_cpuUtility);
        if (!double.IsNaN(load)) load = Math.Clamp(load, 0, 100);

        double freq = double.NaN;
        double baseFreq = Read(_cpuFrequency);
        double perf = Read(_cpuPerformance);
        if (!double.IsNaN(baseFreq))
            freq = double.IsNaN(perf) ? baseFreq : baseFreq * perf / 100.0;

        // 2. Per-Core CPU Loads
        double[] coreLoads = new double[_coreCounters.Length];
        for (int i = 0; i < _coreCounters.Length; i++)
        {
            double cVal = Read(_coreCounters[i]);
            coreLoads[i] = double.IsNaN(cVal) ? double.NaN : Math.Clamp(cVal, 0, 100);
        }

        // 3. Thermal zones
        double temp = ReadThermalZones();

        // 4. Slow sensors: Fans, Battery, ACPI fallback
        bool slowDue = Stopwatch.GetElapsedTime(_lastSlowTicks).TotalMilliseconds >= SlowIntervalMs || _lastSlowTicks == 0;
        if (slowDue)
        {
            _lastSlowTicks = Stopwatch.GetTimestamp();
            ReadFans();
            ReadBattery();
            if (_thermalCounters.Count == 0)
                ReadAcpiTemperature();
        }

        if (double.IsNaN(temp)) temp = _wmiTemp;

        // 5. Memory
        double memLoad = double.NaN, memUsed = double.NaN, memTotal = double.NaN;
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            memLoad = mem.dwMemoryLoad;
            memTotal = mem.ullTotalPhys / 1073741824.0;
            memUsed = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        }

        // 6. Disk Usage
        double diskPct = Read(_diskTime);
        if (!double.IsNaN(diskPct)) diskPct = Math.Clamp(diskPct, 0, 100);
        double diskRead = Read(_diskRead);
        double diskWrite = Read(_diskWrite);

        // 7. Wi-Fi / Network
        SampleWifi();

        // 8. GPU & NPU via PDH
        SampleGpuAndNpu();

        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new SensorSample(
            temp, load, freq, _fan1, _fan2, memLoad, memUsed, memTotal,
            _batteryWatts, _batteryCharging, ms,
            coreLoads,
            _lastGpuLoad,
            diskPct, diskRead, diskWrite,
            _lastWifiRxSpeed, _lastWifiTxSpeed, _wifiLinkSpeed, _wifiName,
            _lastNpuLoad, _npuName, _gpuName);
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        // CPU Overall
        _cpuUtility = TryCreate("Processor Information", "% Processor Utility", "_Total");
        _cpuPerformance = TryCreate("Processor Information", "% Processor Performance", "_Total");
        _cpuFrequency = TryCreate("Processor Information", "Processor Frequency", "_Total");

        // CPU Per-Core Counters
        int coreCount = Environment.ProcessorCount;
        _coreCounters = new PerformanceCounter?[coreCount];
        for (int i = 0; i < coreCount; i++)
        {
            _coreCounters[i] = TryCreate("Processor Information", "% Processor Utility", $"0,{i}")
                            ?? TryCreate("Processor Information", "% Processor Utility", $"{i}")
                            ?? TryCreate("Processor", "% Processor Time", $"{i}");
            _ = Read(_coreCounters[i]);
        }

        // Thermal Zones
        try
        {
            if (PerformanceCounterCategory.Exists("Thermal Zone Information"))
            {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                foreach (string inst in cat.GetInstanceNames())
                {
                    var c = TryCreate("Thermal Zone Information", "Temperature", inst);
                    if (c != null) _thermalCounters.Add(c);
                }
            }
        }
        catch { }

        // Disk Counters
        _diskTime = TryCreate("PhysicalDisk", "% Disk Time", "_Total")
                 ?? TryCreate("PhysicalDisk", "% Disk Time", "0");
        _diskRead = TryCreate("PhysicalDisk", "Disk Read Bytes/sec", "_Total")
                 ?? TryCreate("PhysicalDisk", "Disk Read Bytes/sec", "0");
        _diskWrite = TryCreate("PhysicalDisk", "Disk Write Bytes/sec", "_Total")
                  ?? TryCreate("PhysicalDisk", "Disk Write Bytes/sec", "0");

        // Wi-Fi / Network init
        InitWifi();

        // PDH GPU / NPU init
        InitPdhGpuNpu();

        // Hardware names background probe
        _ = Task.Run(ProbeHardwareNames);

        // Prime rate-based counters (first NextValue always returns 0)
        _ = Read(_cpuUtility);
        _ = Read(_cpuPerformance);
        _ = Read(_diskTime);
        _ = Read(_diskRead);
        _ = Read(_diskWrite);

        bool isAdmin = HpPowerService.IsRunningAsAdministrator();
        SourceSummary =
            $"CPU: {coreCount} cores  •  " +
            $"GPU: {(_pdhInitialized ? "PDH" : "n/a")}  •  " +
            $"Disk: {(_diskTime != null ? "perf counters" : "n/a")}  •  " +
            $"Wi-Fi: {_wifiName}  •  " +
            $"NPU: {(_lastNpuLoad >= 0 || _pdhInitialized ? "active" : "standby")}";

        if (!isAdmin)
        {
            _fanFailures = MaxSlowFailures;
            FanStatus = "run as Administrator to read fans";
        }
        if (_thermalCounters.Count == 0 && !isAdmin) _acpiFailures = MaxSlowFailures;
    }

    private void InitWifi()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            _activeWifi = interfaces.FirstOrDefault(n =>
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                n.OperationalStatus == OperationalStatus.Up)
                ?? interfaces.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up)
                ?? interfaces.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

            if (_activeWifi != null)
            {
                _wifiName = _activeWifi.Description;
                _wifiLinkSpeed = _activeWifi.Speed;
                var stats = _activeWifi.GetIPStatistics();
                _lastWifiRx = stats.BytesReceived;
                _lastWifiTx = stats.BytesSent;
                _lastWifiTicks = Stopwatch.GetTimestamp();
            }
        }
        catch { }
    }

    private void SampleWifi()
    {
        try
        {
            if (_activeWifi == null || _activeWifi.OperationalStatus != OperationalStatus.Up)
            {
                InitWifi();
                if (_activeWifi == null) return;
            }

            var stats = _activeWifi.GetIPStatistics();
            long rx = stats.BytesReceived;
            long tx = stats.BytesSent;
            long nowTicks = Stopwatch.GetTimestamp();
            double dt = Stopwatch.GetElapsedTime(_lastWifiTicks, nowTicks).TotalSeconds;

            if (dt > 0.1 && _lastWifiTicks > 0)
            {
                long dRx = rx - _lastWifiRx;
                long dTx = tx - _lastWifiTx;
                if (dRx >= 0) _lastWifiRxSpeed = dRx / dt;
                if (dTx >= 0) _lastWifiTxSpeed = dTx / dt;
            }

            _lastWifiRx = rx;
            _lastWifiTx = tx;
            _lastWifiTicks = nowTicks;
            _wifiLinkSpeed = _activeWifi.Speed;
        }
        catch { }
    }

    private void InitPdhGpuNpu()
    {
        try
        {
            uint status = PdhOpenQuery(null, IntPtr.Zero, out _pdhQuery);
            if (status != ERROR_SUCCESS) return;

            status = PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter);
            if (status == ERROR_SUCCESS)
            {
                _pdhInitialized = true;
                _ = PdhCollectQueryData(_pdhQuery);
                _lastGpuLoad = 0;
                _lastNpuLoad = 0;
            }
        }
        catch
        {
            _pdhInitialized = false;
        }
    }

    private void SampleGpuAndNpu()
    {
        if (!_pdhInitialized || _pdhQuery == IntPtr.Zero || _gpuCounter == IntPtr.Zero)
            return;

        try
        {
            uint status = PdhCollectQueryData(_pdhQuery);
            if (status != ERROR_SUCCESS) return;

            uint bufferSize = 0;
            uint itemCount = 0;
            status = PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE, ref bufferSize, out itemCount, IntPtr.Zero);
            if ((status == ERROR_SUCCESS || status == PDH_MORE_DATA) && bufferSize > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    status = PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE, ref bufferSize, out itemCount, buffer);
                    if (status == ERROR_SUCCESS && itemCount > 0)
                    {
                        int structSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
                        long ptr = buffer.ToInt64();
                        double gpuSum = 0;
                        double npuSum = 0;

                        for (uint i = 0; i < itemCount; i++)
                        {
                            var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(new IntPtr(ptr));
                            string? name = Marshal.PtrToStringUni(item.szName);
                            double val = item.FmtValue.doubleValue;

                            if (!double.IsNaN(val) && val > 0 && !string.IsNullOrEmpty(name))
                            {
                                if (name.Contains("npu", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("neural", StringComparison.OrdinalIgnoreCase))
                                {
                                    npuSum += val;
                                }
                                else if (name.Contains("3D", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("Compute", StringComparison.OrdinalIgnoreCase))
                                {
                                    gpuSum += val;
                                }
                            }

                            ptr += structSize;
                        }

                        _lastGpuLoad = Math.Clamp(gpuSum, 0, 100);
                        _lastNpuLoad = Math.Clamp(npuSum, 0, 100);
                    }
                    else if (status == ERROR_SUCCESS)
                    {
                        _lastGpuLoad = 0;
                        _lastNpuLoad = 0;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch { }
    }

    private void ProbeHardwareNames()
    {
        try
        {
            using var searcherGpu = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject o in searcherGpu.Get())
            {
                using (o)
                {
                    string? name = o["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _gpuName = name;
                        break;
                    }
                }
            }
        }
        catch { }

        try
        {
            using var searcherNpu = new ManagementObjectSearcher(
                "SELECT Caption, Name FROM Win32_PnPEntity WHERE Caption LIKE '%AI Boost%' OR Caption LIKE '%NPU%' OR Caption LIKE '%Neural%'");
            foreach (ManagementObject o in searcherNpu.Get())
            {
                using (o)
                {
                    string? name = o["Caption"]?.ToString() ?? o["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _npuName = name;
                        break;
                    }
                }
            }
        }
        catch { }
    }

    private static PerformanceCounter? TryCreate(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue();
            return c;
        }
        catch
        {
            return null;
        }
    }

    private static double Read(PerformanceCounter? c)
    {
        if (c == null) return double.NaN;
        try { return c.NextValue(); }
        catch { return double.NaN; }
    }

    private double ReadThermalZones()
    {
        double best = double.NaN;
        foreach (var c in _thermalCounters)
        {
            double celsius = Read(c) - 273.15;
            if (celsius is > 0 and < 130 && (double.IsNaN(best) || celsius > best))
                best = celsius;
        }
        return best;
    }

    private void ReadAcpiTemperature()
    {
        if (_acpiFailures >= MaxSlowFailures) return;
        try
        {
            _acpiSearcher ??= new ManagementObjectSearcher(@"\\.\root\wmi",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true });

            double best = double.NaN;
            foreach (ManagementObject o in _acpiSearcher.Get())
            {
                using (o)
                {
                    double c = Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15;
                    if (c is > 0 and < 130 && (double.IsNaN(best) || c > best)) best = c;
                }
            }

            _wmiTemp = best;
            _acpiFailures = double.IsNaN(best) ? _acpiFailures + 1 : 0;
        }
        catch
        {
            _acpiFailures++;
            _wmiTemp = double.NaN;
        }
    }

    private void ReadFans()
    {
        if (_fanFailures >= MaxSlowFailures) return;

        // Try the known-good combo first, otherwise probe command codes (legacy 1 / OMEN-style 0x20008)
        // and WMI method variants (different output buffer sizes).
        (uint Cmd, string Method)[] combos = _fanCombo is { } known
            ? [known]
            : [(1u, "hpqBIOSInt128"), (0x20008u, "hpqBIOSInt128"), (1u, "hpqBIOSInt4"), (0x20008u, "hpqBIOSInt4"), (1u, "hpqBIOSInt0"), (0x20008u, "hpqBIOSInt0")];

        string lastErr = "no response";
        foreach (var (cmd, method) in combos)
        {
            if (HpPowerService.ExecuteBiosReadCommand(CmdTypeFanLevels, method, out byte[]? data, out uint rc, out string err, cmd))
            {
                if (rc == 0 && data is { Length: >= 2 })
                {
                    _fanCombo = (cmd, method);
                    _fan1 = data[0] * 100.0;
                    _fan2 = data[1] * 100.0;
                    _fanFailures = 0;
                    FanStatus = $"BIOS cmd 0x{cmd:X} via {method}";
                    return;
                }
                lastErr = $"BIOS returned code {rc}";
            }
            else if (!string.IsNullOrEmpty(err))
            {
                lastErr = err;
            }
        }

        _fanFailures++;
        _fan1 = _fan2 = double.NaN;
        FanStatus = $"unsupported by this BIOS ({lastErr})";
    }

    private void ReadBattery()
    {
        if (_batteryFailures >= MaxSlowFailures) return;
        try
        {
            _batterySearcher ??= new ManagementObjectSearcher(@"\\.\root\wmi",
                "SELECT Charging, Discharging, ChargeRate, DischargeRate FROM BatteryStatus",
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true });

            foreach (ManagementObject o in _batterySearcher.Get())
            {
                using (o)
                {
                    bool charging = o["Charging"] is true;
                    double mw = charging
                        ? Convert.ToDouble(o["ChargeRate"] ?? 0)
                        : Convert.ToDouble(o["DischargeRate"] ?? 0);
                    _batteryCharging = charging;
                    _batteryWatts = Math.Max(0, mw) / 1000.0;
                    _batteryFailures = 0;
                    return;
                }
            }

            _batteryFailures++;
            _batteryWatts = double.NaN;
        }
        catch
        {
            _batteryFailures++;
            _batteryWatts = double.NaN;
        }
    }

    public void Dispose()
    {
        _cpuUtility?.Dispose();
        _cpuPerformance?.Dispose();
        _cpuFrequency?.Dispose();
        foreach (var c in _coreCounters) c?.Dispose();
        _coreCounters = [];
        foreach (var c in _thermalCounters) c.Dispose();
        _thermalCounters.Clear();
        _diskTime?.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();

        if (_pdhQuery != IntPtr.Zero)
        {
            try { PdhCloseQuery(_pdhQuery); } catch { }
            _pdhQuery = IntPtr.Zero;
        }

        _batterySearcher?.Dispose();
        _acpiSearcher?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    // --- PDH Interop ---
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint ERROR_SUCCESS = 0x00000000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
        [FieldOffset(8)] public long longValue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? pDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr phQuery, string pFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCollectQueryData(IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr hCounter,
        uint dwFormat,
        ref uint lpdwBufferSize,
        out uint lpdwItemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCloseQuery(IntPtr phQuery);
}
