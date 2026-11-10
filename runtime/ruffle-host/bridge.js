(() => {
  "use strict";

  const callbackNames = [
    "debug",
    "requestLoadGame",
    "pre-load",
    "loaded",
    "pext",
    "connectionLost",
    "userIntentionalAction",
    "openWebsite",
    "packet",
    "packetFromServer"
  ];

  let player = null;
  let hostCallHandler = null;
  let status = "not-started";
  let statusDetail = null;

  function normalizeArguments(rawArguments) {
    // Skua's Externalizer.call(name, ...rest) currently passes its rest array
    // as one ActionScript ExternalInterface argument.
    if (rawArguments.length === 1 && Array.isArray(rawArguments[0])) {
      return rawArguments[0];
    }

    return rawArguments;
  }

  function dispatchToHost(name, rawArguments) {
    const args = normalizeArguments(rawArguments);

    if (typeof hostCallHandler === "function") {
      Promise.resolve(hostCallHandler(name, args)).catch(error => {
        console.error("[HeadlessSkua] Host callback failed:", name, error);
      });
      return undefined;
    }

    if (typeof window.headlessSkuaHostCall === "function") {
      Promise.resolve(window.headlessSkuaHostCall(name, args)).catch(error => {
        console.error("[HeadlessSkua] Host callback failed:", name, error);
      });
      return undefined;
    }

    console.debug("[HeadlessSkua] Host callback received before .NET binding:", name, args);
    return undefined;
  }

  // ExternalInterface.call resolves the supplied function name on the page.
  // These callbacks intentionally return immediately; the host event channel
  // is asynchronous and must not block the ActionScript execution thread.
  for (const name of callbackNames) {
    Object.defineProperty(window, name, {
      configurable: false,
      enumerable: false,
      writable: false,
      value: (...args) => dispatchToHost(name, args)
    });
  }

  window.__headlessSkua = Object.freeze({
    async start(options) {
      if (!window.RufflePlayer) {
        throw new Error("Ruffle did not load. Expected ./ruffle/ruffle.js.");
      }

      const source = options?.swfUrl;
      if (!source) {
        throw new Error("No SWF URL was provided.");
      }

      const container = options?.container;
      if (!(container instanceof HTMLElement)) {
        throw new Error("A valid player container is required.");
      }

      const ruffle = window.RufflePlayer.newest();
      player = ruffle.createPlayer();
      player.id = "headless-skua-player";
      container.appendChild(player);

      setStatus("loading", source);
      await player.ruffle().load({
        url: source,
        allowScriptAccess: true
      });

      setStatus("loaded", source);
      return true;
    },

    setHostCallHandler(handler) {
      hostCallHandler = typeof handler === "function" ? handler : null;
    },

    callActionScript(name, args = []) {
      if (!player) {
        throw new Error("The Ruffle player has not been created.");
      }

      if (typeof name !== "string" || name.length === 0) {
        throw new TypeError("An ActionScript callback name is required.");
      }

      if (!Array.isArray(args)) {
        throw new TypeError("ActionScript callback arguments must be an array.");
      }

      return player.ruffle().callExternalInterface(name, ...args);
    },

    getStatus() {
      return { status, detail: statusDetail };
    },

    setStatus(nextStatus, detail = null) {
      status = nextStatus;
      statusDetail = detail;
      console.info("[HeadlessSkua] Runtime status:", status, detail ?? "");
    }
  });
})();
