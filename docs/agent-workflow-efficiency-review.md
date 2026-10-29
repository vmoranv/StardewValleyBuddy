# 清理任务、寻路与除障提效复盘

日期：2026-10-29

范围：StardewValleyBuddy 游戏内清理任务复盘，以及寻路、除障、作业接口审查与实施。

状态：主要区域作业、安全边界、生命周期和扫描提效已落盘；2026-10-29 已完成 SMAPI 运行时区域扫描验收。

## 本次任务记录

目标以 Farm 图块 `(46,55)` 为中心，向四边各扩 8 格，按**闭区间正方形**解释：

- X：38–54
- Y：47–63
- 共 17 × 17 = 289 格

执行过程：

1. 第一次扫描时 Companion1 在 FarmHouse，扫描结果标记的地图也是 FarmHouse，不能代表 Farm。
2. 将伙伴传送至 Farm 后，正确扫描发现 7 格长草和一棵位于 `(48,47)` 的树。
3. 一次批量清理出现行走无进展，伙伴没有挥砍；该作业已取消。改为在目标附近逐格处理。
4. 长草清理后，树先经过多次直接挥斧仍未移除。将伙伴放在树西侧后，对单格 `(48,47)` 发起 `clear_area`；SMAPI 日志记录作业完成：done 1、swings 4。
5. 随后对目标格和整个方框复扫，结果分别为 `tiles: []`。作业结束时 Companion1 血量 100、体力约 96%，没有活动作业。
6. 部署本轮 shell 与 Logic 并重新进档后，使用 `stardew_care(task=scan_tiles, location=Farm, center=(46,55), radius=8)` 复验；扫描范围为 `38,47..54,63`，`tiles: []`，终态为 `0 non-empty tile(s)`。Companion1 血量 100、体力 100%，无活动作业。

**当时的验收边界：** 原始 `scan_tiles` 只检查 `objects`、`terrainFeatures` 和作物，没有覆盖掉落物块。因此当时的空扫描不能证明掉落物也为空。本轮已让区域扫描返回 `location.debris` 中的掉落物坐标和类型，并以临时文件加原子替换写入扫描结果。

## 项目已有基础

- `stardew_work` 已提供意图级作业：`clear_area`、`chop`、`mine` 等；mod 负责逐格选工具，单次范围上限 500 格。
- `CompanionJobRunner` 已有障碍插队、不可达重试、候选目标排序和作业进度。
- `NpcWalker` 用自有逐帧移动执行 A* 路径；这是为了避开多人主机上 vanilla PathFindController 不移动的问题。
- 文件桥为指令队列与游戏状态解耦，现有基础适合继续收敛成一个稳定的区域作业流程。

主要提效方向是统一地图、范围、任务生命周期和结果语义，而不是再增加一组低层挥斧工具。

## 方案与实施状态

### P1：统一区域请求与清理入口

已完成：`stardew_work` 与 `scan_tiles` 共用 `location` 和区域形式（闭区间 bounds、中心正方形、显式 tiles），两边都限制为最多 500 格。旧 work 坐标和旧 scan chest/filter 范围仍兼容。区域扫描按请求的地图解析；地图不存在或缺少范围时返回错误。

统一结构示例：

```json
{
  "companion": "Companion1",
  "location": "Farm",
  "area": {
    "center": { "x": 46, "y": 55 },
    "radius": 8,
    "shape": "square"
  },
  "include": ["objects", "grass", "trees", "weeds"],
  "exclude": ["crops", "fruitTrees", "resourceClumps"],
  "dryRun": true
}
```

半径 8 的方框展开为 `[x-8,x+8] × [y-8,y+8]`，端点包含在内。MCP 参数校验会拒绝非法、负坐标、冲突或超过 500 格的范围。扫描与清理仍是两个异步工具调用，尚未做单次预览/确认/执行/复扫工作流；可以先 `scan_tiles`，再用相同 area 调用 `clear_area`，完成后再次扫描。

仍需区分两个清理语义：`stardew_action clear` 调用单格 `ClearDebris`；`stardew_work clear_area` 逐格处理可清理对象与地形。本轮已默认保护果树；作物和资源石块不属于 `clear_area` 目标。

### P1：统一作业、传送和取消的生命周期，并守住范围

已完成：work/care/move/warp/cancel 共用停止流程，清理 job、care、两个 walker、NPC controller 和 refill 状态。工作任务保留请求格集合，路线障碍只会在 `clear_area` 且障碍属于原请求范围时插入队列；范围外阻挡记入 `blockedBy` 并跳过受阻格。每个作业共享最多 40 次绕行。

### P1：增加体力预算，所有超时改为稳定时间

已完成：增加 15 分钟墙钟总超时、每格最多 32 次挥砍和 10 点体力保留；作业终止会保留剩余坐标。已有的局部卡住/冷却计数仍按 tick 工作，后续可基于运行时数据再迁移为时间阈值。

### P1：保留任务终态和结构化回执

部分完成：job 和 care 的快照在完成、取消或中止后继续保留一个最近终态，包含地图、请求边界、数量、失败明细、阻挡坐标、停止原因、时间戳和剩余坐标。仍未做按 ID 查询或多条历史回执；状态桥会提供最近一条结果。

### P2：复用寻路结果，使用真实路径成本

部分完成：已移除每格完成后对最多 15 个候选目标的重复 A* 探测；障碍候选先按几何代价排序，只对实际选中的清障目标规划一次路径。绕湖/窄道仍使用欧氏距离近似，40 次预算仍由整个作业共享。`NpcWalker` 已按完整 NPC 碰撞框验证每一步和最终航点，阻挡时记录 `BlockedTile`。

### P2：扫描补上掉落物，并原子发布扫描结果

已完成：`scan_tiles` 纳入地图对象、地形/作物和掉落物块，按区域输出位置/类型；结果先写 `.tmp` 再原子替换 `scan_result.json`。清理作业仍只会拾取伙伴附近的掉落物。

## 工具兼容约束

项目当前 MCP 工具总数固定为 31，README 工具表和 `pnpm smoke` 需要一致。优先扩展现有 `stardew_work`、`stardew_care` 的共享区域参数，避免仅为统一清理流程就新增一批重复低层工具；若工具清单确需调整，必须同步 README 和 smoke 的断言，并按项目约定保持总数为 31。

## 验收与未完成项

验收结果：

- `center=(46,55), radius=8, shape=square` 必须展开为 Farm 的 `x=38..54, y=47..63`，共 289 格。
- 统一区域范围通过 bounds/center-square/tiles 编码；MCP 与游戏侧均维持 500 格上限，工具总数仍为 31。
- 传送、任务替换、移动和取消会同步清理旧任务与路线；超范围障碍只作为阻挡结果报告。
- 果树默认保护；体力余量、32 次单格挥砍、15 分钟总时限和终态剩余格回执已加入。
- 编译与 smoke 结果见下方；SMAPI 运行时已加载本轮 shell/Logic，复扫 289 格得到 `tiles: []`，Companion1 保持存活且没有活动作业。

尚未实现：单次 dry-run 预览/确认工作流、按 ID 查询多条历史回执、连通区域级绕行预算，以及基于真实 A* 成本排序。清理/扫描仍由两次以上调用组成。

检查命令：

```text
pnpm build
pnpm smoke                 # expected: tools/list = 31
dotnet build packages/smapi-mod/StardewValleyBuddy.csproj -p:GamePath=<game-directory> -p:EnableModDeploy=false -p:EnableModZip=false
dotnet build packages/logic/SdvbLogic.csproj -p:GamePath=<game-directory> -p:OutDir=<temporary-directory>
```

## 相关实现

- [CompanionJobRunner.cs](../packages/logic/src/CompanionJobRunner.cs)：作业启动、逐格决策、调度、障碍清理与终态。
- [NpcWalker.cs](../packages/logic/src/NpcWalker.cs)：A* 路线的逐帧执行与移动碰撞。
- [CompanionCareRunner.cs](../packages/logic/src/CompanionCareRunner.cs)：区域扫描与文件输出。
- [BotManager.cs](../packages/smapi-mod/BotManager.cs)：warp、work、care 等指令分发。
- [actions.ts](../packages/mcp-server/src/tools/actions.ts)：MCP 作业和扫描参数定义。
