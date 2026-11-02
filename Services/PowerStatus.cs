using System.Runtime.InteropServices;

namespace HpPowerSwitcher.Services;

/// <summary>Reads battery/AC state without loading Windows Forms.</summary>
public readonly record struct PowerStatus(bool IsOnAc, int BatteryPercent, bool IsCharging, bool IsKnown)
{
    public string Description => !IsKnown ? "Power state unavailable" :
        $"{(BatteryPercent < 0 ? "Unknown battery" : $"{BatteryPercent}%")} ({(IsOnAc ? IsCharging ? "Charging on AC" : "Plugged in" : "On battery")})";

    public static PowerStatus Read()
    {
        if (!GetSystemPowerStatus(out var value)) return new(false, -1, false, false);
        bool hasBattery = value.BatteryFlag != 255 && (value.BatteryFlag & 128) == 0;
        return new(value.AcLineStatus == 1, hasBattery && value.BatteryLifePercent <= 100 ? value.BatteryLifePercent : -1,
            value.BatteryFlag != 255 && (value.BatteryFlag & 8) != 0, value.AcLineStatus != 255);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
