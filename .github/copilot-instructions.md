# Repository Guidance

This repository contains a .NET 10 Windows Forms application targeting `net10.0-windows`.

- Follow conventions already present in the repository and keep changes scoped to the requested work.
- Before editing, inspect the relevant implementation and tests; after editing, run the narrowest applicable validation.
- Do not add dependencies, generated files, or project scaffolding unless the task calls for them.
- The app's tray startup, UI, profile model, JSON persistence, adapter discovery, validation, and `netsh` integration live in `Program.cs`.
- Profiles and tray adapter visibility settings are stored at `%LOCALAPPDATA%\NetSwitch\profiles.json` and `%LOCALAPPDATA%\NetSwitch\settings.json`.
- Build with `dotnet build NetSwitch.csproj`; run with `dotnet run --project NetSwitch.csproj` on Windows with the .NET 10 SDK installed.
- Network changes require administrator privileges. The application manifest requests elevation.