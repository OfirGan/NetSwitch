# NetSwitch

A single-screen Windows Forms utility for reading, saving, editing, and applying IPv4 network adapter profiles. It opens the main window on launch by default, or starts with only the tray icon when **Start minimized to tray** is enabled. It uses a custom network-node icon. Profiles and app settings are stored locally in `%LOCALAPPDATA%\NetSwitch\profiles.json` and `%LOCALAPPDATA%\NetSwitch\settings.json`; there is no account or database.

Current release: `2026.11.09-001`.

## Requirements

- Windows 10 or 11
- .NET 10 SDK (the SDK includes the Windows Desktop runtime needed to run the app)

The app requests administrator permission at startup because Windows requires elevation to change adapter settings. Applying a profile may interrupt the selected adapter's connection.

Use **Start with Windows** in the main window to create an elevated Task Scheduler logon task. Use **Start minimized to tray** to choose whether launch opens the main window or only the notification-area icon. The display choice takes effect on the next launch.

## Build and run

1. Install the .NET 10 SDK for Windows from [the .NET download page](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Open PowerShell in this project folder.
3. Build the app:

   ```powershell
   dotnet build .\NetSwitch.csproj
   ```

4. Run it:

   ```powershell
   dotnet run --project .\NetSwitch.csproj
   ```

5. Accept the Windows administrator prompt. NetSwitch starts as an icon in the notification area near the clock. If you prefer to launch the built app directly, open `bin\Debug\net10.0-windows\NetSwitch.exe`.

## Try it safely

1. Launching NetSwitch opens the main window unless **Start minimized to tray** is enabled. Launching it again brings the existing main window forward. Left-click the tray icon to open or hide the window; right-click it for the tray menu.
2. Click **Tray adapters...** in the main window, or choose **Choose visible adapters...** from the right-click tray menu. Unchecked adapters are hidden from tray profile menus, but remain available in the main editor.
3. Select an adapter and click **Read current** to inspect its current IPv4 and DNS settings.
4. For static addressing, enter the primary IPv4 address. Use **Optional IPs...** to add secondary addresses with an IP and subnet mask for each row. They use the primary address's gateway. Enter the connection-specific DNS suffix in the DNS section if needed.
5. Enter a profile name and click **Save profile**. Double-click a saved profile name to edit its name, then click **Save profile** to rename it. Create a second profile to check that multiple profiles persist.
6. To apply from the tray, right-click the icon, open a profile's submenu, and choose its target adapter. Confirm the warning dialog.
7. To test without changing the network, stop after saving and check `%LOCALAPPDATA%\NetSwitch\profiles.json` to confirm the profiles were saved. You can also load a profile in the main window to review or edit its values.
8. To close the app, choose **Exit** from the tray menu. Closing the main window hides it back to the tray.

Use **Refresh** after connecting or disconnecting an adapter. Profiles are not locked to their source adapter; the adapter selected from the tray submenu is the target.