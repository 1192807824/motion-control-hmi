# Vision window responsiveness checks

Run on Windows from the repository root:

```powershell
dotnet run --project tests/VisionWindowResponsivenessChecks/VisionWindowResponsivenessChecks.csproj
```

Uses two hidden native WPF windows in separate processes and the production layout
message implementation. The receiver deliberately blocks its UI thread to simulate
a synchronous camera inspection. The checks verify that layout requests return
promptly and three asynchronous sequences on the sender dispatcher can continue.
They also check duplicate suppression, applying the latest parent size, reparenting,
and retry after an invalid target. No machine application, SDK, camera, motion card,
or persisted user settings are opened.

This verifies the identified window-layout blocking path, not real hardware timing
or the full production scheduler. Both ControlHub and VisionMasterHost must be rebuilt
together because the resize message is handled by the vision host.

The legacy SetWindowPos call is now measured too, without assuming it blocks in
this hidden-window setup. On the development machine it returned immediately and
all continuations progressed; the original test therefore does NOT establish that
window resizing caused the reported machine stall.

The diagnostic-session check additionally verifies that a background heartbeat
records a deliberately blocked dispatcher and that an unload-task exception is
logged while the camera step is still pending. Production logs are written under
`logs/production` beside the running executable, with a build MVID, phase durations,
cached axis feedback, pause/stop flags, and task exceptions. Logging issues do not
throw into production, and no additional hardware reads or commands are issued.
