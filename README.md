# Headless Skua

**A fully headless AdventureQuest Worlds automation runtime built around the Skua engine and existing Skua scripts.**

Headless Skua is a separate .NET 10 host for Skua. It is intended to run without an interactive desktop, window, or visible game client while retaining compatibility with existing Skua scripts and CoreBots wherever the underlying runtime permits.

> **Status: Early architecture and runtime-prototype phase.** The repository is being established now. Game login, the Ruffle bridge, and script execution are not implemented yet.

## Goals

- Run the AQW game client in a genuinely headless runtime.
- Preserve the existing Skua scripting API and CoreBots compatibility.
- Replace the Windows-only ActiveX/COM Flash host with a runtime-neutral implementation.
- Keep the host independent of Avalonia and desktop UI assemblies.
- Start with one reliable account session, then add isolated multi-account sessions.
- Provide a CLI, structured logs, graceful shutdown, and automation-friendly control.

## Architecture direction

The first runtime candidate is **Ruffle Web inside headless Chromium**, controlled by a .NET host through Playwright. Ruffle provides the Flash/ActionScript execution layer; Chromium runs without a visible window; the .NET side owns Skua services, scripts, lifecycle, and the bridge.

This is a technical direction, not a claim that compatibility is already proven. The first milestone is a small end-to-end prototype that proves the exact SWF can load, network correctly, and exchange calls in both directions with .NET. If that fails, we will evaluate a native Ruffle embedding route before building the rest of the host around it.

## Repository layout

```
Headless-Skua/
├── docs/
│   ├── architecture.md
│   ├── roadmap.md
│   └── decisions/
│       └── 0001-headless-flash-runtime.md
├── src/
│   └── HeadlessSkua.Host/
│       ├── HeadlessSkua.Host.csproj
│       └── Program.cs
├── Directory.Build.props
└── HeadlessSkua.sln
```

## Requirements

- .NET 10 SDK
- Windows is the initial development target; cross-platform support is a later goal.
- A legally obtained AQW client SWF and the required game assets/configuration.

## Build

Clone with the pinned VibeSkua source submodule:

```powershell
git clone --recurse-submodules https://github.com/wtffidy/Headless-Skua.git
cd Headless-Skua
dotnet build HeadlessSkua.sln
```

If the repository was cloned without submodules, initialize them with `git submodule update --init --recursive`.

Install the headless browser and Ruffle assets:

```powershell
pwsh src/HeadlessSkua.Host/bin/Debug/net10.0/playwright.ps1 install chromium
pwsh runtime/ruffle-host/Install-Runtime.ps1
```

Start the runtime:

```powershell
dotnet run --project src/HeadlessSkua.Host -- run
```

Use `dotnet run --project src/HeadlessSkua.Host -- doctor` to inspect local prerequisites.

## Current milestones

See [docs/roadmap.md](docs/roadmap.md) for the implementation plan and [docs/architecture.md](docs/architecture.md) for the boundaries between the host, game runtime, Skua services, and scripts.

## Compatibility principles

- Existing Skua scripts should not need to know whether the game is hosted by ActiveX or a headless runtime.
- Runtime-specific code belongs behind the Flash/runtime abstraction, not inside CoreBots.
- Desktop-only services must be replaced with headless implementations or excluded from the headless dependency graph.
- Credentials and session secrets must never be written to logs or committed to the repository.
