#!/usr/bin/env node
// Migrated from StardewValley-MCP, https://github.com/amarisaster/StardewValley-MCP
// (原仓 src/index.ts 的 Server 装配与工具注册；上游 LICENSE.txt 为 Apache-2.0)

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
    CallToolRequestSchema,
    ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { actionsToolDefinitions, handleActionsTool, workToolDefinitions } from "./tools/actions.js";
import { companionToolDefinitions, handleCompanionTool } from "./tools/companion.js";
import { err, type DomainHandler } from "./tools/shared.js";
import { stateToolDefinitions, handleStateTool } from "./tools/state.js";

const TOOL_DEFINITIONS = [
    ...stateToolDefinitions,
    ...companionToolDefinitions,
    ...actionsToolDefinitions,
    ...workToolDefinitions,
];

// Domain dispatch order is arbitrary — each handler only claims tools it owns.
const HANDLERS: DomainHandler[] = [
    handleStateTool,
    handleCompanionTool,
    handleActionsTool,
];

class StardewBridgeServer {
    private server: Server;

    constructor() {
        this.server = new Server(
            {
                name: "stardew-valley-buddy",
                version: "0.4.0",
            },
            {
                capabilities: {
                    tools: {},
                },
            }
        );

        this.setupHandlers();
    }

    private setupHandlers() {
        this.server.setRequestHandler(ListToolsRequestSchema, async () => ({
            tools: TOOL_DEFINITIONS,
        }));

        this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
            const { name, arguments: args } = request.params;
            const a = (args || {}) as Record<string, any>;

            try {
                for (const handler of HANDLERS) {
                    const result = handler(name, a);
                    if (result) return result;
                }
                return err(`Unknown tool: ${name}`);
            } catch (error: any) {
                return err(error.message);
            }
        });
    }

    async run() {
        const transport = new StdioServerTransport();
        await this.server.connect(transport);
        console.error("Stardew Valley Buddy v0.4.0 running on stdio");
    }
}

const server = new StardewBridgeServer();
server.run().catch(console.error);
