# HP Power Switcher

A native WinUI 3 utility for HP thermal profiles, Windows power modes, HP background components, and live hardware sensors. Targets **.NET 10**, x64, Windows 10 build 19041 or newer.

## Build and run

Install the .NET 10 SDK (or a newer SDK that can target .NET 10) and Windows SDK 10.0.26100.0, or use Visual Studio with the WinUI development tools.

```powershell
dotnet build HpPowerSwitcher.csproj -c Release
```

Run `bin\Release\net10.0-windows10.0.26100.0\win-x64\HpPowerSwitcher.exe`. The executable requests administrator access for HP BIOS WMI and service controls. The target computer needs the x64 .NET 10 runtime; WPF and Windows Forms are no longer used.

## Publish

```powershell
dotnet publish HpPowerSwitcher.csproj -c Release -r win-x64 -o artifacts\publish
```

Distribute the entire publish folder. The Windows App SDK runtime is bundled, so a separate Windows App Runtime installation is unnecessary. The .NET runtime remains framework-dependent. Visual Studio's FolderProfile uses the same configuration.

## Resource usage

- Sensor services, histories, and charts load on the first visit to Live sensors.
- Only the current sensor view creates charts; leaving Sensors or hiding the window releases its chart controls.
- Sensor polling stops off-page, while minimized, and in the tray. History buffers retain only the selected duration at the chosen sampling rate, capped at 3,602 points per series.
- Charts redraw on samples and scrolling, skip charts outside the viewport, and limit line geometry to roughly one point per two pixels.
- Power and service polling runs every eight seconds only on the relevant visible page. Service rows reuse their models and virtualize their controls.
- WMI collections and scheduled-task COM references are disposed promptly. GPU sampling reuses its native buffer, and shutdown waits for in-flight work before disposing counters.

Some sensors depend on HP firmware, administrator rights, and driver-provided counters. Unavailable NPU counters display as unavailable rather than a fabricated zero reading.

## History-buffer checks

```powershell
dotnet run --project Tests\SampleHistory.Tests.csproj -c Release
```

These checks cover bounded allocation, chronological order after wrapping, search boundaries, retaining the latest readings when resizing, and unavailable readings. They do not change hardware, services, registry settings, or scheduled tasks.
