# ControlHub AI Development Guide

This file is for AI coding assistants and future developers working on the
`ControlHub` WPF application.

Read this file before making structural or behavior changes.

## Product context

`ControlHub` is a Windows WPF control dashboard for a 16-axis motion-control
system. The current project is still in an early integration stage:

- The main screen is a production-style dashboard, not a marketing UI.
- Motion-card calls are already behind an interface.
- Several dashboard data sources are still seeded demo data.
- The current UI layout has been adjusted manually and should be changed
  carefully.

## Tech stack

- .NET WPF
- Target framework: `net9.0-windows`
- Nullable reference types enabled
- Implicit usings enabled
- XAML views with code-behind for current UI event wiring
- Small ViewModel layer for bindable screen state
- Local JSON persistence next to the executable

## Repository entry points

- Solution: `../ControlHub.sln`
- Project: `ControlHub.csproj`
- Application startup: `App.xaml`
- Main view: `Views/MainWindow.xaml`
- Main code-behind: `Views/MainWindow.xaml.cs`
- Main screen state: `ViewModels/MainWindowViewModel.cs`

## Folder responsibilities

| Folder | Responsibility |
| --- | --- |
| `Assets` | PNG icons and visual resources |
| `Data` | Temporary dashboard seed data before real runtime sources replace it |
| `Models` | Axis, alarm, I/O, and station data models |
| `Services/Motion` | Motion-card contracts and hardware implementation |
| `Services/Persistence` | Local JSON persistence stores |
| `ViewModels` | Bindable view state and property notification helpers |
| `Views` | WPF windows and dialogs |

Do not move behavior back into random root-level files after this structure has
been established.

## Current behavior that should be preserved

### Main window

- The motion page loads `motion-settings.json` and opens real Leadshine hardware by default.
- Offline simulation must be enabled explicitly with `SimulationMode: true`; hardware errors never silently fall back.
- The top clock refreshes every second.
- The top permission text starts as `用户权限：未登录` when the application
  starts.
- The motion-control dashboard is the first screen.

### Axis table

- Before a motion card opens successfully, the axis, I/O, and alarm collections stay empty.
- After connection, the axis table is populated up to the configured `AxisCount` and detected hardware axis count.
- Axis names can be edited in the `轴名称` column.
- Axis name, jog speed, and jog distance are persisted by axis number.
- Clicking outside the axis table clears the selected blue row.
- Editing must be committed before saving on close or outside-table click.
- Jog buttons use `JogDistance` and `JogSpeed` and call
  `IMotionCard.MoveRelative`.

### Alarm table

- Clicking outside the alarm table clears its selected blue row.

### Login dialog

- Successful login remembers the last account name and role for later dialog
  prefilling.
- It does not persist a logged-in application state across restarts.
- It does not store passwords.
- On the next application startup the user is still shown as not logged in
  until a new successful login happens.

## Local persisted files

The application stores JSON files beside the executable.

### `axis-settings.json`

Stores axis UI settings by axis number:

```json
{
  "1": {
    "Name": "X1",
    "JogSpeed": 25,
    "JogDistance": 10
  }
}
```

Implementation:
`Services/Persistence/AxisSettingsStore.cs`

The store still reads legacy `axis-names.json` when present.

### `remembered-login.json`

Stores only the last successful login user name and role:

```json
{
  "UserName": "engineer",
  "Role": "工程师"
}
```

Implementation:
`Services/Persistence/RememberedLoginStore.cs`

The store still reads legacy `permission-session.json` when present.

## Seed data and real integration boundary

`Data/DashboardSeedData.cs` currently creates:

- Initial axis rows
- The I/O point demo set
- Alarm record demo rows

When adding real device/runtime data:

1. Keep hardware-facing code behind service interfaces.
2. Replace seed data consumption gradually.
3. Do not mix device polling, file persistence, and XAML event logic in one
   method.

## Motion layer notes

- Use `IMotionCard` from `Services/Motion`.
- The current implementation is `LeisaiMotionCard`.
- `LeisaiMotionCard` wraps `LTDMC.dll` native calls.
- Keep native interop details inside the motion service.
- Keep real hardware as the default and retain explicit simulation mode for local UI development.
- UI axis numbers are 1-based; `AxisStatus.HardwareAxisNo` maps them to the card's 0-based axes.
- EtherCAT servo state is read from `nmc_get_axis_state_machine`; do not use a UI flag as hardware truth.
- EtherCAT homing on the commissioned E3064S uses `nmc_set_home_profile`, `dmc_home_move`, and `dmc_get_home_result`.

## UI guidance for this project

- This is an operational dashboard. Favor dense, stable, scannable layouts.
- Keep the axis table readable; do not reclaim too much width from operational
  columns just to enlarge one field.
- The right-side I/O and alarm area is narrower than the axis area by design.
- The current I/O seed layout uses 20 visible points in a `5 x 4` arrangement.
- Top-menu icon file names use lowercase English and hyphens.
- Keep menu icons under `Assets/Icons/TopMenu`.
- Keep login dialog icons under `Assets/Icons/LoginDialog`.

## Code organization rules

- Put reusable bindable state in `ViewModels`.
- Put reusable device and persistence logic in `Services`.
- Put current visual interaction code in the matching view code-behind until a
  command-based refactor is intentionally done.
- Keep models small and focused on state representation.
- Add new dialog windows under `Views/Dialogs`.
- Add new UI asset families under a named folder in `Assets`.
- Prefer existing naming conventions over new naming styles.

## What not to do

- Do not store passwords in JSON or source code.
- Do not treat remembered login data as an authenticated session.
- Do not bypass `IMotionCard` from XAML code.
- Do not put new persistence files in `AppData`; current local settings are
  intentionally stored beside the executable.
- Do not reintroduce root-level `MainWindow.xaml` or root-level dialog files.
- Do not remove legacy-settings compatibility without checking existing user
  data first.

## Build and verification

Normal solution build:

```powershell
dotnet build .\ControlHub.sln
```

When the app or Visual Studio locks default output files, use a separate build
output folder:

```powershell
dotnet build .\ControlHub.sln -p:UseAppHost=false -p:OutputPath=.\bin\CodexVerify\
```

After view relocation or XAML changes, verify at minimum:

- The solution builds.
- `App.xaml` still starts `Views/MainWindow.xaml`.
- XAML class names and code-behind namespaces still match.
- Resource paths still point to existing PNG files.

## Suggested next refactors

These are reasonable future improvements, not current requirements:

1. Move axis commands from `Views/MainWindow.xaml.cs` into commands or an
   application-service layer.
2. Replace `DashboardSeedData` with runtime services for axis, I/O, and alarm
   data.
3. Add validation for jog speed and jog distance input ranges.
4. Add user and permission policy services before protecting real control
   actions.
