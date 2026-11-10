# USB microscope preview checks

Run on Windows with the x86 .NET 9 Windows Desktop Runtime:

```powershell
dotnet run --project tests/MicroscopePreviewChecks/MicroscopePreviewChecks.csproj -c Release
```

An optional output-directory argument writes a home-page PNG for layout inspection.

The checks construct the home and microscope views without starting the main window,
opening a camera, or attaching a motion controller. A generated 640 × 480 frame verifies:

- Single ownership of the shared preview as pages are hidden and shown.
- Unfinished corner selection, completed shapes, frame, and zoom surviving navigation.
- Rectangle/square and circle fitting from image coordinates, undo, clear, and zoom reset.
- Annotated photo rendering at the original image resolution.
- Usable preview height and wrapping toolbar at normal and compact home sizes.

The test invokes button routed events and supplies image points directly; it does not
simulate physical mouse input or validate USB streaming. On a connected microscope,
check home → motion → home, home → calibration → home, home → connection → home,
and home → parameters → home while watching a moving object. The image should remain
live without reconnecting. Also verify drawing, dragging, zooming, and photo saving on
home, then explicitly disconnect and confirm that the preview clears.
