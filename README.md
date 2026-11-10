# Headless Skua

**A headless AdventureQuest Worlds automation runtime built around the Skua engine and existing Skua scripts.**

Headless Skua is a separate .NET 10 host for Skua. It runs the AQW client in headless Chromium through Ruffle, with the .NET host providing Skua services, scripts, plugins, and command-line control.

> **Status: Early runtime prototype.** Full game login, network compatibility, and broad CoreBots validation remain in progress.

## Build and run

On Windows, run:

```bat
Build.bat
```

The build script restores and builds the solution, installs Chromium and Ruffle, then publishes a self-contained Windows x64 application to:

```
Build\
├── HeadlessSkua.exe
├── HeadlessSkua.dll
├── plugins\
│   └── ButlerV5.dll
└── runtime\
    └── ruffle-host\
        ├── ruffle\
        └── game\
            └── skua.swf
```

The script launches the published executable after a successful build. You can also run it directly from the output folder:

```bat
Build\HeadlessSkua.exe run
```

Run an existing Skua C# script:

```bat
Build\HeadlessSkua.exe run --script "C:\path\to\YourScript.cs"
```

Run ButlerV5:

```bat
Build\HeadlessSkua.exe -butler PlayerName
Build\HeadlessSkua.exe -butler PlayerName 30m
Build\HeadlessSkua.exe -butler PlayerName 02:00:00
Build\HeadlessSkua.exe -butler stop
```

An omitted Butler duration means follow indefinitely until stopped. Duration formats accept seconds, minutes, hours, days, or `HH:MM:SS`.

## Console and login

The terminal supports Midnight, Ember, and Neon themes:

```bat
Build\HeadlessSkua.exe run --theme neon
Build\HeadlessSkua.exe run --theme ember
Build\HeadlessSkua.exe run --theme midnight
Build\HeadlessSkua.exe run --no-color
```

If VibeSkua Manager accounts are available, choose one from the numbered list. Otherwise, choose manual login and enter a username and password. After authentication, select an online game server. Scripts and ButlerV5 start after the game world connects.

## Plugins

Headless Skua initializes Skua's plugin manager and discovers plugins from the normal VibeSkua plugin directory (%APPDATA%\VibeSkua\plugins) and the local `plugins` folder. Plugins that require desktop UI cannot display those windows in headless mode; compatibility depends on their dependencies.

ButlerV5 is bundled in the published `Build\plugins` folder.

## Requirements

- Git
- .NET 10 SDK
- Windows x64 for the current published build

The published executable is self-contained and does not require a separately installed .NET runtime. Build.bat still requires the .NET 10 SDK to compile and publish.

For manual development builds, initialize the submodule with `git submodule update --init --recursive`, then run `dotnet build HeadlessSkua.sln --configuration Release`.
