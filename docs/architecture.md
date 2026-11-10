# Architecture

## Design objective

Run the existing Skua automation engine and scripts without requiring an interactive desktop client. The headless host owns process lifetime and infrastructure; it must not become a second implementation of every Skua game operation.

## Proposed component boundaries

```text
HeadlessSkua.Host
  ├── CLI / configuration / logging / shutdown
  ├── Session manager
  │     └── Game session (one isolated account)
  │           ├── Headless game runtime
  │           │     ├── Ruffle ActionScript player
  │           │     └── Browser/network container
  │           ├── Runtime bridge (IFlashUtil adapter)
  │           └── Skua service provider
  │                 ├── Existing Skua.Core
  │                 ├── Existing script API
  │                 └── Existing CoreBots scripts
  └── Optional control API (later)
```

## Reuse strategy

The existing VibeSkua repository already separates much of the automation engine from its desktop application:

- `Skua.Core` contains script management, game-facing services, and script helpers.
- `Skua.Core.Interfaces` contains contracts including `IFlashUtil`.
- `Skua.Core.Models` and `Skua.Core.Utils` provide shared types and utilities.
- `Skua.App.Avalonia` supplies the current desktop composition root and the Windows ActiveX Flash implementation.

The headless host should reference the reusable core projects, not the Avalonia application. It will provide its own dependency-injection composition root and replace infrastructure services that currently depend on UI types.

## Runtime adapter

The existing `IFlashUtil` contract is the compatibility seam for calls between C# and the SWF. The current implementation uses the Windows ActiveX/COM control and receives ActionScript callbacks through its FlashCall event.

The new implementation must preserve the observable behavior of this contract:

- Initialize the SWF and wait for the game client to become ready.
- Invoke named ActionScript functions with compatible argument and return-value conversion.
- Receive ActionScript-to-host callbacks.
- Preserve null, boolean, number, string, array, and object conversion.
- Serialize calls safely; the browser runtime is an event-driven environment.
- Cancel or fail pending calls cleanly when a session shuts down.

Do not change the public script API merely to accommodate a runtime implementation detail. If the current interface proves too synchronous or too broad, introduce a lower-level asynchronous runtime contract and keep an adapter for `IFlashUtil`.

## First runtime candidate

Use Ruffle's web player hosted in Playwright-controlled Chromium running in headless mode. This avoids a visible window and avoids embedding the legacy ActiveX control.

The browser process is an implementation detail of a game session. It is not a user-facing browser and must not require a desktop window manager.

The proof of concept must validate:

1. The actual Skua SWF loads in Ruffle.
2. The required AQW network and asset requests work.
3. The SWF can call a JavaScript bridge function.
4. .NET can invoke an ActionScript callback exposed by the SWF.
5. Calls preserve the XML/value semantics expected by `IFlashUtil`.
6. The game remains functional when Chromium is headless.

Ruffle's ExternalInterface/networking behavior must be configured deliberately. Do not assume that a generic browser page is equivalent to the ActiveX host.

## Session isolation

The first milestone is one session. Multi-account support comes after the runtime and lifecycle are stable.

Each future session must own its own browser context/page, runtime bridge, service provider, script manager, cancellation lifetime, logs, and account state. Do not share mutable game-facing singleton services between accounts.

## Desktop service replacement

The desktop composition root registers services for dialogs, file pickers, dispatchers, windows, hotkeys, and other UI facilities. The headless composition root must not register Avalonia types.

For each service dependency, choose one of:

- A headless implementation with equivalent useful behavior.
- A no-op implementation only when the operation is genuinely optional.
- An explicit unsupported-operation result when a script asks for an inherently interactive action.

Avoid silent no-op behavior for game actions or script operations.

## Security and operations

- Never log passwords, session tokens, or authentication payloads.
- Store credentials outside source control and restrict access to configuration files.
- Keep per-session logs separate and redact sensitive fields.
- Support cancellation and graceful shutdown at host and session levels.
- Do not expose a control API on a public interface by default.
