# ControlHub

## Project layout

- `Assets`: UI images and icon resources.
- `Data`: temporary seed data used to populate the current dashboard.
- `Models`: axis, alarm, station, and I/O data models.
- `Services/Motion`: motion-card abstractions and hardware integrations.
- `Services/Persistence`: local JSON stores for remembered UI settings.
- `ViewModels`: bindable UI state and notification helpers.
- `Views`: WPF windows and dialogs.

## Development notes

- Keep view-specific interaction code in the matching `Views/*.xaml.cs` file.
- Keep bindable screen state in `ViewModels`.
- Keep hardware calls behind interfaces in `Services/Motion`.
- Keep persisted UI data behind stores in `Services/Persistence`.
- Replace `Data/DashboardSeedData` with real runtime data sources as the device integration grows.

## Local files

The running application writes these JSON files next to the executable:

- `axis-settings.json`: axis name, jog speed, and jog distance/absolute target by axis number.
- `remembered-login.json`: last successful login user name and role for dialog prefilling.
- `motion-settings.json`: real card selection, polling, every per-axis move-profile input, per-axis EtherCAT homing parameters, explicit homing sequence, wait/tolerance settings, and the shared homing timeout.
- `vibration-feeder-settings.json`: vibration feeder TCP endpoint and line-ending settings. Feeder communication uses fixed 2-second connect/write timeouts and ASCII payloads.
  It also stores the directional-vibration frequency, amplitude, and pulse duration used by the eight direction buttons and the scatter/gather controls.

Legacy `axis-names.json` and `permission-session.json` files are still read when present so older local data is not lost.

## EtherCAT motion control

The motion page now uses the real Leadshine EtherCAT API by default. It discovers the hardware card ID with
`dmc_get_CardInfList`, maps displayed axis 1 to hardware axis 0, checks the EtherCAT bus before commands, and polls
feedback position, target, speed, CiA 402 state, limits, homing result, stop reason, and local digital inputs.
The homing page now edits each axis independently (enable, numeric mode, low/high speed, acceleration/deceleration, offset,
and sequence order), with a clearly labelled shared homing timeout. Sequence order `0` excludes the axis from multi-axis homing;
there is no hidden fallback sequence. Valid motion inputs are saved automatically on focus change, axis change, and normal shutdown, then
restored the next time the same build is opened. If saving fails during shutdown, the window stays open and reports why.
Invalid or unsaved homing input disables both homing commands, so a stale profile cannot be executed.
Until the card opens successfully, the axis table, I/O area, and alarm table remain empty and all motion controls are disabled.
The I/O tabs are live: digital inputs and outputs are read back from the selected local port, digital outputs can be
toggled, analog inputs are polled, and analog outputs are written only after pressing the channel's `写` button.

Before running on the machine:

1. Install the Leadshine driver/runtime and the **x86 .NET 9 Windows Desktop Runtime**. This repository ships the
   32-bit Leadshine `LTDMC.dll`, so both `ControlHub.exe` and the DLL beside it must be x86; do not substitute an x64 DLL
   or rely on an x64-only .NET runtime.
2. Use Leadshine Motion to scan the EtherCAT slaves and download the ENI configuration.
3. Review `motion-settings.json`. `CardNo: null` selects the first detected hardware ID.
4. On the homing tab, enter the selected axis mode, speeds, acceleration/deceleration, offset, shared timeout, and sequence
   order, then check `启用当前轴回零`. Use sequence order `0` until the machine-safe multi-axis order has been verified.
   Homing is deliberately disabled by default because the correct mode depends on the drive, sensor wiring, and mechanism.
5. Confirm that the displayed 1-based axis order matches hardware axes 0 through 15 before enabling motion.

`ControlHub.csproj` fixes `PlatformTarget=x86` and `RuntimeIdentifier=win-x86`. The solution's `Any CPU` and `x64`
configuration labels are compatibility labels only; they still build this project as x86 and do not change the process
architecture. Use the generated `win-x86` output and keep its copied `LTDMC.dll` next to `ControlHub.exe`.

Set `SimulationMode` to `true` only for explicit offline UI testing. Simulation performs time-based moves and status
updates; hardware mode never falls back silently to simulation.

Live DO/AO values are read from hardware and are not replayed automatically after restart. This is intentional: restoring
an output command at startup could energize equipment unexpectedly.

## Vibration feeder TCP connection

The vibration feeder connection is a raw TCP client. Existing ASCII/HEX device commands are sent unchanged over the
socket; this is transport-level TCP, not Modbus TCP register framing. Configure the feeder's IP address and listening
port on the connection page. The UI reports remote disconnects automatically, and connection/write operations use the
configured timeouts.
