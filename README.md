# SpotiBai Taskbar

A small Windows taskbar overlay that displays the current Spotify track and provides playback controls. It supports the Spotify desktop app and Spotify Web in supported browsers.

## Install

Download and run `SpotiBai-Taskbar-Setup.exe` from the project's GitHub Releases. The installer:

- Installs for the current Windows user (no administrator access required)
- Creates Desktop and Start Menu shortcuts
- Starts the app with Windows
- Includes the .NET runtime

The installer currently targets Windows x64.

## Build the installer

Requirements: Windows and the .NET 8 SDK.

Run:

```powershell
.\build-setup.cmd
```

The distributable installer is created at:

```text
dist\SpotiBai-Taskbar-Setup.exe
```

The `dist` folder and generated publish files are ignored by Git. Upload the installer as a GitHub Release asset rather than committing it to the repository.

## Spotify Web note

Spotify Web detection relies on Windows exposing the browser's media session and the browser title identifying the playing Spotify track. Behavior can vary by browser.
