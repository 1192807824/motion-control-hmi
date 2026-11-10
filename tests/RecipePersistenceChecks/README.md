# Recipe persistence checks

Run on Windows from the repository root:

```powershell
dotnet run --project tests/RecipePersistenceChecks/RecipePersistenceChecks.csproj
```

Uses real WPF parameter controls without loading a main window or attaching hardware.
The home view initially reads existing settings; all subsequent writes use temporary
settings and recipe directories.

Checks creation, edits, save and A/B/A application with different pickup counts,
disk reloads, boundary values, legacy defaults, and rejection of odd, empty,
out-of-range, fractional, nonnumeric and overflowing drafts. Both New and Save Current
must report the validation error without creating or overwriting a recipe.

Also covers all 176 active numeric recipe controls (including the 16 axes' motion parameters),
10 switches, 3 station instrument selections, SM7110 mode/limits, optional cleared
positions and eight vision procedure names. High precision values must survive
save, disk reload, recipe application and another save without rounding. Invalid
drafts and out-of-range values must fail explicitly. The actual Save Current handler
must use edited procedure names, independently of the active vision runtime names.
Manual drive-object writes and the independent machine pressure threshold are
excluded from recipe numeric validation.

Recognized-center procedure coverage includes separate A/B/A names, missing-field
and legacy defaults, Save Names, invalid drafts, actual host startup arguments,
and an eight-row parameter-page render at 1280 and 1680 pixels. Recognition still
resolves source/script modules by name within the selected procedure and retains
the existing coordinate/movement validation.

Photo-settle checks cover the compatible 200ms default, auto-save, integer
0–60000ms bounds, invalid drafts, independent A/B/A recipes, locking during
production and the parameter-page layout. TCP timing is covered separately by
HopperVibrationChecks.

First-set break-vacuum checks verify default-on and recipe persistence, both
nozzles' exact off-only IO writes, synchronous completion even with 60000ms
configured waits, the captured switch value, normal enabled timing, unchanged
second-set pulses and the checkbox fitting the existing Z-parameter card.

Parameter position checks exercise all 16 Move/Record buttons with an injected
confirmation response and an instant fake motion card. Cancel must leave saved
coordinates and motion untouched, including initial records and overwrites.
Approved moves use first-set, second-set or lower-camera calibration speeds in
both interpolation modes, even with invalid production speeds or a different
active calibration tab. Invalid calibration speeds must issue no motion commands.
