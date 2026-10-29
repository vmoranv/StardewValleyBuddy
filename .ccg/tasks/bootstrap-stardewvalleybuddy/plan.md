# 实施计划：Bootstrap StardewValleyBuddy

方案：**A — pnpm workspace 单仓**（见 analysis.md §3）

## Layer 1（无依赖，可并行）

### T1: 仓库骨架
- `package.json`（root，scripts: build/dev，packageManager: pnpm）
- `pnpm-workspace.yaml`（packages: ["packages/*"]）
- `.gitignore`（node_modules/build/bin/obj/bridge_data.json）
- `LICENSE.txt`（GPLv3，保留来源标注）
- `README.md`（架构图、安装链路：SMAPI → mod → pnpm build → MCP client 配置、工具清单表、已知限制）

### T2: packages/mcp-server（迁移 + 拆分）
- 从参考项目复制 `mcp-server/`，改为 pnpm 包
- `src/index.ts` 拆分：`src/tools/`（每域一文件：state/companion/actions/inventory）、`src/bridge.ts`（bridge_data.json + actions 队列 I/O）、`src/index.ts`（server 装配）
- 修复：读取 bridge_data 时校验 `syncedAt` staleness（>5s 报 stale 警告）
- `pnpm install && pnpm build` 通过（tsc strict）

### T3: packages/smapi-mod（迁移）
- 复制 8 个 .cs + manifest.json + csproj + assets/
- csproj：SMAPI 引用路径改为可配置（环境变量 `SMAPI_GAME_PATH`，默认 Steam 常见路径）
- manifest：填 UpdateKeys（GitHub 源）、UniqueID 改为 `vmoranv.StardewValleyBuddy`

## Layer 2（依赖 Layer 1 对应部分）

### T4: mod 缺陷修缮（P0）
- ModEntry：订阅 `SaveLoaded`（读档重建 companions）/ `Saving`；UpdateTicked 内文件 I/O 加 try/catch + IOException 重试
- manifest UpdateKeys（在 T3 基础上）

### T5: mod 缺陷修缮（P1）
- 新增 `ModConfig.cs`（bridgePath/companionNames/enableAI 开关）+ `helper.ReadConfig/WriteConfig`
- GMCM 集成（软依赖，检测到该 mod 才注册）
- 订阅 `MenuChanged`：菜单打开时 pause companion AI
- `helper.Console.AddCommand`：`svb_status` / `svb_cleanup` 调试命令

### T6: 文档
- `docs/api-audit.md`：完整审计表（analysis.md 扩展版，含 P2 backlog：Input/World/Multiplayer）
- CONTRIBUTING.md 简版

## 测试策略
- mcp-server：`pnpm build`（tsc strict 零错误）+ 手动 smoke：node 启动 server 列出 26 工具
- smapi-mod：`dotnet build` 通过（若本机有 .NET SDK；否则结构校验 + 用户在游戏环境验证）
- 审计交付：docs/api-audit.md

## 风险与缓解
| 风险 | 缓解 |
|---|---|
| 本机无 SMAPI dll → csproj 编译失败 | csproj 用条件引用 + 文档说明；不阻塞验收（结构正确即可） |
| companion 状态恢复逻辑复杂（T4） | 先最小实现：读档后仅清理 + 提示重新 spawn，避免写复杂迁移 |
| MCP SDK 版本旧（^1.0.1） | 升级到当前最新 1.x，按需要小改 API 用法 |

## 范围外（backlog，不做）
- Input/World/Multiplayer events（docs/api-audit.md 中记录）
- Harmony patch
- 自动化测试框架（本仓无前端，收益低）

## 追加：NagiBridge-Remix / Junimo-MCP 调研结论（2026-10-28）
- NagiBridge-Remix（pendel001，PolyForm Noncommercial）：HTTP bridge 架构（7842/7843）、farmhand 联机路线。**代码不可复制**，架构记入 backlog
- Junimo-MCP（mlkgrnt，OCaml，37 工具）：「不假成功」回读纪律、AGENTS.md 部署、mock 冒烟测试 —— 三者采纳为低成本改进
- 修订：T1 增加 AGENTS.md + CI workflow；T2 增加 mock 冒烟测试骨架；T6 docs/api-audit.md 增加架构演进节（HTTP bridge、不假成功、farmhand 路线对比）
