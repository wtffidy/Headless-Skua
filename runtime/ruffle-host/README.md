# Ruffle host prototype

This folder contains the first browser-side runtime shell. It is intentionally independent from the Avalonia application.

## Local layout

Place a self-hosted Ruffle Web distribution in `ruffle/` so that `ruffle/ruffle.js` and its WebAssembly assets are available. Place the Skua-built SWF at `game/skua.swf`.

Expected layout:

```
runtime/ruffle-host/
├── bridge.js
├── index.html
├── ruffle/
│   ├── ruffle.js
│   └── *.wasm / supporting assets
└── game/
    └── skua.swf
```

Do not commit the downloaded Ruffle distribution or game SWF. They are runtime assets and are ignored by the repository.

## Host integration contract

The .NET host must expose a Playwright binding named `headlessSkuaHostCall` before navigating to this page. It receives:

- `name`: the ExternalInterface function name called by the SWF.
- `args`: the normalized argument array.

The page registers the current Skua Externalizer callback names as JavaScript functions. Calls are forwarded asynchronously to .NET and return immediately to ActionScript. This is appropriate for the current host notification callbacks; any callback that requires a synchronous return value must be explicitly identified and handled before the compatibility adapter is considered complete.

To invoke an ActionScript callback registered through `ExternalInterface.addCallback`, call:

```javascript
window.__headlessSkua.callActionScript("isLoggedIn");
```

The implementation uses Ruffle's versioned `player.ruffle().callExternalInterface(name, ...args)` API.

## Important

- Serve this folder over localhost HTTP rather than opening `index.html` with a `file://` URL. WebAssembly and game asset loading need correct HTTP response handling.
- The page enables Ruffle `allowScriptAccess` because this trusted local SWF needs to communicate with the host.
- Networking is enabled for the AQW client. Do not expose the local host server to the public network.
- This page is only a runtime bootstrap. It does not yet authenticate, connect, execute scripts, or implement the full `IFlashUtil` adapter.
