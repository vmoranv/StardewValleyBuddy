# AGENTS.md

面向 AI Agent（Claude Code、Codex 等）的部署与验收文档。目标是让 agent 只靠本文件把 StardewValleyBuddy 从零部署到可用。

## 这是什么

Stardew Valley 的 AI 伴侣：SMAPI mod 在游戏内维护伙伴（NPC + 隐藏 shadow Farmer），MCP server（Node.js, stdio）暴露 32 个 `stardew_*` 工具，两者通过 `bridge_data.json`（状态上行）+ `actions/` 目录（指令下行）的文件桥通信。AI 通过 MCP 工具调用直接操控伙伴。

## 部署步骤（从零到能用）

### 步骤 1 · 安装 SMAPI

1. 确认游戏为 Stardew Valley 1.6+，找到游戏目录（Steam 默认 `C:/Program Files/Steam/steamapps/common/Stardew Valley`）。
2. 从 https://smapi.io 下载 SMAPI 4.x 并安装（安装脚本会自动检测游戏目录，生成 `StardewModdingAPI.exe`）。
3. 之后一律通过 `StardewModdingAPI.exe` 启动游戏，不要走 Steam 直启。

### 步骤 2 · 编译并放置 mod

```bash
cd <仓库>/packages/smapi-mod
export GAME_PATH="<游戏目录>"
dotnet build
```

构建产物在 `bin/Debug/net6.0/`，ModBuildConfig 会**自动部署**到 `<游戏目录>/Mods/StardewValleyBuddy/` 并生成发布用 zip。

验收标志：`Mods/StardewValleyBuddy/manifest.json` 与 `StardewValleyBuddy.dll` 都存在。自定义精灵（可选）放入 `packages/smapi-mod/assets/`。

### 步骤 3 · 构建 MCP server

```bash
cd <仓库>
pnpm install
pnpm build        # = pnpm -r build，产出 packages/mcp-server/build/index.js
```

### 步骤 4 · 注册到 MCP 客户端

Claude Code：

```bash
claude mcp add stardew -- node "<仓库绝对路径>/packages/mcp-server/build/index.js"
```

若 mod 不在仓库默认相对路径，显式指定环境变量：

```bash
claude mcp add stardew \
  -e STARDEW_BRIDGE_PATH="<游戏目录>/Mods/StardewMCPBridge/bridge_data.json" \
  -e STARDEW_ACTION_DIR="<游戏目录>/Mods/StardewMCPBridge/actions" \
  -- node "<仓库绝对路径>/packages/mcp-server/build/index.js"
```

通用 JSON 配置（`mcpServers` 块）见仓库 README「注册到 MCP 客户端」一节。

### 步骤 5 · 冒烟验证

```bash
pnpm smoke
```

预期输出 `smoke: tools/list = 32`。**工具数量必须为 31**（26 个原有工具 + 4 个作业/对话工具）；数量不符说明 build 产物过期或包不完整，回到步骤 3。

### 步骤 6 · 游戏内验收

1. 通过 `StardewModdingAPI.exe` 启动游戏，加载存档。
2. SMAPI 控制台应出现 `StardewMCPBridge` 加载日志，`Mods/StardewMCPBridge/bridge_data.json` 随之生成。
3. 在 MCP 客户端调用 `stardew_get_state` —— 返回游戏状态 JSON 即为打通。
4. 调用 `stardew_spawn` 生成伙伴，再 `stardew_set_mode`（target=Companion1, mode=player）进入直接操控模式。

## 开工前 30 秒自检

- 游戏在运行且已读档（`bridge_data.json` 最近修改时间是当前游戏会话）
- `pnpm smoke` 输出 `tools/list = 32`
- `stardew_get_state` 能返回非 `{"error": "Bridge file not found..."}` 的内容

## 部署常见问题

| 症状 | 原因与处理 |
|------|-----------|
| `Bridge file not found. Is the SMAPI mod running?` | 游戏未启动 / 未读档 / mod 未加载。检查 SMAPI 控制台报错，确认 `Mods/StardewMCPBridge/` 存在 |
| smoke 超时（`timeout waiting for tools/list`） | `packages/mcp-server/build/index.js` 不存在或为旧产物，重跑 `pnpm build` |
| smoke 报 `expected 32 tools, got N` | mcp-server 包不完整或被改动，核对工具清单后重建 |
| 指令发出但游戏无反应 | 检查 `STARDEW_ACTION_DIR` 是否指向 mod 实际监听的 `actions/` 目录；mod 消费指令后会删除文件，目录长期堆积文件说明 mod 侧没在消费 |
| `dotnet build` 找不到游戏 | 未设置 `GAME_PATH`，或路径含空格未加引号 |
| 伙伴卡住不动 | player 模式下 `stardew_move_to` 是异步的，先 `stardew_get_surroundings` 看是否被地形卡住，可用 `stardew_warp_companion` 脱困 |

## 开发约定（要改本仓库的 agent 请读）

- pnpm workspace 单仓多包：`packages/mcp-server`（TypeScript/Node）、`packages/smapi-mod`（C#/.NET）。根目录只放编排脚本：`pnpm build`（= `pnpm -r build`）、`pnpm smoke`（spawn 编译产物 → MCP 握手 → 断言 tools 数量为 32）。
- 文件桥协议：指令一条一个 JSON 文件，文件名 `<时间戳>-<序号>.json` 保证字典序即执行序；写入用「临时文件 + rename」原子发布；mod 消费后删除。`bridge_data.json` 同样原子写入，server 侧只读。
- 环境变量：`STARDEW_BRIDGE_PATH`（状态文件）、`STARDEW_ACTION_DIR`（指令目录）；缺省回退到 `packages/smapi-mod/` 下的相对路径。
- 修改 mcp-server 工具集时必须同步更新 README 工具清单表，并保证 `pnpm smoke` 的期望数量一致。
