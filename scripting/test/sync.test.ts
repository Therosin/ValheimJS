import { describe, expect, test } from "bun:test";
import { decodeFrame, diffEntity, encodeFrame, quantise } from "../sdk/sync";

describe("sync frames", () => {
    test("round-trips partial entities", () => {
        const entities = [
            { id: 1, x: 1.5, y: 2.25, z: -3.75, rotY: 0.5 },
            { id: 2, flags: 3 },
            { id: 3, x: 0.01 }
        ];

        const decoded = decodeFrame(encodeFrame(entities));
        expect(decoded).toHaveLength(3);
        expect(decoded[0].id).toBe(1);
        expect(decoded[0].x).toBeCloseTo(1.5, 5);
        expect(decoded[0].rotY).toBeCloseTo(0.5, 5);
        expect(decoded[1]).toEqual({ id: 2, flags: 3 });
        expect(decoded[2].x).toBeCloseTo(0.01, 5);
        expect(decoded[2].y).toBeUndefined();
    });

    test("stays compact", () => {
        const entities = Array.from({ length: 100 }, (_, id) => ({ id, x: id * 0.5, y: 1, z: -id * 0.5 }));
        const frame = encodeFrame(entities);
        const json = JSON.stringify(entities);

        // 15 raw bytes per entity (u16 id + u8 mask + 3 * f32) -> 20 base64 chars.
        expect(frame.length / entities.length).toBeLessThan(21);
        // Roughly 1.6x smaller than the equivalent JSON on this shape; the gap widens with more
        // fields because JSON repeats their names per entity.
        expect(frame.length).toBeLessThan(json.length * 0.7);
    });

    test("diff skips sub-threshold movement", () => {
        const previous = { id: 7, x: 10, y: 2, z: 10, rotY: 0 };
        expect(diffEntity(previous, { id: 7, x: 10.004, y: 2, z: 10, rotY: 0 })).toBeNull();
        const moved = diffEntity(previous, { id: 7, x: 10.4, y: 2, z: 10, rotY: 0 });
        expect(moved).not.toBeNull();
        expect(moved!.x).toBe(10.4);
        expect(moved!.y).toBeUndefined();
    });

    test("quantise snaps", () => {
        expect(quantise(1.23456)).toBe(1.23);
    });
});
