# Ralph Loop Round 1 审查报告

> 外部模型（codex/grok/kimi）未配置，子代理审查输出异常 → 由 Claude 直接完成审查。
> 审查方式：源码核对 + 路径解析实测 + `pnpm build` + `pnpm smoke`。

## 验证结果
- `pnpm build`（mcp-server, tsc strict）✅ 通过
- `pnpm smoke` ✅ `tools/list = 26`，exit 0
- 源 vs 迁移代码 diff：8 个 .cs 中 7 个仅差文件头注释，BotManager 差异全部为新增修缮（PauseAI / Cleanup 加固）✅
- assets 6→6 全量 ✅
- MCP 26 工具 ↔ mod `ProcessAction` case 逐一对齐，无「只声明不实现」✅

## Critical（已修复）
| # | 文件 | 问题 | 修复 |
|---|---|---|---|
| C1 | `packages/mcp-server/src/bridge.ts:10,12` | 默认路径 `../../../smapi-mod/` 从 `packages/mcp-server/build/` 解析到 `<repo>/smapi-mod/`（不存在），实际应为 `<repo>/packages/smapi-mod/`。未设环境变量时 `get_state` 永远返回 "Bridge file not found" | 改为 `../../smapi-mod/`，已重建并实测解析为 `D:\coding\StardewValleyBuddy\packages\smapi-mod\bridge_data.json` |
| C2 | `AGENTS.md:25` | 文档称「构建会自动把 mod 部署到 Mods/StardewMCPBridge/」，实际 csproj 已移除 ModBuildConfig（需手动拷贝），且目录名应为 StardewValleyBuddy | 改为手动拷贝步骤 + 正确路径 |

## Warning（未改，接受）
| # | 文件 | 问题 | 判断 |
|---|---|---|---|
| W1 | `packages/smapi-mod/ModEntry.cs:420` `WithIoRetry` | `Thread.Sleep(50)` 在游戏主线程（UpdateTicked）阻塞，最坏 100ms/文件 × N 指令文件 → 帧卡顿 | 触发条件仅 Windows 文件锁竞态，极少发生；改为异步会引入跨线程访问游戏对象的风险，收益不抵风险。保留，注释已说明 |
| W2 | `packages/smapi-mod/ModEntry.cs:323` GMCM `dynamic api` | 旧版 GMCM 若缺少 `AddTextOption` 等签名会在绑定时抛异常 | 已有外层 try/catch 降级到 config.json，行为安全 |
| W3 | `packages/smapi-mod/ModEntry.cs:149` OnSaveLoaded | 读档直接 Cleanup 全部 companion，不会自动恢复；玩家需重新 spawn | **已修复（Round 2）**：新增 `CompanionState.cs` + `BotManager.SaveState()/RestoreState()`，通过 `helper.Data.WriteJsonFile("companion_state.json")` 持久化 name/mode/autoCombat；读档时重建（先扫残留再 spawn）。SaveState 挂在 `OnSaving`、`OnReturnedToTitle`、`SetAllMode`、`set_mode`、`set_auto_combat` 五处 |
| W4 | `packages/mcp-server/src/bridge.ts:31` | 写 actions 前用 `renameSync`，若 mod 正在 `GetFiles` 遍历瞬间可能出现「文件已列出但已删除」→ mod 侧已用 try/catch + continue 兜底 | 可接受 |

## Info
- I1: `bridge.ts` 用 `process.env` 在模块加载时求值，运行期改环境变量不生效——MCP server 进程生命周期内不会变，符合预期。
- I2: `manifest.json` `UpdateKeys` 指向 `Github:vmoranv/StardewValleyBuddy`，仓库若未发布需自行改。
- I3: `pnpm-lock.yaml` 已生成，CI 用 `--frozen-lockfile` 可行。

## 许可结论（用户确认）
- 本项目：**GPL-3.0-or-later**（root LICENSE.txt、两个 package.json `license` 字段、8 个 .cs 文件头）
- 来源标注：源 StardewValley-MCP 为 Apache-2.0，各文件头已注明源 URL + 本文件许可
- `packages/smapi-mod/LICENSE.txt` 已同步为 GPLv3 全文（原为 Apache-2.0，与二改声明冲突，已替换）

## 结论
Critical 2 项已修复；Warning 4 项评估后保留（均有兜底或属已确认的设计取舍）。可进入最终验收。

## Round 3：真实游戏环境编译验证（2026-10-28）
环境：Steam 安装 `D:/game/steam/steamapps/common/Stardew Valley`，游戏 1.6.15.24356，SMAPI 4.x，dotnet SDK 10.0.201。

| # | 级别 | 问题 | 修复 |
|---|---|---|---|
| C3 | Critical | dev-3 用显式 `Reference` 替换了 ModBuildConfig，并假设 SMAPI 程序集在 `smapi-internal/SMAPI.dll`，实际 1.6/4.x 是根目录 `StardewModdingAPI.dll` → 36 个 CS0246 | 恢复 `Pathoschild.Stardew.ModBuildConfig 4.1.1`；保留 GamePath 解析链与 CheckGamePath 快速失败目标 |
| C4 | Critical | 即便 SMAPI 引用修好，手写引用集仍缺 1.6 分部程序集里的 `StardewValley.GameData.Characters`（CS0234） | 同上，ModBuildConfig 自动解析完整程序集集 |
| C5 | Critical | dev-4 使用不存在的 `helper.Console.AddCommand`（CS1061） | 改为 `helper.ConsoleCommands.Add(name, description, handler)` |

**结果**：`dotnet build -p:GamePath="..."` → **0 警告 0 错误**，ModBuildConfig 自动部署到 `Mods/StardewValleyBuddy/`（dll + manifest + assets），并生成 `StardewValleyBuddy 0.4.0.zip`。

**教训**：dev-3 的"用空程序集冒充 dll 验证 HintPath"证伪了真实 API 面——程序集文件名与类型覆盖面都必须用真实游戏验证。
