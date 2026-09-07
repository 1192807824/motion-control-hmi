# Production scheduling checks

Run from the repository root:

```powershell
dotnet run --project tests/ProductionSchedulingChecks/ProductionSchedulingChecks.csproj
```

The checks compile the production scheduler and motion configuration sources directly.
They do not open the motion card, start the WPF application, or send hardware commands.

Coverage includes migration from existing polling settings to 10 ms while preserving
commissioned axis profiles, polling validation, queued second-set operation, separate
pickup and BIN completion, empty batches, failures before and after pickup, and cancellation.

Build the application separately with `dotnet build ControlHub.sln` to check integration.
Windows scheduling latency and real machine throughput require hardware measurement.
