// Migrated from StardewValley-MCP, https://github.com/amarisaster/StardewValley-MCP
// (原仓 src/index.ts 的常量与工具结果辅助；上游 LICENSE.txt 为 Apache-2.0)

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// Standard MCP text-content tool result, as returned by CallToolRequest handlers.
// A type alias (not an interface) so it carries an implicit index signature and
// stays assignable to the SDK's index-signature'd handler result types.
export type ToolResult = {
    content: Array<{ type: "text"; text: string }>;
};

// Loosely-typed tool arguments, matching the original `(args || {}) as any` dispatch.
export type ToolArgs = Record<string, any>;

// Domain handlers claim only the tools they own; unknown names yield undefined.
export type DomainHandler = (name: string, args: ToolArgs) => ToolResult | undefined;

export function ok(text: string): ToolResult {
    return { content: [{ type: "text" as const, text }] };
}

export function err(text: string): ToolResult {
    return { content: [{ type: "text" as const, text: `Error: ${text}` }] };
}

export const COMPANION_ENUM = ["Companion1", "Companion2"];
export const MODE_ENUM = ["follow", "farm", "mine", "fish", "idle", "player"];
export const TOOL_ENUM = ["pickaxe", "axe", "hoe", "watering_can", "sword"];
export const DIRECTION_DESC = "0=up, 1=right, 2=down, 3=left";
