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

Also covers all 173 active numeric controls (including the 16 axes' motion parameters),
8 switches, 3 station instrument selections, SM7110 mode/limits, optional cleared
positions and seven vision procedure names. High precision values must survive
save, disk reload, recipe application and another save without rounding. Invalid
drafts and out-of-range values must fail explicitly. The actual Save Current handler
must use edited procedure names, independently of the active vision runtime names.

Parameter position checks exercise all 16 Move/Record buttons with an injected
confirmation response and an instant fake motion card. Cancel must leave saved
coordinates and motion untouched, including initial records and overwrites.
Approved moves use first-set, second-set or lower-camera calibration speeds in
both interpolation modes, even with invalid production speeds or a different
active calibration tab. Invalid calibration speeds must issue no motion commands.
