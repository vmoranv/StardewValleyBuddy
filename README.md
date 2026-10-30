<div align="center">

# 🌾 StardewValleyBuddy

**给 Stardew Valley 招一个 AI 队友。**
Claude 等 AI 助手通过 MCP 直接操控游戏里的伙伴：挥镐、战斗、钓鱼、种田、下矿——像个真正的玩家一样玩 Stardew Valley。

<a href="#-演示"><img src="packages/smapi-mod/assets/Companion1_sprite_preview.png" width="420" alt="伙伴精灵预览"/></a>

`32 个 MCP 工具` · `6 种模式` · `单机存档` · `SMAPI 4.x` · `Stardew Valley 1.6+`

[快速开始](#-快速开始) · [工具一览](#-mcp-工具32-个) · [架构](#-架构) · [已知限制](#-已知限制)

</div>

---

## ✨ 它能做什么

- **Player 2 模式** —— AI 通过 `stardew_get_surroundings` 看地图、`stardew_move_to` 走位、`stardew_use_tool` 挥工具，你玩你的，它玩它的。
- **自主打工** —— `farm` / `mine` / `fish` / `follow` 四种自主模式，伙伴在后台自己干活，遇到怪物自动开打、低血自动撤退进食。
- **意图级作业** —— `stardew_work` 一条指令 = 清一片地 / 翻土播种浇水 / 收获 / 卖货。逐格决策（换工具、跳过已浇水、水壶空了去打水）由 mod 完成，AI 不需要看任何地块细节。
- **活着的伙伴** —— 有体力、会睡觉、遵守宵禁、掉落物自动收进指定箱子，状态跨存档保存，读档自动归队。
- **会聊天** —— `stardew_say` 让伙伴以角色身份说话（聊天行 + 气泡 + 肖像对话框），`stardew_talk_to` 与村民对话。

## 🎬 演示

```text
你: "帮我把农场西边那片荒地清了，木头石头收进门口的箱子"
AI → stardew_work(task=clear_area, location=Farm, area={center, radius})
AI → stardew_set_deposit_chest(chest="64,15")
伙伴: 换斧头砍树 → 换镐敲石头 → 换镰刀割草 → 捡起掉落 → 存入箱子
     ✅ cleared 47 tiles, collected 12 wood, 8 stone, 3 fiber
```

> 录屏与截图欢迎 PR 补充到这里。

## 🚀 快速开始

**前置条件**：[Stardew Valley](https://www.stardewvalley.net/) 1.6+ · [SMAPI](https://smapi.io/) 4.x · Node.js 20+ 与 pnpm（`corepack enable`）· .NET SDK

**1. 安装 SMAPI** —— 从 [smapi.io](https://smapi.io/) 下载安装到游戏目录。

**2. 编译并部署 mod**

```bash
cd packages/smapi-mod
export GAME_PATH="C:/Program Files/Steam/steamapps/common/Stardew Valley"
dotnet build   # 自动部署到 Mods/StardewMCPBridge/
```

**3. 构建工作区**

```bash
pnpm install && pnpm build
```

**4. 注册到 MCP 客户端**

<details open>
<summary><b>Claude Code</b></summary>

```bash
claude mcp add stardew -- node "<仓库绝对路径>/packages/mcp-server/build/index.js"
```

</details>

<details>
<summary>其他客户端（通用 JSON 配置）</summary>

```json
{
  "mcpServers": {
    "stardew": {
      "command": "node",
      "args": ["<仓库路径>/packages/mcp-server/build/index.js"],
      "env": {
        "STARDEW_BRIDGE_PATH": "<游戏路径>/Mods/StardewMCPBridge/bridge_data.json",
        "STARDEW_ACTION_DIR": "<游戏路径>/Mods/StardewMCPBridge/actions"
      }
    }
  }
}
```

仓库内自用（mod 与本仓库同盘同布局）时默认相对路径已生效，`env` 可省略。

</details>

**5. 验证**

```bash
pnpm smoke   # 应输出 smoke: tools/list = 32
```

启动游戏（务必经 `StardewModdingAPI.exe`）→ 读档 → 让 AI 调用 `stardew_spawn` 生成伙伴 → `stardew_set_mode` 选模式，开玩。

> [!TIP]
> 伙伴的存活状态、模式与自动战斗开关会写入存档目录（SMAPI Data API），读档自动重建，无需重新 spawn。

## 🛠️ MCP 工具（32 个）

<details open>
<summary><b>全局工具（14 个）—— 模式化控制</b></summary>

| 工具 | 说明 |
|------|------|
| `stardew_get_state` | 当前游戏状态——时间、天气、位置、玩家属性、伙伴状态、附近 NPC |
| `stardew_spawn` | 在玩家附近生成伙伴 |
| `stardew_follow` | 所有伙伴跟随玩家（遇怪自动战斗） |
| `stardew_stay` | 所有伙伴原地待命 |
| `stardew_farm` | 自动农场模式——浇水、收获、清理杂物 |
| `stardew_mine` | 战斗/挖矿模式——打怪、敲石头、找梯子下行 |
| `stardew_fish` | 钓鱼模式——找水、抛竿、自动起钩 |
| `stardew_water_all` | 立即浇灌当前地图所有未浇水作物 |
| `stardew_harvest_all` | 立即收获当前地图所有成熟作物 |
| `stardew_chat` | 发送一条游戏内聊天消息 |
| `stardew_say` | 伙伴以角色身份说话（聊天行 + 气泡，可选肖像对话框） |
| `stardew_warp` | 将所有伙伴传送到指定地点的指定图块 |
| `stardew_set_mode` | 设置指定伙伴模式（follow / farm / mine / fish / idle / player） |
| `stardew_action` | 在指定图块发送自定义动作（water / harvest / clear / hoe） |

</details>

<details open>
<summary><b>Player 模式工具（12 个）—— AI 即 Player 2</b></summary>

| 工具 | 说明 |
|------|------|
| `stardew_get_surroundings` | 伙伴视野内的图块、物体、作物、怪物、NPC（伙伴的"眼睛"） |
| `stardew_get_inventory` | 伙伴背包中的工具与物品 |
| `stardew_get_companion_state` | 指定伙伴详细状态——位置、地图、血量、体力、模式、上条指令结果 |
| `stardew_move_to` | 寻路走向指定图块（异步，下一 tick 查进度） |
| `stardew_warp_companion` | 将指定伙伴传送到指定地点（脱困用） |
| `stardew_face_direction` | 转向（up / right / down / left） |
| `stardew_use_tool` | 在指定图块使用工具（pickaxe / axe / hoe / watering_can / sword） |
| `stardew_interact` | 与图块上的物体、作物、箱子、梯子或机器交互 |
| `stardew_attack` | 用装备武器攻击最近的怪物 |
| `stardew_cast_fishing_rod` | 抛竿并在鱼咬钩时自动起钩（需靠近水） |
| `stardew_set_auto_combat` | 自动战斗开关：靠近→挥砍→低血撤退进食，全程自动走位 |
| `stardew_eat_item` | 吃背包食物恢复血量/体力 |

</details>

<details open>
<summary><b>作业、照料与对话工具（6 个）—— 一段意图，多次动作</b></summary>

| 工具 | 说明 |
|------|------|
| `stardew_work` | **意图级任务**：`clear_area`（清树石杂草，果树默认保护，自动拾取掉落）/ `plant`（翻土→播种→浇水）/ `water`（跳过已浇水，水壶空自动打水）/ `harvest` / `sell`（可加 `filter`）/ `chop` / `mine`。支持 bounds、`center/radius` 或显式 tiles（上限 500 格） |
| `stardew_set_deposit_chest` | 指定清扫掉落物自动入库的目标箱子；溢出流向下一个最近箱子，全满自动补箱 |
| `stardew_get_job` | 查询作业进度与最近终态摘要（完成/跳过/失败、阻挡原因、剩余格） |
| `stardew_cancel_job` | 取消当前作业 |
| `stardew_talk_to` | 与 NPC 对话：先走到相邻格，再返回对话内容 |
| `stardew_care` | `tend_animals`（抚摸+收蛋奶羊毛）/ `feed_animals` / `inspect_crops`（只读查成熟天数）/ `scan_tiles`（扫描对象、地形、作物、掉落物）/ `chest_deposit` / `chest_withdraw` |

**为什么是「意图」而不是「操作」**：AI 看不见地块，让它判断"这格是石头还是灌木"必然出错。`stardew_work` 只接收意图，逐格决策由 mod 完成——树换斧头、石头换镐、已浇水的跳过。每次操作只经过一次 MCP 调用，一棵树挥 5–10 下全在游戏主循环内完成。

</details>

## 🏗️ 架构

```
┌──────────────────┐  stdio / JSON-RPC  ┌──────────────┐    文件桥     ┌────────────┐
│    MCP 客户端     │◄──────────────────►│  mcp-server  │◄────────────►│ smapi-mod  │
│ (Claude Code 等)  │                    │  (Node.js)   │              │ (C#/.NET)  │
└──────────────────┘                    └──────────────┘              └─────┬──────┘
                                                                           │ SMAPI API
                                                                   ┌───────▼────────┐
                                                                   │ Stardew Valley │
                                                                   └────────────────┘
```

**文件桥**（解耦 MCP 进程与游戏进程，双向均为原子写入）：

- **指令下行**：mcp-server 写入 `actions/<时间戳>-<序号>.json`（一条指令一个不可变文件），mod 按文件名顺序消费后删除 —— 免锁队列，不丢指令、不重复执行。
- **状态上行**：mod 周期性写入 `bridge_data.json`（游戏状态、伙伴状态、player 模式下的视野与背包、上一条指令的执行结果），mcp-server 只读。

**伙伴系统**：每个伙伴 = 可见 NPC（自定义精灵、寻路）+ 隐藏 shadow Farmer（承载工具、战斗、钓鱼等真实游戏机制）。`player` 模式下自主 AI 关闭，AI 通过 MCP 工具调用成为 "Player 2"。

**仓库结构**：pnpm workspace 单仓多包 —— `packages/mcp-server`（TypeScript/Node）+ `packages/smapi-mod`（C#/.NET）+ `packages/logic`（伙伴行为逻辑）。

## 📋 自定义伙伴精灵

放入 `packages/smapi-mod/assets/`：4x4 精灵表（16x32 每帧）+ 配套立绘。

## ⚠️ 已知限制

> [!WARNING]
> - **仅单机验证**：shadow Farmer 刻意不注册进 `Game1.otherFarmers`（网络同步表，非 net 支撑的 Farmer 会导致 NPE），整条机制只在单机存档下验证过，多人支持不在当前路线图内。
> - **伙伴数量固定**：枚举限定 `Companion1` / `Companion2`，新增伙伴需同时扩展 mod 与工具枚举。
> - **文件桥本地进程通信**：mcp-server 与游戏须在同一台机器上。

## 🗺️ Roadmap

- [ ] 多人存档支持验证
- [ ] 可配置伙伴数量与自定义角色
- [ ] 更多自主模式（畜牧、采集、节日活动）

## 致谢

- [SMAPI](https://smapi.io/) —— Stardew Valley mod 框架
- [Model Context Protocol](https://modelcontextprotocol.io) —— AI 与工具的通用协议

## License

见 [LICENSE](LICENSE)。
