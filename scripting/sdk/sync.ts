/**
 * Compact binary frames for state sync (positions, rotations, small flags).
 *
 * Why not JSON: a per-entity transform in JSON costs ~60-80 bytes and a parse per entity. The
 * frame below is ~13 bytes per entity and is decoded with a single DataView walk. It is meant for
 * things the server simulates but the game does not network itself — plugin-managed NPCs,
 * markers, patrol points.
 *
 * Wire form is base64 so it can travel through the string-based transport unchanged.
 *
 *   [u8 version][u16 count] then per entity:
 *   [u16 id][u8 mask][f32 x?][f32 y?][f32 z?][f32 rotY?][u8 flags?]
 *
 * mask bits: 1 = x, 2 = y, 4 = z, 8 = rotY, 16 = flags.
 */

export const FRAME_VERSION = 1;

export interface SyncEntity {
    id: number;
    x?: number;
    y?: number;
    z?: number;
    rotY?: number;
    flags?: number;
}

const MASK_X = 1;
const MASK_Y = 2;
const MASK_Z = 4;
const MASK_ROT = 8;
const MASK_FLAGS = 16;

const B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

export function encodeFrame(entities: SyncEntity[]): string {
    let size = 3;
    for (const entity of entities) {
        size += 3;
        if (entity.x !== undefined) size += 4;
        if (entity.y !== undefined) size += 4;
        if (entity.z !== undefined) size += 4;
        if (entity.rotY !== undefined) size += 4;
        if (entity.flags !== undefined) size += 1;
    }

    const bytes = new Uint8Array(size);
    const view = new DataView(bytes.buffer);
    view.setUint8(0, FRAME_VERSION);
    view.setUint16(1, entities.length);

    let offset = 3;
    for (const entity of entities) {
        view.setUint16(offset, entity.id & 0xffff);
        offset += 2;

        let mask = 0;
        if (entity.x !== undefined) mask |= MASK_X;
        if (entity.y !== undefined) mask |= MASK_Y;
        if (entity.z !== undefined) mask |= MASK_Z;
        if (entity.rotY !== undefined) mask |= MASK_ROT;
        if (entity.flags !== undefined) mask |= MASK_FLAGS;
        view.setUint8(offset, mask);
        offset += 1;

        if (entity.x !== undefined) { view.setFloat32(offset, entity.x); offset += 4; }
        if (entity.y !== undefined) { view.setFloat32(offset, entity.y); offset += 4; }
        if (entity.z !== undefined) { view.setFloat32(offset, entity.z); offset += 4; }
        if (entity.rotY !== undefined) { view.setFloat32(offset, entity.rotY); offset += 4; }
        if (entity.flags !== undefined) { view.setUint8(offset, entity.flags & 0xff); offset += 1; }
    }

    return toBase64(bytes);
}

export function decodeFrame(frame: string): SyncEntity[] {
    const bytes = fromBase64(frame);
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (view.byteLength < 3 || view.getUint8(0) !== FRAME_VERSION) {
        throw new Error("unsupported sync frame");
    }

    const count = view.getUint16(1);
    const entities: SyncEntity[] = [];
    let offset = 3;

    for (let index = 0; index < count; index++) {
        const id = view.getUint16(offset);
        const mask = view.getUint8(offset + 2);
        offset += 3;

        const entity: SyncEntity = { id };
        if (mask & MASK_X) { entity.x = view.getFloat32(offset); offset += 4; }
        if (mask & MASK_Y) { entity.y = view.getFloat32(offset); offset += 4; }
        if (mask & MASK_Z) { entity.z = view.getFloat32(offset); offset += 4; }
        if (mask & MASK_ROT) { entity.rotY = view.getFloat32(offset); offset += 4; }
        if (mask & MASK_FLAGS) { entity.flags = view.getUint8(offset); offset += 1; }
        entities.push(entity);
    }

    return entities;
}

/** Quantise a float to the frame's precision so unchanged values can be skipped. */
export function quantise(value: number, step = 0.01): number {
    return Math.round(value / step) * step;
}

/** Drops fields whose change is below `step`, so a still entity sends an empty object. */
export function diffEntity(previous: SyncEntity, next: SyncEntity, step = 0.01): SyncEntity | null {
    const delta: SyncEntity = { id: next.id };
    let changed = false;

    for (const key of ["x", "y", "z", "rotY"] as const) {
        const before = previous[key];
        const after = next[key];
        if (after === undefined) continue;
        if (before === undefined || Math.abs(after - before) >= step) {
            delta[key] = quantise(after, step);
            changed = true;
        }
    }

    if (next.flags !== undefined && next.flags !== previous.flags) {
        delta.flags = next.flags;
        changed = true;
    }

    return changed ? delta : null;
}

function toBase64(bytes: Uint8Array): string {
    let out = "";
    for (let index = 0; index < bytes.length; index += 3) {
        const a = bytes[index];
        const b = index + 1 < bytes.length ? bytes[index + 1] : 0;
        const c = index + 2 < bytes.length ? bytes[index + 2] : 0;
        out += B64[a >> 2];
        out += B64[((a & 3) << 4) | (b >> 4)];
        out += index + 1 < bytes.length ? B64[((b & 15) << 2) | (c >> 6)] : "=";
        out += index + 2 < bytes.length ? B64[c & 63] : "=";
    }
    return out;
}

function fromBase64(text: string): Uint8Array {
    const clean = text.replace(/=+$/, "");
    const length = Math.floor((clean.length * 3) / 4);
    const bytes = new Uint8Array(length);

    let byteIndex = 0;
    for (let index = 0; index < clean.length; index += 4) {
        const c0 = B64.indexOf(clean[index]);
        const c1 = B64.indexOf(clean[index + 1] ?? "A");
        const c2 = B64.indexOf(clean[index + 2] ?? "A");
        const c3 = B64.indexOf(clean[index + 3] ?? "A");
        const chunk = (c0 << 18) | (c1 << 12) | (c2 << 6) | c3;

        if (byteIndex < length) bytes[byteIndex++] = (chunk >> 16) & 0xff;
        if (byteIndex < length) bytes[byteIndex++] = (chunk >> 8) & 0xff;
        if (byteIndex < length) bytes[byteIndex++] = chunk & 0xff;
    }

    return bytes;
}
