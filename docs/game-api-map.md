# 游戏 API 能力矩阵

> 基于本机 Stardew Valley **1.6.15** 程序集反编译生成（`ilspycmd`），非凭记忆。
> 复现：`tools/api.sh <Namespace.Type> [pattern]`，产物缓存在 `.cache/api/`。
>
> 统计口径：`Stardew Valley.dll` 共 **1449** 个 `StardewValley.*` 类型。已排除编译器生成的
> `<>c` 类后，关键玩法域为：Tools 15、Monsters 32、TerrainFeatures 13、Buildings 12、Objects 37。

## 覆盖率总览（实测，2026-10-28）

统计方式：`grep -vE "<>|DisplayClass|<>c"` 去掉编译器生成类后，逐个比对源码引用。

| 玩法域 | 全部类型 | 已用 | 已用清单 | 覆盖率 |
|---|---|---|---|---|
| Tools | 15 | 6 | Axe, Pickaxe, Hoe, WateringCan, MeleeWeapon, FishingRod | 40% |
| TerrainFeatures | 13 | 6 | Tree, FruitTree, HoeDirt, Bush, Grass, ResourceClump | 46% |
| Monsters | 32 | 1 | 通用 `Monster`（不分种类） | 3% |
| Buildings | 12 | 2 | Building（遍历/indoors）、AnimalHouse | 17% |
| Objects | 37 | 1 | Chest（存取已接） | 3% |
| 农业 | Crop / FarmAnimal | 3 | Crop（GetData/phaseDays/currentPhase）、FarmAnimal（pet/GetHarvestType）、AnimalHouse | — |
| Locations | 60 | 2 | MineShaft + 通用 GameLocation | 3% |
| Menus | 143 | 0 | — | 0% |

### 已接入（2026-10-28 补充）

| 能力 | 实现 | 用到的 API |
|---|---|---|
| 养殖：抚摸 + 收蛋/奶/羊毛 | `stardew_care task=tend_animals` | `Building.indoors`、`AnimalHouse`、`GameLocation.Animals`、`FarmAnimal.pet()`、`GetHarvestType()`、`MilkPail`、`Shears` |
| 养殖：补饲料 | `task=feed_animals` | `AnimalHouse.feedAllAnimals()` |
| 作物进度查询 | `task=inspect_crops` | `Crop.GetData()`、`currentPhase`、`phaseDays`、`HoeDirt.state` |
| 箱子存取 | `task=chest_deposit` / `chest_withdraw` | `Chest.GetItemsForPlayer()`、`IInventory : IList<Item>` |
| 意图级作业 | `stardew_work` | `HoeDirt`、`Crop`、`WateringCan.WaterLeft/waterCanMax`、`Farm.shipItem`、`ResourceClump`、`Bush`、`Grass`、`FruitTree` |

> 1.6 注意：`Barn` / `Coop` 类已废弃（仅用于旧存档），畜舍一律是普通 `Building`，
> 内景才是 `AnimalHouse : GameLocation`。动物在这些内景的 `Animals` 表里，不在建筑上。

### 剩余缺口

| 影响 | 缺口 | 涉及 API |
|---|---|---|
| 质量 | 32 种怪物当同一种打 | `RockCrab`、`Shooter`、`Mummy` 等 |
| 质量 | 出货箱容量/位置未检查 | `ShippingBin` |
| 遗漏 | 树液采集器 | `Tree.tapped` |
| 遗漏 | 巨型作物只当障碍 | `GiantCrop` |
| 遗漏 | 商店购买、邮件 | `Menus` 相关 |

> **不追求全覆盖**：1449 个类型中大量是 UI 内部类与事件参数，全接收益极低。按游玩循环补即可。

## 1. 工具 Tools

| 类型 | 关键成员 | 状态 |
|---|---|---|
| `Axe` | `DoFunction` | ✅ 已接（chop 作业） |
| `Pickaxe` | `DoFunction` | ✅ 已接（mine 作业） |
| `Hoe` | `DoFunction` | ✅ 已接 |
| `WateringCan` | `DoFunction`、`WaterLeft` | ⚠️ 未检查水量，空罐时会静默失败 |
| `MeleeWeapon` | `DoDamage(…, int power, Farmer who)`、`NetInt speed` | ✅ 已接（战斗状态机） |
| `FishingRod` | `CastFishingRod`、`TickFishingRod` | ✅ 已接 |
| `Slingshot` | `DoFunction` | ❌ 未接（远程武器） |
| `Pan` | `DoFunction` | ❌ 未接（淘金） |
| `MilkPail` | `DoFunction` | ❌ 未接（挤奶） |
| `Shears` | `DoFunction` | ❌ 未接（剪毛） |
| `Wand` / `Raft` / `Lantern` | — | ❌ 暂不需要 |

## 2. 地形与资源 TerrainFeatures

| 类型 | 关键成员（真实签名） | 状态 |
|---|---|---|
| `Tree` | `NetFloat health`、`NetBool stump`、`NetInt growthStage`、`NetBool tapped`、`NetBool hasMoss` | ✅ 砍伐已接；⚠️ 树桩/长满苔藓/已挂树液采集器未处理 |
| `HoeDirt` | `const int watered = 1`、`NetInt state`、`readyForHarvest()` | ✅ 锄地/浇水已接；⚠️ 未区分已浇水（会重复消耗体力） |
| `FruitTree` | `NetInt growthStage`、`NetInt daysUntilMature`、`NetInt struckByLightningCountdown` | ❌ 未接（果树结果/摇晃） |
| `Bush` | `readyForHarvest()`、`inBloom()`、`GetShakeOffItem()` | ❌ 未接（浆果灌木） |
| `Grass` | `NetByte grassType`、`NetInt numberOfWeeds` | ❌ 未接（割草喂动物） |
| `ResourceClump` | `NetInt parentSheetIndex`、`NetFloat health` | ❌ 未接（大石头/原木，需多次砍且需斧/镐等级） |
| `GiantCrop` | — | ⚠️ 只作为障碍物识别 |
| `Flooring` / `CosmeticPlant` / `Tent` | — | ❌ 暂不需要 |

## 3. 农业 Crop

| 成员 | 说明 | 状态 |
|---|---|---|
| `NetIntList phaseDays` | 每阶段天数 | ❌ 未读 |
| `NetInt currentPhase` / `NetInt dayOfCurrentPhase` | 生长进度 | ❌ 未读 |
| `NetBool fullyGrown` | 是否成熟 | ⚠️ 间接（HoeDirt.readyForHarvest） |
| `NetString indexOfHarvest` | 产物 ID | ❌ 未读 |
| `CropData GetData()` | 作物数据（能否复收等） | ❌ 未读 |

**缺失能力**：播种、施肥、判断剩余生长天数（用于规划"种什么"）、识别可复收作物。

## 4. 怪物 Monsters（32 种）

当前统一处理为「最近的可攻击目标」，未区分行为差异：

| 类别 | 代表 | 需要特殊处理 |
|---|---|---|
| 近战冲锋 | `GreenSlime` `ShadowBrute` `DustSpirit` | 无 |
| 飞行 | `Bat` `Fly` `Serpent` | 移动快，需预判拦截 |
| 远程 | `Shooter` `SquidKid` `BlueSquid` | 应绕后或近身 |
| 护甲 | `RockCrab`（先打壳）、`Duggy`（钻地） | 正面攻击无效 |
| 免疫/特殊 | `Mummy`（需炸弹）、`Ghost` | 物理伤害低效 |

❌ 全部未做区分。

## 5. 建筑 Buildings

| 类型 | 关键成员 | 状态 |
|---|---|---|
| `ShippingBin` | 投递物品卖钱 | ❌ 未接 |
| `Barn` / `Coop`（`AnimalHouse`） | `NetLongList animalsThatLiveHere`、`NetInt animalLimit`、`feedAllAnimals()` | ❌ 未接 |
| `FishPond` | `NetString fishType`、`int FishCount`、`NeedsBait` 等 | ❌ 未接 |
| `JunimoHut` / `Mill` / `Stable` | — | ❌ 未接 |
| `GreenhouseBuilding` | — | ❌ 未接 |

## 6. 养殖 FarmAnimal

| 成员 | 状态 |
|---|---|
| `NetInt age` / `NetInt fullness` / `NetString currentProduce` | ❌ 未读 |
| `isBaby()` / `GetHarvestType()` | ❌ 未接（判断能否挤奶/剪毛/捡蛋） |

## 7. 对象 Objects

| 类型 | 状态 |
|---|---|
| `Chest` | `Inventory Items`、`AddItem` | ⚠️ 只识别，无存取 |
| 篝火/机器/酿造桶等 | ❌ 未接 |

---

## 建议接入顺序（按游玩收益排序）

| 优先级 | 能力 | 涉及类型 | 预估 |
|---|---|---|---|
| **P0** | 播种（含作物进度查询） | `Crop`、`CropData`、`HoeDirt` | 中 |
| **P0** | 卖货（ShippingBin） | `ShippingBin` | 小 |
| **P0** | 浇水去重 + 水壶水量检查 | `HoeDirt.state`、`WateringCan.WaterLeft` | 小 |
| **P1** | 采集细分：大石/原木、灌木浆果、割草 | `ResourceClump`、`Bush`、`Grass` | 中 |
| **P1** | 养殖：喂食、收集产品、挤奶剪毛 | `AnimalHouse`、`FarmAnimal` | 中大 |
| **P1** | 战斗细分：护甲怪、远程怪 | `RockCrab`、`Shooter` 等 | 中 |
| **P2** | 果树 / 鱼塘 / 箱子 | `FruitTree`、`FishPond`、`Chest` | 中 |
| **P2** | 远程武器、淘金 | `Slingshot`、`Pan` | 小 |

---

## 附：验证方法备忘

```bash
tools/api.sh StardewValley.Crop currentPhase fullyGrown
tools/api.sh StardewValley.Buildings.ShippingBin
tools/api.sh StardewValley.TerrainFeatures.HoeDirt state
```

> 注意：游戏程序集被拆为 `Stardew Valley.dll`（玩法）与 `StardewValley.GameData.dll`（数据定义）。
> 查 `StardewValley.GameData.*` 下的类型需走后者；`tools/api.sh` 已自动回退。
