import { context } from "./runtime";

/** A world-space point used by the draw API. */
export interface DrawPoint {
    x: number;
    y: number;
    z: number;
}

export interface DrawOptions {
    color?: string;
    /** Seconds the primitive stays visible; default 0.1. */
    duration?: number;
}

/**
 * World-space lines and meshes. On a server these commands replicate to connected clients; on a
 * client they draw locally. Coordinates are metres in world space.
 */
export const draw = {
    line(a: DrawPoint, b: DrawPoint, options: DrawOptions = {}): void {
        context().host.api.draw.line(a.x, a.y, a.z, b.x, b.y, b.z, options.color ?? "#ffffff", options.duration ?? 0.1);
    },

    polyline(points: DrawPoint[], options: DrawOptions = {}): void {
        context().host.api.draw.polyline(points as unknown, options.color ?? "#ffffff", options.duration ?? 0.1);
    },

    box(center: DrawPoint, size: DrawPoint, options: DrawOptions = {}): void {
        context().host.api.draw.box(center.x, center.y, center.z, size.x, size.y, size.z, options.color ?? "#ffffff", options.duration ?? 0.1);
    },

    circle(center: DrawPoint, radius: number, options: DrawOptions & { segments?: number } = {}): void {
        context().host.api.draw.circle(center.x, center.y, center.z, radius, options.color ?? "#ffffff", options.duration ?? 0.1, options.segments ?? 24);
    },

    mesh(vertices: DrawPoint[], indices: number[], options: DrawOptions & { filled?: boolean } = {}): void {
        context().host.api.draw.mesh(vertices as unknown, indices, options.color ?? "#ffffff", options.filled ?? false, options.duration ?? 0.1);
    },

    clear(): void {
        context().host.api.draw.clear();
    }
};
