// Migrated from StardewValley-MCP, https://github.com/amarisaster/StardewValley-MCP
// (原仓 src/index.ts 的文件 I/O 逻辑；上游 LICENSE.txt 为 Apache-2.0)

import * as fs from "fs";
import * as path from "path";

// Point these to where the SMAPI mod is installed (its Mods/StardewMCPBridge/ folder).
// Defaults assume the sibling smapi-mod package inside this repo; override via env vars.
// __dirname is <repo>/packages/mcp-server/build → ../.. lands on <repo>/packages.
const BRIDGE_PATH = process.env.STARDEW_BRIDGE_PATH
    || path.resolve(__dirname, "../../smapi-mod/bridge_data.json");
const ACTION_DIR = process.env.STARDEW_ACTION_DIR
    || path.resolve(__dirname, "../../smapi-mod/actions");

// Bridge data older than this gets a `stale: true` warning so the agent can
// re-read before acting on it. Non-blocking: the payload is still returned.
const STALE_THRESHOLD_MS = 5000;

// Monotonic counter so two commands issued within the same millisecond still
// get distinct, lexically-ordered filenames (single-threaded server, no locking needed).
let actionSeq = 0;

export function sendAction(action: object): string {
    // One immutable file per command. The mod drains them in filename order and
    // deletes each after processing — a race-free queue that can't drop or
    // double-execute commands the way a single overwritten file could.
    fs.mkdirSync(ACTION_DIR, { recursive: true });
    const name = `${Date.now()}-${String(actionSeq++).padStart(6, "0")}`;
    const finalPath = path.join(ACTION_DIR, `${name}.json`);
    const tmpPath = `${finalPath}.tmp`;
    // Atomic publish: write to temp then rename so the mod never reads a partial file.
    fs.writeFileSync(tmpPath, JSON.stringify(action));
    fs.renameSync(tmpPath, finalPath);
    return "Command sent.";
}

// Flag stale snapshots. The mod stamps `syncedAt` as a round-trip ISO 8601
// timestamp (DateTime.UtcNow.ToString("o")), which Date.parse reads directly.
function withStaleWarning(raw: string): string {
    try {
        const data: unknown = JSON.parse(raw);
        if (data && typeof data === "object" && !Array.isArray(data)) {
            const syncedAt = (data as { syncedAt?: unknown }).syncedAt;
            const syncedAtMs = typeof syncedAt === "string" ? Date.parse(syncedAt) : Number.NaN;
            if (!Number.isNaN(syncedAtMs) && Date.now() - syncedAtMs > STALE_THRESHOLD_MS) {
                return JSON.stringify({ ...(data as Record<string, unknown>), stale: true }, null, 2);
            }
        }
    } catch {
        // Malformed bridge data: hand it back untouched; the mod may be mid-write.
    }
    return raw;
}

export function readBridge(): string {
    if (fs.existsSync(BRIDGE_PATH)) {
        return withStaleWarning(fs.readFileSync(BRIDGE_PATH, "utf-8"));
    }
    return '{"error": "Bridge file not found. Is the SMAPI mod running?"}';
}

// Helper to read companion state from bridge data
function getCompanionState(companionName: string): string {
    const raw = readBridge();
    try {
        const data = JSON.parse(raw);
        if (data.companions) {
            const companion = (data.companions as any[]).find((c: any) => c.name === companionName);
            if (companion) return JSON.stringify(companion, null, 2);
        }
        return `Companion "${companionName}" not found in bridge data.`;
    } catch {
        return raw;
    }
}

function getCompanionSurroundings(companionName: string): string {
    const raw = readBridge();
    try {
        const data = JSON.parse(raw);
        if (data.companions) {
            const companion = (data.companions as any[]).find((c: any) => c.name === companionName);
            if (companion?.surroundings) return JSON.stringify({
                tile: companion.tile,
                location: companion.location,
                surroundings: companion.surroundings,
            }, null, 2);
            if (companion) return `Companion "${companionName}" has no surroundings data (is it in player mode?).`;
        }
        return `Companion "${companionName}" not found in bridge data.`;
    } catch {
        return raw;
    }
}

function getCompanionInventory(companionName: string): string {
    const raw = readBridge();
    try {
        const data = JSON.parse(raw);
        if (data.companions) {
            const companion = (data.companions as any[]).find((c: any) => c.name === companionName);
            if (companion?.inventory) return JSON.stringify(companion.inventory, null, 2);
            if (companion) return `Companion "${companionName}" has no inventory data (is it in player mode?).`;
        }
        return `Companion "${companionName}" not found in bridge data.`;
    } catch {
        return raw;
    }
}

// Internal read helpers shared by the state tools.
export { getCompanionState, getCompanionInventory, getCompanionSurroundings };
