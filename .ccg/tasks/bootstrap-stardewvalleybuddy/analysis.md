# 分析报告（纯 Claude 模式 — 外部模型不可用）

> antigravity 地区限制 / grok 未登录 / codex 未配置 → 全部分析与实施由 Claude 完成。

## 1. 参考项目现状（D:/coding/StardewValley-MCP）

- `mcp-server/`：TypeScript MCP server，26 个 `stardew_*` 工具，npm + tsc，564 行单文件 `src/index.ts`
- `smapi-mod/`：SMAPI 4.0 mod，8 个 C# 文件约 2400 行，通过 `bridge_data.json` 文件轮询 + `actions/` 目录文件队列与 MCP server 通信

## 2. SMAPI API 覆盖审计（核心缺陷清单）

### 已覆盖
- GameLoop events: GameLaunched / UpdateTicked / DayStarted / DayEnding / TimeChanged / ReturnedToTitle
- Content events: AssetRequested（注入 Portraits/Characters/Data）
- Game1 运行时 API：locations、player、currentLocation、chatBox、drawDialogue、getLocationFromName

### 缺失（迁移时修复或列入 backlog）
| 缺口 | 影响 | 处置 |
|---|---|---|
| **helper.Data 读写**（ReadJsonFile/WriteJsonFile/WriteGlobalData） | 存档/读档后 companion 状态、命令历史全部丢失 | P0：SaveLoaded 事件恢复 companion + Data API 持久化 |
| **SaveLoaded / Saving / Saved 事件** | 无存档生命周期感知，读档后僵尸状态 | P0 |
| **helper.Console（AddCommand）** | 无法 `debug` 侧排查，只能靠 MCP | P1 |
| **GMCM（Generic Mod Config Menu）** | bridge 路径、companion 名字硬编码，不可配置 | P1：加 ModConfig 类 + GMCM 集成 |
| **Input events**（ButtonPressed/Released） | 无手动快捷键控制 companion | P2 backlog |
| **World events**（ObjectListChanged/TerrainFeatureListChanged） | harvest/water 依赖自扫描，错过事件驱动优化 | P2 backlog |
| **Multiplayer**（PeerConnected 等） | 多人模式下 bot 行为未验证/未处理 | P2 backlog + 文档标注 |
| **Display/Menu events**（MenuChanged） | 弹窗阻塞 AI 无感知 | P1：MenuChanged 时 pause companion AI |
| **Harmony patch** | 未使用；当前无必需场景 | 不加（避免过度工程） |
| **UpdateKeys** | manifest 为空数组 | P0 修复（填 GitHub key） |

### 代码质量缺陷
- 文件轮询（0.5s tick 读 JSON）→ 可接受但应文档化；**File.ReadAllText 无 FileShare/重试**，Windows 上偶发 IOException
- `index.ts` 564 行单文件无拆分 → 迁移时按 tools/ 拆分
- 无 lint/test/CI 配置
- 无错误恢复：MCP 读到半旧 bridge_data 时无 staleness 检测（虽有 syncedAt 字段但 server 端未校验）

## 3. 方案对比

### 方案 A：pnpm workspace 单仓（推荐）
```
StardewValleyBuddy/
  pnpm-workspace.yaml
  package.json          (root, scripts)
  packages/
    mcp-server/         (TypeScript, src/tools/ 拆分)
    smapi-mod/          (C# + csproj + assets, 不受 pnpm 管)
  docs/api-audit.md
```
- 优点：单命令构建、结构清晰、后续可加 packages/cli
- 缺点：多一层 workspace 配置

### 方案 B：直接平铺复制（mcp-server/ + smapi-mod/ 同参考项目）
- 优点：迁移成本最低，diff 友好
- 缺点：无 workspace 复用空间，root package.json 语义模糊

**推荐方案 A**。smapi-mod 用 dotnet build（本机需 .NET 6+ SDK；csproj 引用 SMAPI dll 路径用环境变量可配）。
