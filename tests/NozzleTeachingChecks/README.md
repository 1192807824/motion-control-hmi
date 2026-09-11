# Independent nozzle teaching checks

Run on Windows with the x86 .NET 9 Windows Desktop Runtime:

```powershell
dotnet run --project tests/NozzleTeachingChecks/NozzleTeachingChecks.csproj -c Release
```

The checks do not initialize the vision SDK, open a camera, command motion, or access
the user's calibration settings. They use temporary settings and profile files.

Coverage:

- Three edge points determine the correct circle regardless of point order.
- Duplicate, collinear, nearly collinear, and nonfinite points are rejected.
- Z1/Z2 draft events and redraw notifications affect only the selected nozzle.
- Malformed host events cannot replace a valid draft.
- Saving either nozzle preserves the other's calibration flag and offsets.
- A profile with only Z2 is valid; saving Z1 later retains Z2.
- Profile and settings reload retain the separately saved results.

Hardware verification: acquire the teaching pair, select three circle-edge points
on Z1, save Z1, then do the same for Z2. Redraw only Z1 and confirm Z2's circle and
saved offset remain unchanged; repeat in the opposite direction. Leave calibration
and return to verify that the last teaching images and annotations return. Starting
a new nine-point calibration or moving to a new teaching/dot operation discards the
old image drafts. Saved offsets are preserved during reteaching and only the selected
nozzle's successful save replaces its result. A new nine-point calibration still
requires reteaching offsets for the new calibration matrix.

Saved offsets persist across application restarts. Retained SDK teaching images and
unfinished circle selections are kept for the current application session.
