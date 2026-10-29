// Migrated from StardewValley-MCP, https://github.com/amarisaster/StardewValley-MCP
// (原仓 src/index.ts；上游 LICENSE.txt 为 Apache-2.0)

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { sendAction } from "../bridge.js";
import {
    COMPANION_ENUM,
    TOOL_ENUM,
    err,
    ok,
    type ToolResult,
} from "./shared.js";
import { AREA_SCHEMA, TILE_LIST_SCHEMA, normalizeCareArea, normalizeWorkArea } from "./area.js";

// ============================
// WORLD / ACTION TOOLS
// ============================
export const actionsToolDefinitions: Tool[] = [
    {
        name: "stardew_farm",
        description: "Enable auto-farm mode for all companions.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_mine",
        description: "Enable combat/mining mode for all companions.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_fish",
        description: "Enable fishing mode for all companions.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_water_all",
        description: "Instantly water all unwatered crops in the current location.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_harvest_all",
        description: "Instantly harvest all ready crops in the current location.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_action",
        description: "Send a custom action (water, harvest, clear, hoe) at a tile. Defaults to the player's current location; pass `location` for tiles elsewhere (e.g. \"Farm\" while the player is indoors).",
        inputSchema: {
            type: "object",
            properties: {
                actionType: { type: "string" },
                x: { type: "number" },
                y: { type: "number" },
                location: { type: "string", description: "Map name to act on. Omit to use the player's current location." },
            },
            required: ["actionType"],
        },
    },
    {
        name: "stardew_warp",
        description: "Warp all companions to a location.",
        inputSchema: {
            type: "object",
            properties: {
                location: { type: "string", description: "Location name (Farm, Town, Mine, Beach, Forest, Mountain, etc.)." },
                x: { type: "number", description: "Tile X." },
                y: { type: "number", description: "Tile Y." },
            },
            required: ["location", "x", "y"],
        },
    },
    {
        name: "stardew_use_tool",
        description: "Use a tool at a tile. Tools: pickaxe, axe, hoe, watering_can, sword.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                tool: { type: "string", enum: TOOL_ENUM },
                x: { type: "number", description: "Target tile X." },
                y: { type: "number", description: "Target tile Y." },
            },
            required: ["companion", "tool", "x", "y"],
        },
    },
    {
        name: "stardew_interact",
        description: "Interact with an object, crop, chest, ladder, or machine at a tile.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                x: { type: "number", description: "Target tile X." },
                y: { type: "number", description: "Target tile Y." },
            },
            required: ["companion", "x", "y"],
        },
    },
    {
        name: "stardew_attack",
        description: "Attack the nearest monster with equipped weapon.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
            },
            required: ["companion"],
        },
    },
    {
        name: "stardew_cast_fishing_rod",
        description: "Cast the fishing rod and auto-hook when a fish bites. Companion must be near water.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
            },
            required: ["companion"],
        },
    },
    {
        name: "stardew_set_auto_combat",
        description: "Toggle auto-combat: when enabled, the companion automatically attacks nearby monsters each tick.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                enabled: { type: "boolean", description: "true to enable, false to disable." },
            },
            required: ["companion", "enabled"],
        },
    },
    {
        name: "stardew_eat_item",
        description: "Eat a food item from inventory to restore health/stamina.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                slot: { type: "number", description: "Inventory slot index (optional — picks first edible if omitted)." },
            },
            required: ["companion"],
        },
    },
];
/**
 * Job tools. `work` is region-shaped on purpose: the mod walks the companion to
 * each tile and keeps swinging until the tree/rock is gone, so an agent can fell
 * a tree with one call instead of one call per swing.
 */
export const workToolDefinitions = [
    {
        name: "stardew_work",
        description:
            "Run an intent over an area until done. The companion decides per tile what to do " +
            "(which tool, whether it's already done, when to refill the watering can). " +
            "tasks: clear_area (trees/rocks/weeds/bushes — anything in the way), plant (till → sow seed → water), " +
            "water (skips already-watered tiles, auto-refills from a nearby water source), " +
            "harvest (crops + bushes + fruit trees), sell (shipping bin; optional filter), " +
            "chop / mine (explicit overrides). " +
            "Area: inclusive bounds {x1,y1,x2,y2}, square center {center:{x,y},radius,shape:\"square\"}, " +
            "or explicit tiles [[x,y],...]; capped at 500 tiles. Legacy flat x/y, x1/y1/x2/y2, and tiles remain supported. " +
            "Returns immediately — poll stardew_get_job for progress.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                task: { type: "string", enum: ["clear_area", "plant", "water", "harvest", "sell", "chop", "mine"] },
                location: { type: "string", description: "Map name. Defaults to the companion's current location." },
                area: AREA_SCHEMA,
                x: { type: "number" }, y: { type: "number" },
                x1: { type: "number" }, y1: { type: "number" },
                x2: { type: "number" }, y2: { type: "number" },
                tiles: TILE_LIST_SCHEMA,
                seed: { type: "string", description: "For task=plant: seed name in inventory, e.g. \"Parsnip Seeds\"." },
                filter: { type: "string", enum: ["all", "crops", "forage", "fish", "minerals"], description: "For task=sell." },
            },
            required: ["companion", "task"],
        },
    },
    {
        name: "stardew_care",
        description:
            "Building/animal/inventory tasks. tasks: " +
            "tend_animals (pet every animal and collect eggs/milk/wool; optional `building` filter like \"Coop\"), " +
            "feed_animals (refill troughs/silos), " +
            "inspect_crops (read-only: daysToMature per crop, so you can plan instead of guessing), " +
            "scan_tiles (read-only: every non-empty tile in a region — objects, terrain, crops; accepts location and the same area forms as stardew_work), " +
            "chest_deposit / chest_withdraw (need `chest` as \"x,y\" and an optional `filter` name substring).",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                task: { type: "string", enum: ["tend_animals", "feed_animals", "inspect_crops", "scan_tiles", "chest_deposit", "chest_withdraw"] },
                location: { type: "string", description: "Map name for tile scans. Defaults to the companion's current location." },
                area: AREA_SCHEMA,
                tiles: TILE_LIST_SCHEMA,
                building: { type: "string", description: "For animal tasks: building type substring, e.g. \"Coop\"." },
                chest: { type: "string", description: "For chest tasks: tile as \"x,y\" in the companion's current location. Legacy scan_tiles range start is also accepted." },
                filter: { type: "string", description: "Optional item name substring. Legacy scan_tiles range end is also accepted." },
            },
            required: ["companion", "task"],
        },
    },
    {
        name: "stardew_get_job",
        description: "Get the current job's progress (done/skipped/failed counts, current tile, skip reasons).",
        inputSchema: {
            type: "object",
            properties: { companion: { type: "string", enum: COMPANION_ENUM } },
            required: ["companion"],
        },
    },
    {
        name: "stardew_cancel_job",
        description: "Cancel the companion's running job.",
        inputSchema: {
            type: "object",
            properties: { companion: { type: "string", enum: COMPANION_ENUM } },
            required: ["companion"],
        },
    },
    {
        name: "stardew_talk_to",
        description:
            "Talk to an NPC by name. Walks to an adjacent tile first, then returns their dialogue. " +
            "Call again if the first call only reports that it is walking.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                npc: { type: "string", description: "NPC name, e.g. \"Abigail\"." },
            },
            required: ["companion", "npc"],
        },
    },

];

export function handleActionsTool(name: string, a: Record<string, any>): ToolResult | undefined {
    switch (name) {
        case "stardew_farm":
            return ok(sendAction({ actionType: "farm" }));

        case "stardew_mine":
            return ok(sendAction({ actionType: "mine" }));

        case "stardew_fish":
            return ok(sendAction({ actionType: "fish" }));

        case "stardew_water_all":
            return ok(sendAction({ actionType: "water_all" }));

        case "stardew_harvest_all":
            return ok(sendAction({ actionType: "harvest_all" }));

        case "stardew_action":
            if (!a.actionType)
                return err("actionType is required.");
            // Forward only the known fields rather than the raw args object,
            // so arbitrary client-supplied keys can't leak into the bridge.
            return ok(sendAction({
                actionType: a.actionType,
                ...(a.x != null ? { x: a.x } : {}),
                ...(a.y != null ? { y: a.y } : {}),
                ...(a.location ? { location: a.location } : {}),
            }));

        case "stardew_warp":
            if (!a.location || a.x == null || a.y == null)
                return err("location, x, and y are required.");
            return ok(sendAction({ actionType: "warp", location: a.location, x: a.x, y: a.y }));

        // --- Player mode: companion-targeted commands ---
        case "stardew_use_tool":
            if (!a.companion || !a.tool || a.x == null || a.y == null)
                return err("companion, tool, x, and y are required.");
            return ok(sendAction({
                actionType: "use_tool",
                companion: a.companion,
                tool: a.tool,
                x: a.x, y: a.y,
            }));

        case "stardew_interact":
            if (!a.companion || a.x == null || a.y == null)
                return err("companion, x, and y are required.");
            return ok(sendAction({
                actionType: "interact",
                companion: a.companion,
                x: a.x, y: a.y,
            }));

        case "stardew_attack":
            if (!a.companion) return err("companion is required.");
            return ok(sendAction({
                actionType: "attack",
                companion: a.companion,
            }));

        case "stardew_cast_fishing_rod":
            if (!a.companion) return err("companion is required.");
            return ok(sendAction({
                actionType: "cast_fishing_rod",
                companion: a.companion,
            }));

        case "stardew_set_auto_combat":
            if (!a.companion || a.enabled == null)
                return err("companion and enabled are required.");
            return ok(sendAction({
                actionType: "set_auto_combat",
                companion: a.companion,
                enabled: a.enabled,
            }));

        case "stardew_eat_item":
            if (!a.companion) return err("companion is required.");
            return ok(sendAction({
                actionType: "eat_item",
                companion: a.companion,
                ...(a.slot != null ? { slot: a.slot } : {}),
            }));

        case "stardew_work": {
            if (!a.companion || !a.task) return err("companion and task are required.");
            const area = normalizeWorkArea(a);
            if (!area.ok) return err(area.error);
            if (a.task !== "sell" && !area.fields)
                return err("area is required; use inclusive bounds, a square center, or explicit tiles.");
            if (a.task === "plant" && !a.seed)
                return err("task=plant requires a seed name.");
            return ok(sendAction({
                actionType: "work",
                companion: a.companion,
                task: a.task,
                ...(a.location ? { location: a.location } : {}),
                ...(a.seed ? { seed: a.seed } : {}),
                ...(a.filter ? { filter: a.filter } : {}),
                ...(area.fields ?? {}),
            }));
        }

        case "stardew_care": {
            if (!a.companion || !a.task) return err("companion and task are required.");
            if ((a.task === "chest_deposit" || a.task === "chest_withdraw") && !a.chest)
                return err("chest tasks require a chest tile as \"x,y\".");
            const area = normalizeCareArea(a);
            if (!area.ok) return err(area.error);
            return ok(sendAction({
                actionType: "care",
                companion: a.companion,
                task: a.task,
                ...(a.location ? { location: a.location } : {}),
                ...(a.building ? { building: a.building } : {}),
                ...(a.chest ? { chest: a.chest } : {}),
                ...(a.filter ? { filter: a.filter } : {}),
                ...(area.fields ?? {}),
            }));
        }

        case "stardew_get_job":
            if (!a.companion) return err("companion is required.");
            return ok(sendAction({ actionType: "get_job", companion: a.companion }));

        case "stardew_cancel_job":
            if (!a.companion) return err("companion is required.");
            return ok(sendAction({ actionType: "cancel_job", companion: a.companion }));

        case "stardew_talk_to":
            if (!a.companion || !a.npc) return err("companion and npc are required.");
            return ok(sendAction({ actionType: "talk_to", companion: a.companion, npc: a.npc }));

        default:
            return undefined;
    }
}
