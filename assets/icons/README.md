# CodeChatSync — icon assets

## Files

- `app-icon.ico` — multi-resolution icon (16 to 256 px) for the app/exe
  icon: Windows shortcut, Explorer, taskbar app icon, Start menu.
- `tray-icon.ico` — multi-resolution icon (16 to 48 px), simplified glyph
  with no background square, for the `NotifyIcon` in the system tray.
  Legible on both dark and light taskbar themes.
- `src/app-icon.svg`, `src/tray-icon.svg` — vector sources, edit these if
  the icon needs to change; regenerate the `.ico`/`.png` files from them.
- `png/app/`, `png/tray/` — individual PNG exports at each size, in case a
  raw PNG is needed instead of an `.ico` (e.g. for MSIX assets later).

## Design

Chat bubble + a two-arrow sync ring, on a blue-to-violet gradient rounded
square for the app icon; same glyph in flat solid color on a transparent
background (no card) for the tray icon, per Fluent guidance for
notification-area icons.

## Using them in Visual Studio / WinUI 3

- **App icon:** in `CodeChatSync.App`, set `app-icon.ico` as
  `<ApplicationIcon>` in the `.csproj`
  (`<ApplicationIcon>Assets\app-icon.ico</ApplicationIcon>`).
- **Tray icon:** load `tray-icon.ico` with `H.NotifyIcon.WinUI`'s
  `TaskbarIcon.IconSource`.
- **MSIX packaging (later):** the Windows App SDK also needs a dedicated
  asset set (`Square44x44Logo`, `Square150x150Logo`, `Square310x310Logo`,
  `Wide310x150Logo`, `StoreLogo`, `SplashScreen`) at multiple scales. Not
  generated yet — ask if/when packaging work starts, they can be derived
  from `src/app-icon.svg`.

## Regenerating from the SVG sources

```bash
# requires librsvg2-bin (rsvg-convert) and imagemagick (convert)
rsvg-convert -w 256 -h 256 src/app-icon.svg -o app_256.png
# ...repeat per size, then:
convert app_16.png app_20.png app_24.png app_32.png app_40.png app_48.png app_64.png app_128.png app_256.png app-icon.ico
```
