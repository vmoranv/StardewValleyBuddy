type Tile = [number, number];
type AreaFields =
    | { x1: number; y1: number; x2: number; y2: number }
    | { tiles: Tile[] }
    | { x: number; y: number };
type AreaNormalization =
    | { ok: true; fields?: AreaFields }
    | { ok: false; error: string };

const MAX_AREA_TILES = 500;

export const TILE_LIST_SCHEMA = {
    type: "array",
    minItems: 1,
    maxItems: MAX_AREA_TILES,
    items: {
        type: "array",
        minItems: 2,
        maxItems: 2,
        items: { type: "integer" },
    },
    description: "Explicit tile coordinates as [[x,y],...].",
};

export const AREA_SCHEMA = {
    type: "object",
    description:
        "Choose one area form: inclusive bounds {x1,y1,x2,y2}, " +
        "{bounds:{x1,y1,x2,y2}}, square {center:{x,y},radius,shape:\"square\"}, " +
        "or {tiles:[[x,y],...]}. Coordinates must be non-negative; each area may contain at most 500 tiles.",
    oneOf: [
        {
            type: "object",
            properties: {
                x1: { type: "integer" }, y1: { type: "integer" },
                x2: { type: "integer" }, y2: { type: "integer" },
            },
            required: ["x1", "y1", "x2", "y2"],
        },
        {
            type: "object",
            properties: {
                bounds: {
                    type: "object",
                    properties: {
                        x1: { type: "integer" }, y1: { type: "integer" },
                        x2: { type: "integer" }, y2: { type: "integer" },
                    },
                    required: ["x1", "y1", "x2", "y2"],
                },
            },
            required: ["bounds"],
        },
        {
            type: "object",
            properties: {
                center: {
                    type: "object",
                    properties: { x: { type: "integer" }, y: { type: "integer" } },
                    required: ["x", "y"],
                },
                radius: { type: "integer", minimum: 0 },
                shape: { type: "string", enum: ["square"] },
            },
            required: ["center", "radius", "shape"],
        },
        {
            type: "object",
            properties: { tiles: TILE_LIST_SCHEMA },
            required: ["tiles"],
        },
    ],
};

function hasOwn(value: Record<string, any>, key: string): boolean {
    return Object.prototype.hasOwnProperty.call(value, key);
}

function isTileCoordinate(value: unknown): value is number {
    return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function normalizeBounds(value: Record<string, any>): AreaNormalization {
    const keys = ["x1", "y1", "x2", "y2"];
    if (!keys.every((key) => hasOwn(value, key) && isTileCoordinate(value[key])))
        return { ok: false, error: "area bounds require integer x1, y1, x2, and y2." };
    const width = Math.abs(value.x2 - value.x1) + 1;
    const height = Math.abs(value.y2 - value.y1) + 1;
    const tileCount = width * height;
    if (!Number.isSafeInteger(tileCount) || tileCount > MAX_AREA_TILES)
        return { ok: false, error: "area exceeds the " + MAX_AREA_TILES + "-tile limit." };

    return {
        ok: true,
        fields: {
            x1: Math.min(value.x1, value.x2),
            y1: Math.min(value.y1, value.y2),
            x2: Math.max(value.x1, value.x2),
            y2: Math.max(value.y1, value.y2),
        },
    };
}

function normalizeTileList(value: unknown, allowObjectTiles = false): AreaNormalization {
    if (!Array.isArray(value) || value.length === 0)
        return { ok: false, error: "area tiles must be a non-empty [[x,y],...] array." };
    if (value.length > MAX_AREA_TILES)
        return { ok: false, error: "area exceeds the " + MAX_AREA_TILES + "-tile limit." };

    const tiles: Tile[] = [];
    for (const entry of value) {
        let x: unknown;
        let y: unknown;
        if (Array.isArray(entry) && entry.length === 2) {
            [x, y] = entry;
        } else if (allowObjectTiles && entry !== null && typeof entry === "object"
            && hasOwn(entry as Record<string, any>, "x") && hasOwn(entry as Record<string, any>, "y")) {
            x = (entry as Record<string, any>).x;
            y = (entry as Record<string, any>).y;
        } else {
            return { ok: false, error: "each area tile must be [x,y] with integer coordinates." };
        }

        if (!isTileCoordinate(x) || !isTileCoordinate(y))
            return { ok: false, error: "each area tile must be [x,y] with integer coordinates." };
        tiles.push([x, y]);
    }

    return { ok: true, fields: { tiles } };
}

function normalizeArea(value: unknown): AreaNormalization {
    if (value === null || typeof value !== "object" || Array.isArray(value))
        return { ok: false, error: "area must be an object containing bounds, a square center, or tiles." };

    const area = value as Record<string, any>;
    const hasBounds = hasOwn(area, "bounds");
    const hasDirectBounds = ["x1", "y1", "x2", "y2"].some((key) => hasOwn(area, key));
    const hasCenter = hasOwn(area, "center") || hasOwn(area, "radius") || hasOwn(area, "shape");
    const hasTiles = hasOwn(area, "tiles");
    if ([hasBounds, hasDirectBounds, hasCenter, hasTiles].filter(Boolean).length !== 1)
        return { ok: false, error: "area must use exactly one of bounds, square center, or tiles." };

    if (hasBounds) {
        if (area.bounds === null || typeof area.bounds !== "object" || Array.isArray(area.bounds))
            return { ok: false, error: "area.bounds must contain integer x1, y1, x2, and y2." };
        return normalizeBounds(area.bounds as Record<string, any>);
    }
    if (hasDirectBounds) return normalizeBounds(area);
    if (hasTiles) return normalizeTileList(area.tiles);

    const center = area.center;
    if (center === null || typeof center !== "object" || Array.isArray(center)
        || !isTileCoordinate(center.x) || !isTileCoordinate(center.y)
        || !isTileCoordinate(area.radius) || area.radius < 0 || area.shape !== "square")
        return { ok: false, error: "square area requires integer center.x/center.y, non-negative integer radius, and shape=\"square\"." };

    const x1 = center.x - area.radius;
    const y1 = center.y - area.radius;
    const x2 = center.x + area.radius;
    const y2 = center.y + area.radius;
    if (![x1, y1, x2, y2].every(Number.isSafeInteger))
        return { ok: false, error: "square area coordinates exceed the safe integer range." };

    return normalizeBounds({ x1, y1, x2, y2 });
}

function normalizeLegacyWorkArea(args: Record<string, any>): AreaNormalization {
    const hasSingle = hasOwn(args, "x") || hasOwn(args, "y");
    const hasBounds = ["x1", "y1", "x2", "y2"].some((key) => hasOwn(args, key));
    const hasTiles = hasOwn(args, "tiles");
    if ([hasSingle, hasBounds, hasTiles].filter(Boolean).length > 1)
        return { ok: false, error: "provide only one legacy work area form: x/y, x1/y1/x2/y2, or tiles." };

    if (hasSingle) {
        if (!isTileCoordinate(args.x) || !isTileCoordinate(args.y))
            return { ok: false, error: "legacy work tile requires integer x and y." };
        return { ok: true, fields: { x: args.x, y: args.y } };
    }
    if (hasBounds) return normalizeBounds(args);
    if (hasTiles) return normalizeTileList(args.tiles, true);
    return { ok: true };
}

export function normalizeWorkArea(args: Record<string, any>): AreaNormalization {
    const hasArea = hasOwn(args, "area");
    const hasLegacy = ["x", "y", "x1", "y1", "x2", "y2", "tiles"].some((key) => hasOwn(args, key));
    if (hasArea && hasLegacy)
        return { ok: false, error: "provide area or legacy work coordinates, not both." };
    return hasArea ? normalizeArea(args.area) : normalizeLegacyWorkArea(args);
}

function parseLegacyTilePair(value: unknown): Tile | null {
    if (typeof value !== "string") return null;
    const rawParts = value.split(",").map((part) => part.trim());
    if (rawParts.length !== 2 || rawParts.some((part) => part.length === 0)) return null;
    const parts = rawParts.map(Number);
    if (!isTileCoordinate(parts[0]) || !isTileCoordinate(parts[1])) return null;
    return [parts[0], parts[1]];
}

export function normalizeCareArea(args: Record<string, any>): AreaNormalization {
    const hasArea = hasOwn(args, "area");
    const hasTiles = hasOwn(args, "tiles");
    if (hasArea && hasTiles)
        return { ok: false, error: "provide area or tiles, not both." };

    if (args.task === "scan_tiles" && (hasArea || hasTiles)
        && (hasOwn(args, "chest") || hasOwn(args, "filter")))
        return { ok: false, error: "provide area/tiles or the legacy scan chest/filter range, not both." };

    if (hasArea) {
        return normalizeArea(args.area);
    }
    if (hasTiles) return normalizeTileList(args.tiles);

    if (args.task === "scan_tiles") {
        const hasChest = hasOwn(args, "chest");
        const hasFilter = hasOwn(args, "filter");
        if (!hasChest && !hasFilter)
            return { ok: false, error: "scan_tiles requires area bounds or explicit tiles." };
        const start = parseLegacyTilePair(args.chest);
        const end = parseLegacyTilePair(args.filter);
        if (!start || !end)
            return { ok: false, error: "legacy scan range requires chest=\"x1,y1\" and filter=\"x2,y2\"." };
        return normalizeBounds({ x1: start[0], y1: start[1], x2: end[0], y2: end[1] });
    }

    return { ok: true };
}
