import { describe, expect, test } from "bun:test";
import { all, any, cooldown, delay, not, once, stable } from "../sdk/predicates";

/** Runs a predicate over a fixed sample sequence and returns the outputs. */
function sample(predicate: () => boolean, values: boolean[]): boolean[] {
    return values.map(value => {
        current = value;
        return predicate();
    });
}

let current = false;
const input = () => current;

describe("predicate combinators", () => {
    test("all and any combine inputs", () => {
        let a = false;
        let b = false;
        const both = all(() => a, () => b);
        const either = any(() => a, () => b);
        expect(both()).toBe(false);
        a = true;
        expect(both()).toBe(false);
        expect(either()).toBe(true);
        b = true;
        expect(both()).toBe(true);
        expect(all()()).toBe(true);
        expect(any()()).toBe(false);
    });

    test("not inverts", () => {
        let value = false;
        const inverted = not(() => value);
        expect(inverted()).toBe(true);
        value = true;
        expect(inverted()).toBe(false);
    });

    test("once is true only on the first true sample", () => {
        expect(sample(once(input), [true, true, false, true])).toEqual([true, false, false, false]);
    });

    test("stable waits for consecutive samples on rise and fall", () => {
        const stableInput = stable(input, 2, 3);
        expect(sample(stableInput, [true, true, false, false])).toEqual([false, true, true, true]);
        expect(sample(stableInput, [false, false, true])).toEqual([false, false, false]);

        const noisy = stable(input, 1, 3);
        expect(sample(noisy, [true, false, false])).toEqual([true, true, true]);
    });

    test("delay requires consecutive true samples", () => {
        expect(sample(delay(input, 2), [true, false, true, true])).toEqual([false, false, false, true]);
    });

    test("cooldown passes once per window", () => {
        expect(sample(cooldown(input, 3), [true, true, true, true, true])).toEqual([true, false, false, false, true]);
    });
});
