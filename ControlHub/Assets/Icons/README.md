# Icon assets

Keep application icons grouped by the UI area that owns them.

## Folders

- `TopMenu`: icons for the top navigation menu buttons.
- `ControlPanel`: icons for the operation buttons in the control panel.
- `LoginDialog`: icons for the permission login dialog.

## PNG sizes

- Top menu icon files: `24 x 24 px`.
- Control panel icon files: `32 x 32 px`.
- Login dialog title icon file: `40 x 48 px`.
- Login dialog field icon files: `32 x 32 px`.

## Login dialog icons

Put the permission login dialog icons in `LoginDialog` with these names:

- `permission-login.png`: title icon, displayed at `34 x 40`.
- `user-name.png`: account label icon, displayed at `24 x 28`.
- `password.png`: password label icon, displayed at `24 x 28`.
- `level.png`: role label icon, displayed at `26 x 28`.

Use transparent PNG files with one solid icon color:

- title icon color: `#EEF5FD`
- field icon color: `#1E3043`

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
