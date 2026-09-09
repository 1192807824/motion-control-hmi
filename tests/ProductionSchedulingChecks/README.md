# Production scheduling checks

Run from the repository root:

```powershell
dotnet run --project tests/ProductionSchedulingChecks/ProductionSchedulingChecks.csproj
```

The checks compile the motion configuration source directly.
They do not open the motion card, start the WPF application, or send hardware commands.

Coverage includes migration from existing polling settings to 10 ms while preserving
commissioned axis profiles, and polling validation. The second-set parallel scheduler
has been reverted; its checks were removed with that implementation.

Build the application separately with `dotnet build ControlHub.sln` to check integration.
Windows scheduling latency and real machine throughput require hardware measurement.
