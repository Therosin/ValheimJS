import { describe, expect, test } from "bun:test";
import { position, type Vec3Like } from "../sdk/planning";

describe("planning helpers", () => {
    test("normalizes vector-like positions", () => {
        expect(position([1, 2, 3])).toEqual({ x: 1, y: 2, z: 3 });
        expect(position({ x: 4, y: 5, z: 6 })).toEqual({ x: 4, y: 5, z: 6 });
        const value: Vec3Like = [0, 1, 2];
        expect(position(value)).toEqual({ x: 0, y: 1, z: 2 });
    });
});
