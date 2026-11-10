# Headless Skua

**A fully headless AdventureQuest Worlds automation runtime built around the Skua engine and existing Skua scripts.**

Headless Skua is a separate .NET 10 host for Skua. It is intended to run without an interactive desktop, window, or visible game client while retaining compatibility with existing Skua scripts and CoreBots wherever the underlying runtime permits.

> **Status: Early runtime prototype.** The headless Chromium/Ruffle host and initial Skua script integration are in place. Full game login, network compatibility, and broad CoreBots validation remain in progress.

## Goals

- Run the AQW game client in a genuinely headless runtime.
- Preserve the existing Skua scripting API and CoreBots compatibility.
- Replace the Windows-only ActiveX/COM Flash host with a runtime-neutral implementation.
- Keep desktop UI out of the runtime while allowing plugins to provide headless-compatible behaviour.
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

Windows users can use the all-in-one build script. It initializes the VibeSkua submodule, restores and builds the solution, installs headless Chromium and Ruffle, copies the Skua SWF, and runs the prerequisite check.

```bat
Build.bat
```

To build and immediately start the headless client:

```bat
Build.bat run
```

To build, prepare the runtime, and execute an existing Skua C# script:

```bat
Build.bat run --script "C:\\path\\to\\YourScript.cs"
```

### Console login

Starting the runtime opens the terminal UI. If VibeSkua Manager accounts are available, choose one from the numbered list; otherwise choose manual login and enter a username and password (the password is not echoed). After authentication, select an online game server from the numbered server list. Scripts and ButlerV5 start only after the game world is connected.

The terminal supports the Midnight, Ember, and Neon themes with `--theme midnight`, `--theme ember`, or `--theme neon`. Use `--no-color` or set `NO_COLOR` to disable ANSI styling.

### Plugins and ButlerV5

Headless Skua initializes Skua's plugin manager and discovers plugin assemblies from the normal VibeSkua plugin directory (%APPDATA%\VibeSkua\plugins) as well as the host's local plugins folder. Plugins that rely on interactive desktop windows cannot show those windows in headless mode; their non-UI functionality can still run when their dependencies support the headless host.

ButlerV5 is bundled as a plugin and can be controlled from the command line:

\`\`\`bat
Build.bat -butler PlayerName
Build.bat -butler PlayerName 30m
Build.bat -butler PlayerName 02:00:00
Build.bat -butler stop
\`\`\`

An omitted duration means the follow runs indefinitely. Durations accept s, m, h, d, or HH:MM:SS. The stop command sends a local named-pipe request to the active Butler session. ButlerV5 continues to use its existing shared sync files, so other ButlerV5-enabled clients can see and summon this account as usual.

Requirements: Git and the .NET 10 SDK. The script uses PowerShell (or PowerShell 7 if installed) for runtime setup. Run `Build.bat` again after pulling updates to rebuild and refresh the runtime.

For manual setup, initialize the submodule with `git submodule update --init --recursive`, then use `dotnet build HeadlessSkua.sln --configuration Release` and the Playwright/Ruffle setup scripts under `runtime/ruffle-host`.

Use `dotnet run --project src/HeadlessSkua.Host -- doctor` to inspect local prerequisites without rebuilding.

## Current milestones

See [docs/roadmap.md](docs/roadmap.md) for the implementation plan and [docs/architecture.md](docs/architecture.md) for the boundaries between the host, game runtime, Skua services, and scripts.

## Compatibility principles

- Existing Skua scripts should not need to know whether the game is hosted by ActiveX or a headless runtime.
- Runtime-specific code belongs behind the Flash/runtime abstraction, not inside CoreBots.
- Desktop-only services must be replaced with headless implementations or excluded from the headless dependency graph.
- Credentials and session secrets must never be written to logs or committed to the repository.
