# Observation compensation checks

Run on Windows from the repository root:

```powershell
dotnet run --project tests/ObservationCompensationChecks/ObservationCompensationChecks.csproj
```

Constructs the views without a main window or connected motion/camera controller.
The view initially reads existing home settings; all test writes use temporary settings
and recipe directories. No hardware commands are issued. A rendered parameter card
is saved in the printed temporary artifact directory.

Checks signed XY pulse offsets, R degrees at 131072 pulses/revolution (including full
turns), nozzle 1 success-only application, disabled/failure/nozzle 2 passthrough,
production snapshot isolation, invalid and overflowing inputs, automatic persistence,
recipe round trips, runtime control locks and parameter card layout.
