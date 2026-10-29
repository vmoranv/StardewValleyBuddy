# StardewValleyBuddy

Stardew Valley 的 AI 伴侣 —— 通过 [MCP（Model Context Protocol）](https://modelcontextprotocol.io)让 Claude 等 AI 助手直接操控游戏中的伙伴角色：移动、使用工具、战斗、钓鱼、种田、探索矿洞，像个真正的玩家一样玩 Stardew Valley。也支持 `follow` / `farm` / `mine` / `fish` 等自主模式，让伙伴在后台自己干活。

## 架构

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

- **指令下行**：mcp-server 写入 `actions/<时间戳>-<序号>.json`（一条指令一个不可变文件），mod 按文件名顺序消费后删除 —— 免锁队列，不会丢指令、不会重复执行。
- **状态上行**：mod 周期性写入 `bridge_data.json`（游戏状态、伙伴状态、player 模式下的视野与背包、上一条指令的执行结果），mcp-server 只读。

**伙伴系统**：每个伙伴 = 可见 NPC（自定义精灵、寻路）+ 隐藏 shadow Farmer（承载工具、战斗、钓鱼等真实游戏机制）。`player` 模式下自主 AI 关闭，AI 通过 MCP 工具调用成为 "Player 2"。

## 安装与部署

### 前置条件

- [Stardew Valley](https://www.stardewvalley.net/) 1.6+
- [SMAPI](https://smapi.io/) 4.x
- Node.js 20+ 与 pnpm（`corepack enable` 即可获得）
- .NET SDK（编译 mod 用）

### 1. 安装 SMAPI

从 https://smapi.io 下载并安装到游戏目录。

### 2. 编译并放置 mod

```bash
cd packages/smapi-mod
export GAME_PATH="C:/Program Files/Steam/steamapps/common/Stardew Valley"
dotnet build   # 构建并自动部署到 Mods/StardewMCPBridge/
```

自定义伙伴精灵放入 `packages/smapi-mod/assets/`（4x4 精灵表，16x32 每帧；配套立绘）。详见 `packages/smapi-mod/` 内文档。

### 3. 构建工作区

```bash
pnpm install
pnpm build
```

### 4. 注册到 MCP 客户端

JSON 配置示例（Claude Code `settings.json` / 通用 `mcpServers` 配置）：

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

仓库内自用（mod 与本仓库同盘同布局）时，默认相对路径 `../../smapi-mod/` 已生效，`env` 可省略。

Claude Code 一行注册：

```bash
claude mcp add stardew -- node "D:/coding/StardewValleyBuddy/packages/mcp-server/build/index.js"
```

需显式指定桥接文件路径时：

```bash
claude mcp add stardew \
  -e STARDEW_BRIDGE_PATH="C:/Program Files/Steam/steamapps/common/Stardew Valley/Mods/StardewMCPBridge/bridge_data.json" \
  -e STARDEW_ACTION_DIR="C:/Program Files/Steam/steamapps/common/Stardew Valley/Mods/StardewMCPBridge/actions" \
  -- node "D:/coding/StardewValleyBuddy/packages/mcp-server/build/index.js"
```

### 5. 验证

```bash
pnpm smoke   # 应输出 smoke: tools/list = 31
```

启动游戏（经 SMAPI）→ 读档 → `stardew_spawn` 生成伙伴 → `stardew_set_mode` 选择模式。

> **伙伴状态自动保存**：伙伴的存活状态、模式（follow/farm/mine/fish/idle/player）与自动战斗开关会写入存档目录下的 `companion_state.json`（SMAPI Data API），并在保存、切换模式、返回标题时更新。下次读档时自动重建，无需重新 spawn。

## MCP 工具（31 个）

### 全局工具（14 个）—— 模式化控制

| 工具 | 说明 |
|------|------|
| `stardew_get_state` | 获取当前游戏状态——时间、天气、位置、玩家属性、伙伴状态、附近 NPC |
| `stardew_spawn` | 在玩家附近将伙伴生成到游戏世界 |
| `stardew_follow` | 让所有伙伴跟随玩家（附近有怪物时自动战斗） |
| `stardew_stay` | 让所有伙伴停下并留在原地 |
| `stardew_farm` | 开启自动农场模式——浇水、收获、清理杂物 |
| `stardew_mine` | 开启战斗/挖矿模式——打怪、敲石头、找梯子下行 |
| `stardew_fish` | 开启钓鱼模式——找水、抛竿、自动起钩 |
| `stardew_water_all` | 立即浇灌当前地图所有未浇水作物 |
| `stardew_harvest_all` | 立即收获当前地图所有成熟作物 |
| `stardew_chat` | 在游戏中发送一条聊天消息 |
| `stardew_say` | 让某个伙伴以角色身份说话——聊天行 + NPC 气泡，可选弹出带肖像的对话框 |
| `stardew_warp` | 将所有伙伴传送到指定地点的指定图块 |
| `stardew_set_mode` | 设置指定伙伴的模式（follow / farm / mine / fish / idle / player） |
| `stardew_action` | 在指定图块发送自定义动作（water / harvest / clear / hoe） |

### Player 模式工具（12 个）—— 直接操控（AI 即 Player 2）

| 工具 | 说明 |
|------|------|
| `stardew_get_surroundings` | 获取伙伴视野内的图块、物体、作物、怪物、NPC（伙伴的"眼睛"，需 player 模式） |
| `stardew_get_inventory` | 获取伙伴背包中的工具与物品 |
| `stardew_get_companion_state` | 获取指定伙伴的详细状态——位置、地图、血量、体力、模式、上一条指令结果 |
| `stardew_move_to` | 通过寻路让伙伴走向指定图块（异步，下一 tick 查看进度） |
| `stardew_warp_companion` | 将指定伙伴传送到指定地点 |
| `stardew_face_direction` | 让伙伴转向（up / right / down / left） |
| `stardew_use_tool` | 在指定图块使用工具（pickaxe / axe / hoe / watering_can / sword） |
| `stardew_interact` | 与图块上的物体、作物、箱子、梯子或机器交互 |
| `stardew_attack` | 用装备武器攻击最近的怪物 |
| `stardew_cast_fishing_rod` | 抛竿并在鱼咬钩时自动起钩（需靠近水） |
| `stardew_set_auto_combat` | 开关自动战斗：靠近 → 挥砍 → 低血撤退吃东西，全程自动走位 |
| `stardew_eat_item` | 吃背包中的食物恢复血量/体力 |

### 作业与对话工具（4 个）—— 一段意图，多次动作

砍一棵树要挥 5–10 下、锄一片地要几十格。这些操作用**一条指令**表达意图，由 mod 自己持续执行到完成为止：

| 工具 | 说明 |
|------|------|
| `stardew_work` | **意图级任务**。`task` 可选：`clear_area`（树/石/杂草/灌木一律清掉）、`plant`（翻土→播种→浇水）、`water`（跳过已浇水，水壶空了自动去打水）、`harvest`（作物/灌木/果树）、`sell`（投入出货箱，可加 `filter`）、`chop` / `mine`（显式覆盖）。范围用 `(x,y)`、`(x1,y1,x2,y2)` 或 `tiles:[[x,y],...]`，上限 500 格 |
| `stardew_get_job` | 查询当前作业进度：已完成/跳过/失败数量、当前格、跳过原因 |
| `stardew_cancel_job` | 取消当前作业 |
| `stardew_talk_to` | 与 NPC 对话：先走到相邻格，再返回对话内容（不在范围内时会提示再调用一次） |
| `stardew_care` | 建筑/动物/背包任务：`tend_animals`（抚摸+收蛋奶羊毛）、`feed_animals`（补饲料）、`inspect_crops`（只读：每株作物还有几天成熟）、`chest_deposit` / `chest_withdraw`（箱子存取） |

**为什么是「意图」而不是「操作」**：AI 看不见地块，让它判断"这格是石头还是灌木"必然出错。`stardew_work` 只接收意图（清地、种地、浇水、收获、卖货），**逐格决策由 mod 完成**——树换斧头、石头换镐、杂草换镰刀、已浇水的跳过、水壶空了自动寻路打水。AI 不需要知道任何底层细节。

每次操作只经过一次 MCP 调用；一棵树要挥 5–10 下，全在游戏主循环内完成。

## 已知限制

- **单机 shadow farmer 路线**：伙伴由 NPC + 隐藏 shadow Farmer 实现，且 shadow Farmer 刻意**不**注册进 `Game1.otherFarmers` —— 该表是网络同步表，非 net 支撑的 Farmer 会导致 `Multiplayer.updateRoots()` 抛 NPE。整条机制只在单机存档下验证过。
- **无多人支持验证**：未在联机存档中做过任何测试，多人支持不在当前路线图内。
- **伙伴数量固定**：枚举限定 `Companion1` / `Companion2`，新增伙伴需同时扩展 mod 与工具枚举。

## License

GPLv3，见 [LICENSE.txt](LICENSE.txt)。
