# SurfaceEdge

SurfaceEdge adds fast edge-swipe controls to the **Surface Laptop 7**. Use the built-in Precision Touchpad to adjust Windows 11 display brightness and default playback volume without installing a driver, using media keys, or opening Settings.

This is a lightweight Windows ARM64 tray utility for Surface Laptop 7 users who want native touchpad brightness control and volume control with a simple one-finger gesture.

## Features

- Swipe up or down along the left touchpad edge to change display brightness.
- Swipe up or down along the right touchpad edge to change default playback volume.
- Uses the real Windows brightness and volume OSDs instead of a custom overlay.
- Sends a haptic tick on every step and a firmer one at the ends of the range, on touchpads that can play a waveform on command.
- Runs silently from the notification area with no installer, no administrator rights, and no pop-up notifications.
- Optional Start with Windows setting for the current user.
- Fixed 5% edge strip, tuned so a full swipe covers the whole range without overshooting.
- Rejects multi-touch, palm input, physical clicks, and gestures that leave the edge.
- No network access, driver, global mouse hook, or suppressed pointer movement.
- Self-contained Windows ARM64 executable with the .NET runtime included.

## Quick Start

1. Download `SurfaceEdge.exe` from the [latest release](https://github.com/Wintego/Surface-Edge/releases/latest).
2. Run it on a Surface Laptop 7 running Windows 11 ARM64.
3. Start a deliberate vertical swipe with one finger inside the outer 5% of the touchpad:
   - Left edge: display brightness
   - Right edge: playback volume
4. Right-click the tray icon to pause the utility, switch haptic feedback on or off, configure startup, open the log, or exit.

The first short movement arms the gesture without changing the current value. Lift your finger to finish.

## Compatibility

SurfaceEdge targets the Windows Precision Touchpad on the Surface Laptop 7 and is built for ARM64. It uses:

- Windows Raw Input for Precision Touchpad HID reports
- WMI `WmiMonitorBrightness` for the built-in display
- Windows Core Audio for the default multimedia playback endpoint
- The HID Simple Haptic Controller (usage page 0x0E) published by haptic Precision Touchpads
- An undocumented ImmersiveShell COM service for native Windows brightness and volume indicators

Only the built-in display is controlled. Volume targets the current default playback endpoint and unmutes it when setting a nonzero value.

## Haptic feedback

Vibration is only possible where the touchpad accepts haptic commands over HID. SurfaceEdge looks for a Simple Haptic Controller collection and uses it in this order:

1. **Waveform trigger.** If the controller publishes a Manual Trigger control, each step plays a single waveform, resolved from the device's own waveform list, with a firmer waveform at 0 and 100.
2. **Intensity write.** If the controller publishes only an Intensity setting, the actuator is driven by firmware and cannot be commanded. SurfaceEdge can then briefly rewrite that setting, which some firmware answers with a short tick and most ignore. The level the user chose in Windows Settings is always restored.

The default follows the hardware: case 1 is on out of the box, case 2 is off, and a pad with no haptic controller at all leaves the menu entry greyed out. The tray menu can override it, and the choice lasts until the application is closed — nothing is written to disk.

The Surface Laptop 7 pad is case 2, and its firmware does not answer intensity writes: it was tested on the machine this was written for and no pulse could be felt. Its actuator clicks under firmware control on a real press, and the descriptor carries no command that plays a waveform on demand, so nothing in software can make it buzz. Run `SurfaceEdge.exe --verify-haptics` to see which case a given machine falls into and to feel five test pulses.

## Limitations

- Touchpads without a HID waveform trigger cannot be made to vibrate on demand. The Surface Laptop 7 pad is one of them, so haptic feedback does nothing there. See Haptic feedback above.
- The cursor can move while a value is being adjusted because SurfaceEdge does not suppress normal pointer input.
- Windows may still recognize taps. Use a deliberate vertical slide without clicking.
- Protected desktops and the lock screen are not supported.
- Windows updates may change the undocumented OSD service. If an OSD call fails, the adjustment still works and the error is written to the log.
- After waking the device, the next touchpad input rebuilds the HID descriptor cache.

## Build

Building requires the .NET 10 SDK and NuGet access for `System.Management`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The script publishes a self-contained single-file `win-arm64` application, verifies its ARM64 PE header, runs the self-test, and replaces the root `SurfaceEdge.exe` only after the checks pass. Exit any running copy from the tray before building.

If `dotnet` is not on `PATH`, provide its full path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -DotNet "C:\path\to\dotnet.exe"
```

## Diagnostics and Tests

```text
SurfaceEdge.exe --self-test
SurfaceEdge.exe --verify-controls
SurfaceEdge.exe --verify-osd
SurfaceEdge.exe --verify-haptics
SurfaceEdge.exe --diagnose
```

`--self-test` checks gesture logic, native architecture, HID structure sizes, the touchpad descriptor, read-only brightness and volume access, and which haptic controls the touchpad exposes. `--verify-controls` also writes the current values back through the real Windows APIs. `--verify-osd` exercises the native OSD calls without intentionally changing controls. `--verify-haptics` sends five test pulses to the touchpad and reports how many the device accepted; rest a finger on the pad while it runs. `--diagnose` listens for 15 seconds and records sample touchpad coordinates without changing controls.

Logs and test results are stored in `%LOCALAPPDATA%\SurfaceEdge\`. The log is capped at approximately 256 KB and contains no keystrokes.

## License

No license has been specified yet. All rights reserved unless the repository owner states otherwise.
