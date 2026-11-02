// Globals the host installs before a plugin bundle is evaluated. Declared ambiently so
// plugin sources resolve them without importing anything.
declare function setTimeout(callback: () => void, ms: number): number;
declare function setInterval(callback: () => void, ms: number): number;
declare function clearTimeout(id: number): void;
declare function clearInterval(id: number): void;

/** Plugin currently executing; set by the host around every hook and timer. */
declare const __vjs_active: import("./index").PluginContext | null;
