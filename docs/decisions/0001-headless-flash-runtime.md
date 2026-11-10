# ADR 0001: Headless Flash runtime

- **Status:** Proposed; proof of concept required
- **Date:** 2026-11-10

## Context

VibeSkua currently hosts its ActionScript client through the Windows ActiveX Shockwave Flash control. Its implementation depends on COM interop, WinForms hosting, and a desktop application startup path. That is incompatible with the goal of running the game without a visible window or interactive desktop.

The Skua core communicates with the SWF through `IFlashUtil`, which is a useful compatibility boundary. Existing scripts depend on the higher-level Skua API rather than directly owning the Flash control.

## Decision

Prototype Ruffle Web hosted inside Playwright-controlled Chromium in headless mode.

The .NET host remains responsible for Skua services, scripts, session lifecycle, and a bridge that adapts the browser player to the existing `IFlashUtil` contract.

This is a candidate architecture, not a commitment to ship it regardless of results.

## Why this candidate

- Ruffle executes SWF content without relying on the retired browser Flash plugin or Windows ActiveX control.
- Ruffle Web is distributed as a WebAssembly player with a JavaScript API.
- Playwright for .NET can launch Chromium in headless mode and control the page from the .NET process.
- The approach keeps the Flash runtime isolated from the Skua script engine and allows the bridge to be tested independently.

## Risks and unknowns

- AQW may rely on Flash/ActionScript behavior that Ruffle does not yet implement completely.
- The exact Skua SWF may expect host behavior supplied by the existing ActiveX container.
- ExternalInterface callbacks and return values must be mapped to the existing XML-based bridge semantics.
- Network policy, cross-origin behavior, cookies, TLS, local assets, and browser security settings may affect login or game connectivity.
- A browser-backed runtime has more process and memory overhead than a native embedded runtime.
- Synchronous calls in `IFlashUtil` need careful coordination with an asynchronous browser event loop.

## Validation gate

Do not build the full host around this assumption until a prototype loads the real SWF, performs network requests, and proves both directions of the bridge in headless Chromium.

If the proof of concept fails, evaluate a native Ruffle embedding approach or another maintained ActionScript runtime before changing the Skua core API.
