# Headless Skua

**A true headless AdventureQuest Worlds automation runtime built around the Skua engine and existing Skua scripts.**

Headless Skua is a standalone .NET 10 host for Skua. Instead of running heavy browser emulators, it connects directly to AQW's SmartFoxServer (SFS) via native TCP sockets using a pure protocol engine. The .NET host manages the game state, network synchronization, Skua services, autonomous script execution, plugins, and remote control.

> **Status: Active Pure Protocol Runtime.** Ultra-lightweight (~50 MB RAM, near-0% CPU). Supports standard VibeSkua scripts, map transitions, combat rotations, quest chains, and drops.

---

## Highlights

- **Zero Flash / Zero Ruffle / Zero Chromium**: Connects directly via raw TCP sockets (`sock*.aq.com:5593`) using AQW's native XML/JSON protocol.
- **Ultra-lightweight**: Drops memory consumption from ~450 MB down to **~50 MB RAM** with virtually no CPU overhead.
- **Standalone Single-File Executable**: Compiles into a self-contained binary with no external browser, Playwright, Node.js, or runtime downloads required.
- **Autonomous Script Execution**: Full compatibility with VibeSkua scripts (`Dailies`, `CoreBots`, combat rotations, map traversal, quest acceptance/turn-in, drops).
- **Remote Web Dashboard**: Integrated lightweight web panel on port `8788` for starting/stopping scripts and live status/diagnostics.

---

## Build and run

On Windows, run:

```bat
Build.bat
```

The build script restores and builds the solution, then publishes the .NET host as a self-contained, single-file Windows x64 executable. No external browser drivers or web runtimes are downloaded. 

After a successful build, the output structure is clean and minimal:

```
Build\
├── HeadlessSkua.exe
├── plugins\
│   └── ButlerV5.dll
└── panel\
    └── index.html
```

The script can launch the published executable after a successful build, or you can run it directly:

```bat
Build\HeadlessSkua.exe run
```

### Command-line options

Run automatically with a specific saved account and server:

```bat
Build\HeadlessSkua.exe run --account 3 --server Twilly
Build\HeadlessSkua.exe run --account "MyAccount" --server Artix
```

Run an existing Skua C# script on startup:

```bat
Build\HeadlessSkua.exe run --script "Dailies/0AllDailies.cs"
Build\HeadlessSkua.exe run --script "C:\path\to\YourScript.cs"
```

Combine options for completely unattended/headless startup:

```bat
Build\HeadlessSkua.exe run --account 1 --server Twilly --script "Dailies/0AllDailies.cs" --quiet
```

Hide verbose debug packets while keeping Headless Skua prompts, status, and errors visible:

```bat
Build\HeadlessSkua.exe run --quiet
```

Run ButlerV5:

```bat
Build\HeadlessSkua.exe -butler PlayerName
Build\HeadlessSkua.exe -butler PlayerName 30m
Build\HeadlessSkua.exe -butler PlayerName 02:00:00
Build\HeadlessSkua.exe -butler stop
```

An omitted Butler duration means follow indefinitely until stopped. Duration formats accept seconds, minutes, hours, days, or `HH:MM:SS`.

---

## Console and login

The terminal supports Midnight, Ember, and Neon themes:

```bat
Build\HeadlessSkua.exe run --theme neon
Build\HeadlessSkua.exe run --theme ember
Build\HeadlessSkua.exe run --theme midnight
Build\HeadlessSkua.exe run --no-color
```

If VibeSkua Manager accounts are available, choose one from the numbered list (or pass `--account <name|index>`). Otherwise, choose manual login (`M`) and enter credentials. After authentication, select an online game server (or pass `--server <name|index>`). 

---

## AQW Socket Networking

Because Headless Skua is a pure protocol client running in .NET, it connects directly to Artix Entertainment's SmartFoxServer TCP cluster over port `5593`.

- **Direct TCP Sockets**: No loopback proxies, WebSockets, or Websockify required.
- **Protocol Codec**: Full native support for null-byte-delimited XML, raw text commands, and zlib-compressed SFS extension payloads (`%xt%` and JSON commands).
- **Game State Synchronization**: Client-side state tracking handles coordinate navigation, cell jumping (`moveToCell`), monster HP reduction, mana regeneration, drops, and inventory caches natively.

---

## Remote panel

Headless Skua serves an embedded browser control panel on port **8788**. It is enabled by default and listens on localhost only:

```bat
Build\HeadlessSkua.exe run
```

Open [http://127.0.0.1:8788](http://127.0.0.1:8788) on the host computer.

To make the panel available to other devices on your home network or VPN, bind it to all interfaces:

```bat
Build\HeadlessSkua.exe run --remote
```

The panel prints the host's available IPv4 addresses at startup. Change the port with `--panel-port 8790`, or disable the panel entirely with `--no-panel`.

The panel exposes:
- Real-time character readiness, map location, cell/pad coordinates, HP/MP, and connection status.
- A script picker populated from the host's `%APPDATA%\VibeSkua\Scripts` folder, plus manual script path entry.
- Start and stop controls for existing Skua C# scripts with detailed compilation diagnostics.
- Start and stop for ButlerV5.
- Live script, debug, and packet logs.

**Security Notice:** `--remote` binds to `0.0.0.0` without authentication. Use it only on a trusted LAN or private VPN, and never expose the port directly to the public internet.

---

## Plugins

Headless Skua initializes Skua's plugin manager and discovers plugins from the standard VibeSkua plugin directory (`%APPDATA%\VibeSkua\plugins`) and the local `plugins` folder. Plugins requiring Avalonia/WPF desktop UI will not render windows in headless mode, but background logic and service hooks operate normally.

ButlerV5 is bundled in the published `Build\plugins` folder.

---

## Requirements

- Git
- .NET 10 SDK
- Windows x64 for the published executable

The published executable in `Build\` is completely self-contained and does not require a pre-installed .NET runtime on target machines. `Build.bat` requires the .NET 10 SDK to compile and publish.

For manual development builds:
```bash
git submodule update --init --recursive
dotnet build HeadlessSkua.sln --configuration Release
```
