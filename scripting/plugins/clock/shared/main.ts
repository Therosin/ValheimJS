/**
 * Class-form plugin: an instance, a timer, a dependency export and a teardown.
 * The host constructs the class and attaches `this.ctx`; methods are discovered as hooks.
 */
import { definePlugin, JsPlugin, log, type ServerTickPayload } from "../../../sdk/index";

class Clock extends JsPlugin {
    version = "1.0.0";

    private timer: ReturnType<typeof setInterval> | undefined;
    private ticks = 0;

    setup(): void {
        const hello = this.ctx.dep<{ greet(name: string): string }>("hello");
        log(hello.greet("clock"));

        this.timer = setInterval(() => {
            this.ticks += 1;
            if (this.ticks % 5 === 0) {
                log(`clock ${this.ticks}`);
            }
        }, 1000);
    }

    onServerTick(_world: ServerTickPayload): void {
        this.ctx.state.seen = Number(this.ctx.state.seen ?? 0) + 1;
    }

    teardown(): void {
        if (this.timer !== undefined) clearInterval(this.timer);
        log(`clock stopped after ${this.ticks}`);
    }
}

export default definePlugin(Clock);
