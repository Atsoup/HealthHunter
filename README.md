# HealthHunter

A Windows desktop utility for exploring process memory in offline, single-player games. HealthHunter combines numeric memory scanning with HUD bar detection and Windows OCR to help identify health, mana, stamina, and other changing values.

## Features

- Scan and refine integer, float, and double values in process memory.
- Correlate changing HUD bars with candidate memory values.
- Read on-screen numbers using the Windows OCR API.
- Inspect memory, search pointer paths, and save per-game profiles.
- Manage value groups, freeze modes, keyboard hotkeys, and controller shortcuts.
- Inspect diagnostics when a scan cannot identify a reliable value.
- Refuse attachment when known anti-cheat modules or services are detected.

## Requirements

- Windows 10 version 1809 or later, or Windows 11, x64.
- .NET 8 SDK or a compatible newer SDK to build.
- .NET 8 Desktop Runtime to run the framework-dependent build.

## Build

From the repository root, run:

```powershell
dotnet build src/HealthHunter/HealthHunter.csproj -c Release
dotnet publish src/HealthHunter/HealthHunter.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Start `publish/HealthHunter.exe`. See [the user guide](USER_GUIDE.md) for the guided workflow, scan modes, supported value types, and limitations. The guide includes the project's earlier version notes; prebuilt release downloads are not included in this source snapshot.

## How it works

The scanner reads accessible memory regions and narrows candidates as values change. HUD detection tracks changing colored bars; OCR provides numeric observations from the game window. Candidate validation attempts to distinguish underlying values from display copies. Pointer scanning and saved profiles help revisit values across sessions.

The interface is implemented with Windows Forms. Process access uses Windows APIs, including `ReadProcessMemory`, `WriteProcessMemory`, and memory-region queries. OCR uses `Windows.Media.Ocr`.

## Project structure

- `src/HealthHunter/Scanner.cs`: memory snapshots and candidate filtering.
- `src/HealthHunter/ProcessMemory.cs`: process access and memory operations.
- `src/HealthHunter/BarDetector.cs`, `HudDetector.cs`, `Ocr.cs`: on-screen observations.
- `src/HealthHunter/AutoFinder.cs`, `NumberFinder.cs`, `MovementFinder.cs`: candidate discovery and validation.
- `src/HealthHunter/PointerScanner.cs`, `Profiles.cs`: pointer paths and saved configuration.
- `src/HealthHunter/MainForm*.cs`: desktop interface and guided workflows.

## Limitations and intended use

Use only with offline, single-player software you are authorized to inspect. Back up saves before changing values. Memory editing can crash a process or corrupt game state. Anti-cheat detection is a best-effort check, not a guarantee that a target is safe to modify; do not use this tool in protected or multiplayer games.

Stylized HUDs, encrypted values, display copies, and unsupported memory layouts can prevent reliable discovery. The guided mode can stop without finding a value. See the user guide for details.

## Validation

This repository contains a source snapshot of version 6.1.0. Compilation is checked separately from behavior in a running game; a successful build does not establish compatibility with every game. No automated gameplay test results are claimed for this publication snapshot.
