import { context } from "./runtime";
import type { UiDefineOptions, UiOpenOptions } from "./api/types";

export type { UiDefineOptions, UiOpenOptions } from "./api/types";

/**
 * Declarative YAML panels. A document has one root `panel`; `components` are reusable panels.
 * Compile once, then open as many independent instances as needed.
 */
export const ui = {
    /** True on client processes, including player hosts; false on dedicated servers. */
    available(): boolean {
        return "ui" in context().host.api;
    },
    /** Compiles one YAML panel document (optionally with in-memory imports). */
    define(yaml: string, options: UiDefineOptions = {}): string {
        return context().host.api.ui.define(yaml, options);
    },
    /** Opens a compiled document; `get`/`set`/`onEvent` are scoped to that panel. */
    open(document: string, options: UiOpenOptions = {}): string {
        return context().host.api.ui.open(document, options);
    },
    /** Closes and destroys an open panel. */
    close(panel: string): void {
        context().host.api.ui.close(panel);
    },
    /** Refreshes bound values on the next host tick. */
    invalidate(panel: string): void {
        context().host.api.ui.invalidate(panel);
    }
};
