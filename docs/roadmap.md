# Roadmap

## Phase 0 — Repository and architecture

- [x] Establish standalone repository and .NET 10 solution.
- [x] Document component boundaries and the current runtime hypothesis.
- [ ] Add build and CI workflows.
- [ ] Confirm licensing and redistribution requirements for all runtime assets.

## Phase 1 — Prove the headless game runtime

- [ ] Add a small Ruffle + headless Chromium proof of concept.
- [ ] Load the exact SWF used by Skua.
- [ ] Confirm SWF networking, asset loading, and startup behavior.
- [ ] Prove ActionScript-to-JavaScript-to-.NET callbacks.
- [ ] Prove .NET-to-ActionScript calls and compatible value conversion.
- [ ] Capture useful browser, console, and Ruffle diagnostics.
- [ ] Decide whether the browser-backed runtime is viable before expanding the host.

**Exit condition:** the real client can start headlessly and the bridge can perform representative Skua calls in both directions.

## Phase 2 — Runtime adapter

- [ ] Implement a runtime-neutral bridge contract.
- [ ] Implement the Ruffle/Chromium runtime.
- [ ] Implement an `IFlashUtil` compatibility adapter.
- [ ] Add lifecycle, timeout, cancellation, and serialization tests.
- [ ] Verify game state reads and game actions against the real SWF.

## Phase 3 — Reuse Skua core and scripts

- [ ] Reference the minimum reusable VibeSkua projects.
- [ ] Build a headless dependency-injection composition root.
- [ ] Replace UI-bound services with headless implementations.
- [ ] Register the existing script API and script manager.
- [ ] Load and execute a simple existing Skua script.
- [ ] Run representative CoreBots scripts without editing their source.
- [ ] Identify and isolate any unavoidable compatibility gaps.

## Phase 4 — Reliable single-account host

- [ ] Add account configuration without committing secrets.
- [ ] Add session startup, readiness checks, stop, and restart.
- [ ] Add reconnect and disconnect handling.
- [ ] Add structured per-session logging and diagnostics.
- [ ] Add a non-interactive CLI workflow suitable for scheduled jobs.

## Phase 5 — Multiple sessions and automation

- [ ] Introduce isolated per-account service scopes.
- [ ] Run multiple independent game sessions.
- [ ] Add resource limits and session health monitoring.
- [ ] Add an optional local control API.
- [ ] Add service-mode and container deployment support where practical.

## Non-goals

- Rebuilding the Avalonia desktop UI.
- Rewriting CoreBots or the Skua scripting API without a demonstrated compatibility need.
- Claiming full headless support until the real game client and script bridge pass integration tests.
