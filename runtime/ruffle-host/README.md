# Ruffle host prototype

This folder contains the browser-side runtime shell used by the standalone .NET host.

## Install runtime assets

Run the installer from PowerShell:

```powershell
.\Install-Runtime.ps1
```

The script downloads the pinned Ruffle Web self-hosted release and copies the compiled Skua SWF from the VibeSkua submodule into the local runtime folder. To use a different SWF, pass `-SwfPath`; to use another Ruffle release, pass `-Version`.

Expected layout:

```
runtime/ruffle-host/
├── bridge.js
├── index.html
├── Install-Runtime.ps1
├── ruffle/
│   ├── ruffle.js
│   └── supporting WebAssembly assets
└── game/
    └── skua.swf
```

The downloaded Ruffle distribution and game SWF are local runtime assets and must not be committed.

## Start the host

From the repository root:

```powershell
dotnet run --project src/HeadlessSkua.Host -- run
```

Or provide a SWF explicitly:

```powershell
dotnet run --project src/HeadlessSkua.Host -- run --swf "C:\path\to\skua.swf"
```

The .NET host starts a localhost-only static server, launches Chromium in headless mode, and waits for Ruffle to report that the SWF has loaded. Press Ctrl+C to stop it.

## Host integration contract

The .NET host exposes a Playwright binding named `headlessSkuaHostCall` before navigating to this page. It receives:

- `name`: the ExternalInterface function name called by the SWF.
- `args`: the normalized argument array.

The page registers the Skua Externalizer callback names as JavaScript functions. Calls are forwarded to .NET and return immediately to ActionScript. This is suitable for notifications, but synchronous callback return values still need explicit compatibility work.

To invoke an ActionScript callback registered through `ExternalInterface.addCallback`, call:

```javascript
window.__headlessSkua.callActionScript("isLoggedIn");
```

The implementation uses Ruffle's versioned `player.ruffle().callExternalInterface(name, ...args)` API.

## Important

- The host serves the player over localhost HTTP; do not open `index.html` directly with `file://`.
- The page enables script access because this trusted local SWF needs to communicate with the host.
- Networking is enabled for the AQW client. The local server binds only to loopback.
- This is a runtime prototype, not yet a complete Skua `IFlashUtil` implementation. It does not authenticate or execute Skua scripts.
