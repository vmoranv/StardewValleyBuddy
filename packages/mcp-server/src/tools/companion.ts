// Migrated from StardewValley-MCP, https://github.com/amarisaster/StardewValley-MCP
// (原仓 src/index.ts；上游 LICENSE.txt 为 Apache-2.0)

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { sendAction } from "../bridge.js";
import {
    COMPANION_ENUM,
    DIRECTION_DESC,
    MODE_ENUM,
    err,
    ok,
    type ToolResult,
} from "./shared.js";

// ============================
// COMPANION MANAGEMENT TOOLS
// ============================
export const companionToolDefinitions: Tool[] = [
    {
        name: "stardew_spawn",
        description: "Spawn companions into the game world near the player.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_follow",
        description: "Make all companions follow the player.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_stay",
        description: "Make all companions stop and stay at their current position.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "stardew_set_mode",
        description: "Set a specific companion's mode. Modes: follow, farm, mine, fish, idle, player.",
        inputSchema: {
            type: "object",
            properties: {
                target: { type: "string", enum: COMPANION_ENUM },
                mode: { type: "string", enum: MODE_ENUM },
            },
            required: ["target", "mode"],
        },
    },
    {
        name: "stardew_move_to",
        description: "Move a companion to a tile via pathfinding. Async — check surroundings on next tick to see progress.",
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
        name: "stardew_warp_companion",
        description: "Teleport a specific companion to a named location.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                location: { type: "string", description: "Location name." },
                x: { type: "number", description: "Tile X." },
                y: { type: "number", description: "Tile Y." },
            },
            required: ["companion", "location", "x", "y"],
        },
    },
    {
        name: "stardew_face_direction",
        description: `Turn a companion to face a direction. ${DIRECTION_DESC}.`,
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM },
                direction: { type: "number", description: DIRECTION_DESC },
            },
            required: ["companion", "direction"],
        },
    },
    {
        name: "stardew_chat",
        description: "Send a chat message in the game.",
        inputSchema: {
            type: "object",
            properties: { message: { type: "string" } },
            required: ["message"],
        },
    },
    {
        name: "stardew_say",
        description: "Make a companion speak like a character in the game world: posts a 'CompanionName: text' chat line, queues the line on the NPC (the player can click the companion to see it), and optionally pops a villager-style dialogue box with the companion's portrait. Use this instead of stardew_chat when a companion should talk as a person.",
        inputSchema: {
            type: "object",
            properties: {
                companion: { type: "string", enum: COMPANION_ENUM, description: "Which companion speaks. Omit to just post a plain chat line." },
                message: { type: "string", description: "What the companion says. Write it in character, like natural speech." },
                popup: { type: "boolean", description: "Also open a villager-style dialogue box with portrait (interrupts the player's view). Default false." },
            },
            required: ["message"],
        },
    },
];

export function handleCompanionTool(name: string, a: Record<string, any>): ToolResult | undefined {
    switch (name) {
        case "stardew_spawn":
            return ok(sendAction({ actionType: "spawn" }));

        case "stardew_follow":
            return ok(sendAction({ actionType: "follow" }));

        case "stardew_stay":
            return ok(sendAction({ actionType: "stay" }));

        case "stardew_set_mode":
            if (!a.target || !a.mode)
                return err("target and mode are required.");
            return ok(sendAction({ actionType: "set_mode", target: a.target, mode: a.mode }));

        case "stardew_move_to":
            if (!a.companion || a.x == null || a.y == null)
                return err("companion, x, and y are required.");
            return ok(sendAction({
                actionType: "move_to",
                companion: a.companion,
                x: a.x, y: a.y,
            }));

        case "stardew_warp_companion":
            if (!a.companion || !a.location || a.x == null || a.y == null)
                return err("companion, location, x, and y are required.");
            return ok(sendAction({
                actionType: "warp_to",
                companion: a.companion,
                location: a.location,
                x: a.x, y: a.y,
            }));

        case "stardew_face_direction":
            if (!a.companion || a.direction == null)
                return err("companion and direction are required.");
            // The mod expects the game's int facing code (0=up,1=right,2=down,3=left);
            // the tool schema exposes the readable names instead.
            const FACING: Record<string, number> = { up: 0, right: 1, down: 2, left: 3 };
            const facing = FACING[String(a.direction).toLowerCase()];
            if (facing === undefined)
                return err(`direction must be one of: up, right, down, left (got "${a.direction}").`);
            return ok(sendAction({
                actionType: "face_direction",
                companion: a.companion,
                direction: facing,
            }));

        case "stardew_chat":
            if (!a.message)
                return err("message is required.");
            return ok(sendAction({ actionType: "chat", metadata: { message: a.message } }));

        case "stardew_say":
            if (!a.message)
                return err("message is required.");
            return ok(sendAction({
                actionType: "say",
                ...(a.companion ? { companion: a.companion, target: a.companion } : {}),
                message: a.message,
                ...(a.popup ? { popup: true } : {}),
            }));

        default:
            return undefined;
    }
}
