# NightDim Windows installer

Build on Windows with .NET 10 SDK and Inno Setup 6.7 or newer. Download Inno Setup from the official site and use its `ISCC.exe`. [Inno Setup downloads](https://jrsoftware.org/isdl.php)

From the repository root, publish the application and build the installer. Match the version in `src/NightDim/NightDim.csproj` when preparing a new release.

```powershell
dotnet publish src/NightDim/NightDim.csproj -c Release -r win-x64 --self-contained true -o artifacts/NightDim-win-x64
.\installer\Build-Installer.ps1 -Version 1.1.1
```

For a compiler in a custom location, pass `-CompilerPath 'C:\Path\To\ISCC.exe'`. The script writes `artifacts/NightDim-Setup-1.1.1-win-x64.exe` and prints its SHA-256.

Setup installs for the current user to `%LOCALAPPDATA%\Programs\NightDim` by default. The setup wizard offers English and Russian and creates a Start menu shortcut. Enable autostart in NightDim's settings. Exit NightDim through its tray menu before upgrading or removing it. Uninstall removes the installed files and the autostart command for that installation. Preferences are kept in `%LOCALAPPDATA%\NightDim`.
