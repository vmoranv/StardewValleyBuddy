// Migrated from StardewValley-MCP, https://github.com/amarisaster/StardewValley-MCP
// (原仓 src/index.ts；上游 LICENSE.txt 为 Apache-2.0)

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import {
    getCompanionInventory,
    getCompanionState,
    getCompanionSurroundings,
    readBridge,
} from "../bridge.js";
import { COMPANION_ENUM, err, ok, type ToolResult } from "./shared.js";

// ============================
// STATE / READ TOOLS
// ============================
export const stateToolDefinitions: Tool[] = [
    {
        name: "stardew_get_state",
        description: "Get current game state — time, weather, location, player stats, companion status, nearby NPCs.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_get_surroundings",
        description: "Get what a companion can see — tiles, objects, crops, monsters, NPCs in a radius around them. The companion's 'eyes'. Companion must be in player mode.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM, description: "Which companion." },
            },
            required: ["companion"],
        },
    },
    {
        name: "stardew_get_inventory",
        description: "Get a companion's inventory — tools and items they're carrying.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
            },
            required: ["companion"],
        },
    },
    {
        name: "stardew_get_companion_state",
        description: "Get detailed state for a specific companion — position, location, health, stamina, mode, surroundings (if in player mode), inventory, last command result.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
            },
            required: ["companion"],
        },
    },
];

export function handleStateTool(name: string, a: Record<string, any>): ToolResult | undefined {
    switch (name) {
        case "stardew_get_state":
            return ok(readBridge());

        case "stardew_get_surroundings":
            if (!a.companion) return err("companion is required.");
            // Extract just surroundings from bridge data (companion must be in player mode)
            return ok(getCompanionSurroundings(a.companion));

        case "stardew_get_inventory":
            if (!a.companion) return err("companion is required.");
            return ok(getCompanionInventory(a.companion));

        case "stardew_get_companion_state":
            if (!a.companion) return err("companion is required.");
            return ok(getCompanionState(a.companion));

        default:
            return undefined;
    }
}
