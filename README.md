# PC Dash

A small Windows app that shows your Claude usage and PC performance on your phone's browser over the local network.

## Run
`publish\PcDash.exe`: a window opens with a QR code. Scan it with the phone (same Wi-Fi/LAN) and the dashboard opens in the browser.
Closing the window keeps it running in the tray; right-click the tray icon → Exit.

- Swipe (or use the dots / arrow keys) between the **Claude** and **PC** slides.
- ⛶ fullscreen (also locks landscape on Android), ☀ keep screen on. Keep-awake turns on with your first tap.
- If the phone can't connect: pick another address in the dropdown, and allow PcDash in Windows Firewall on **Private** networks.
- **New key** invalidates the old QR code.
- **Start with Windows** (checkbox in the window, or tray menu): adds a per-user startup entry
  (`HKCU\...\CurrentVersion\Run\PcDash` → `PcDash.exe --tray`). It then starts silently in the tray; double-click the tray icon for the QR code.
- Gear button on the dashboard: burn-in protection (pixel shift, auto-switch slides, dim) and clock options.

## Tabs
1. **Claude**: session and weekly usage, resets, breakdown.
2. **PC**: CPU, GPU, RAM, network, disks, top processes.
3. **Deck**: buttons that make the PC open apps, websites (default browser or a chosen one), folders/files,
   send keyboard shortcuts or media keys, type text, run commands, or lock / sleep / turn off the monitor.
4. **Clock**: big clock with optional weather (Open-Meteo). Each phone picks clock only or with weather,
   24/12-hour, seconds and date in the gear menu.

Set up the deck and the weather location on the PC: window → **Set up deck & weather…** (or the tray menu).
That page only works on the PC itself; phones can press buttons but never see or change what they do.
Deck and weather settings: `%APPDATA%\PcDash\config.json`, app/site icons cached in `%APPDATA%\PcDash\icons`.
## Build
`dotnet publish -c Release -o publish` (needs .NET 11 SDK). The page (`wwwroot/`) is embedded in the exe.

## Notes
- Settings (access key, port, chosen IP): `%APPDATA%\PcDash\settings.json`.
- Claude data comes from `api.anthropic.com/api/oauth/usage` (undocumented, may change) using Claude Code's token in `~/.claude/.credentials.json`. The app only reads it; if it expires, run Claude Code once to refresh it.
- Port 8765 (falls back to 8766–8774 if busy).

## License
MIT, see [LICENSE](LICENSE). Bundles [NoSleep.js](https://github.com/richtr/NoSleep.js) (MIT).
