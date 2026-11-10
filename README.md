# Headless Skua

**A headless AdventureQuest Worlds automation runtime built around the Skua engine and existing Skua scripts.**

Headless Skua is a separate .NET 10 host for Skua. It runs the AQW client in headless Chromium through Ruffle, with the .NET host providing Skua services, scripts, plugins, and command-line control.

> **Status: Early runtime prototype.** Full game login, network compatibility, and broad CoreBots validation remain in progress.

## Build and run

On Windows, run:

```bat
Build.bat
```

The build script restores and builds the solution, installs Chromium and Ruffle, then publishes the .NET host as one self-contained Windows x64 executable. Playwright's Node driver, the dashboard files, Ruffle, and the AQW client remain alongside the executable as runtime assets:

```
Build\
├── HeadlessSkua.exe
├── plugins\
│   └── ButlerV5.dll
├── panel\
│   └── index.html
├── .playwright\
│   ├── node\
│   │   └── win32_x64\
│   │       └── node.exe
│   └── package\
│       └── cli.js
└── runtime\
    └── ruffle-host\
        ├── index.html
        ├── bridge.js
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

## Remote panel

Headless Skua serves a browser control panel on port **8788**. It is enabled by default and listens on localhost only:

```bat
Build\HeadlessSkua.exe run
```

Open [http://127.0.0.1:8788](http://127.0.0.1:8788) on the host computer.

To make the panel available to other devices on your home network or VPN, bind it to all interfaces:

```bat
Build\HeadlessSkua.exe run --remote
```

Or pass the option through the build script:

```bat
Build.bat run --remote
```

The panel prints the host's available IPv4 addresses at startup. Open `http://HOST-IP:8788` from another device. This includes the machine's LAN address and VPN address when the VPN adapter is active. Change the port with `--panel-port 8790`, or disable the panel with `--no-panel`.

The first panel version exposes:
- Runtime connection and selected server status.
- Start and stop for existing Skua C# scripts.
- Start and stop for ButlerV5, including an optional duration.
- Recent Script, Debug, and Flash log output.

**Important:** `--remote` binds to `0.0.0.0` and the panel/API has no login protection in this initial version. Use it only on a trusted LAN or private VPN, keep the port closed to the public internet, and do not configure router port forwarding. Anyone who can reach the panel can issue its available controls. The game login and server selection remain in the host terminal for now. Script paths entered in the panel refer to files on the Headless Skua computer, not the phone or browser device.

Use `--no-panel` to turn off the web server entirely.

## Plugins

Headless Skua initializes Skua's plugin manager and discovers plugins from the normal VibeSkua plugin directory (%APPDATA%\VibeSkua\plugins) and the local `plugins` folder. Plugins that require desktop UI cannot display those windows in headless mode; compatibility depends on their dependencies.

ButlerV5 is bundled in the published `Build\plugins` folder.

## Requirements

- Git
- .NET 10 SDK
- Windows x64 for the current published build

The published executable is self-contained and does not require a separately installed .NET runtime. Build.bat still requires the .NET 10 SDK to compile and publish.

For manual development builds, initialize the submodule with `git submodule update --init --recursive`, then run `dotnet build HeadlessSkua.sln --configuration Release`.

### Visual Studio

Run `Build.bat` once before launching from Visual Studio. It installs the Playwright Chromium browser, downloads the Ruffle web runtime, and copies those runtime assets into `src\HeadlessSkua.Host\bin\Release\net10.0`. Subsequent Visual Studio builds copy the installed Ruffle files into the output automatically. Select `HeadlessSkua.Host` as the startup project and press F5.
