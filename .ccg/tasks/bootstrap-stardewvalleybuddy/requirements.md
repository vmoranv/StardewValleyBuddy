# 需求：Bootstrap StardewValleyBuddy

## 目标
在 D:\coding\StardewValleyBuddy 新建 repo，参考 D:\coding\StardewValley-MCP 构建基础骨架，迁移关键代码，并修缮已知缺陷。

## 结构化需求
1. **初始化骨架**
   - pnpm init（Node/MCP server 部分：TypeScript + @modelcontextprotocol/sdk）
   - 目录结构：`mcp-server/` + `smapi-mod/`（与参考项目对应）
   - 基础配置：tsconfig、.gitignore、README
2. **代码迁移**
   - 关键代码：ModEntry / BotManager / CompanionAI / CompanionActions / CompanionFarmer / CompanionNPC / SurroundingsScanner / BotFarmer / manifest.json / csproj / assets
   - MCP server：index.ts（26 个 stardew_* 工具）
   - pnpm workspace 化可选（当前参考项目 mcp-server 独立 npm 项目）
3. **缺陷修缮：SMAPI API 覆盖审计**
   - 审计 mod 是否覆盖了 SMAPI/游戏的所有相关 API 面（events、Data API、command、save/load、multiplayer 等）
   - 识别参考项目明显缺失的能力（如 console command、save/load 持久化、Multiplayer、Input、Display events、Harmony patch 等）
   - 迁移时直接修复，或在 plan 中列为待办

## 约束
- 保留参考项目 GPLv3（LICENSE.txt）许可与来源标注
- 不破坏参考项目的运行方式（SMAPI 4.0+，.NET 6）

## 验收标准
- [ ] repo 可 `pnpm install && pnpm build`（mcp-server 编译通过）
- [ ] smapi-mod 可用 dotnet build 编译（如本机有 SDK；否则至少结构与 csproj 正确）
- [ ] API 覆盖审计报告产出，缺失项有明确修复或 backlog
- [ ] README 说明架构与使用方式

## 需求完整性评分
- 目标明确性 3/3，预期结果 3/3，边界范围 2/2，约束条件 1/2（pnpm 结构未定、审计深度未定）
- **总分 9/10 ≥ 7 → 继续**
