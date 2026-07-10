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

- `axis-settings.json`: axis name, jog speed, and jog distance by axis number.
- `remembered-login.json`: last successful login user name and role for dialog prefilling.
- `motion-settings.json`: real card selection, polling, move profile, and per-axis EtherCAT homing configuration.

Legacy `axis-names.json` and `permission-session.json` files are still read when present so older local data is not lost.

## EtherCAT motion control

The motion page now uses the real Leadshine EtherCAT API by default. It discovers the hardware card ID with
`dmc_get_CardInfList`, maps displayed axis 1 to hardware axis 0, checks the EtherCAT bus before commands, and polls
feedback position, target, speed, CiA 402 state, limits, homing result, stop reason, and local digital inputs.
Until the card opens successfully, the axis table, I/O area, and alarm table remain empty and all motion controls are disabled.
The I/O tabs are live: digital inputs and outputs are read back from the selected local port, digital outputs can be
toggled, analog inputs are polled, and analog outputs are written only after pressing the channel's `写` button.

Before running on the machine:

1. Install the Leadshine driver/runtime and put the matching x64/x86 `LTDMC.dll` beside `ControlHub.exe` or on `PATH`.
2. Use Leadshine Motion to scan the EtherCAT slaves and download the ENI configuration.
3. Review `motion-settings.json`. `CardNo: null` selects the first detected hardware ID.
4. Configure the drive-specific homing mode and speeds, then set `HomeProfile.Enabled` to `true`. Homing is deliberately
   disabled by default because mode 33 and its safe speed depend on the drive, sensor wiring, and mechanism.
5. Confirm that the displayed 1-based axis order matches hardware axes 0 through 15 before enabling motion.

Set `SimulationMode` to `true` only for explicit offline UI testing. Simulation performs time-based moves and status
updates; hardware mode never falls back silently to simulation.
