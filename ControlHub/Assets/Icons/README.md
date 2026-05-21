# Icon assets

Keep application icons grouped by the UI area that owns them.

## Folders

- `TopMenu`: icons for the top navigation menu buttons.
- `ControlPanel`: icons for the operation buttons in the control panel.

## PNG sizes

- Top menu icon files: `24 x 24 px`.
- Control panel icon files: `32 x 32 px`.

Use a transparent PNG canvas and keep the visible artwork centered with a small
inner margin. Keep one naming style across the project:

- lowercase English names
- words separated with hyphens
- one action per file, for example `motion-control.png` or `start.png`

The project includes PNG files under this folder as WPF resources. Reference
them with pack-style project paths such as:

```xaml
<Image Source="/Assets/Icons/TopMenu/home.png" />
```
