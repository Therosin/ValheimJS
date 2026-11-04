// Globals the host installs before a plugin bundle is evaluated.

/** Calls the callback once after `ms` milliseconds. Host-driven; returns a timer id. */
declare function setTimeout(callback: () => void, ms: number): number;

/** Calls the callback every `ms` milliseconds. Host-driven; returns a timer id. */
declare function setInterval(callback: () => void, ms: number): number;

declare function clearTimeout(id: number): void;
declare function clearInterval(id: number): void;

/** Context the host attaches around every invocation; used by the SDK trampolines. */
declare const __vjs_context: import("./index").PluginContext | null;
