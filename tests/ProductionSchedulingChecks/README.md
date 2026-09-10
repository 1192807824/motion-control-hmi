# Production scheduling checks

Run from the repository root:

```powershell
dotnet run --project tests/ProductionSchedulingChecks/ProductionSchedulingChecks.csproj
```

The checks compile the motion configuration and unload sequencing sources directly.
They do not open the motion card, start the WPF application, or send hardware commands.

Coverage includes migration from existing polling settings to 10 ms while preserving
commissioned axis profiles, and polling validation. Unload sequencing checks cover:

- Returning queued task handles while previous BIN placement/return is still pending.
- Keeping the next DD pickup gate closed until the new batch has been safely picked.
- Preventing the next batch from reusing the second-set axes before full BIN return.
- Propagating previous-placement, pickup, and late BIN failures without hanging a gate.
- Cancellation before/during pickup, preserving previous-operation cleanup on stop.
- Empty batches and a pending pause/resume gate.

These are deterministic task-level checks using completion signals, not motion-card
simulation. The second-set pick/place motion order is unchanged by this sequencing.

Build the application separately with `dotnet build ControlHub.sln` to check integration.
Windows scheduling latency and real machine throughput require hardware measurement.
