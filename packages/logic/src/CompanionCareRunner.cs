// Structure and animal care tasks. GPL-3.0-or-later. See LICENSE.txt.
//
// Coop/barn work is location-level rather than tile-level: you don't "act on tile
// (5,7)", you tend every animal inside a building. The runner walks the companion to
// the building door, then works the interior.
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCPBridge
{
    /// <summary>Runs at most one care task per companion.</summary>
    public class CompanionCareRunner : ICompanionCare
    {
        private readonly CompanionFarmer companion;
        private readonly IMonitor monitor;
        private int ticks;
        private object lastSnapshot;

        public CareState Current { get; private set; }

        public CompanionCareRunner(CompanionFarmer companion, IMonitor monitor)
        {
            this.companion = companion;
            this.monitor = monitor;
        }

        public CareState Start(CareTask task, string buildingName = null, string chestTile = null, string itemFilter = null,
            string locationName = null, IEnumerable<Vector2> tiles = null)
        {
            // A care task replaces a tile task: a companion can't be in two places.
            if (this.Current != null)
                this.Cancel("replaced by new care task");
            this.companion.Visual.controller = null;
            this.Current = new CareState
            {
                Id = $"{task.ToString().ToLowerInvariant()}-{DateTime.UtcNow.Ticks}",
                Task = task,
                LocationName = string.IsNullOrWhiteSpace(locationName)
                    ? this.companion.Visual.currentLocation?.Name : locationName,
                BuildingName = buildingName,
                ChestTile = chestTile,
                ItemFilter = itemFilter,
            };
            if (tiles != null)
                this.Current.Tiles.AddRange(tiles.Distinct().Take(WorkLimits.MaxTiles));
            this.ticks = 0;

            this.monitor.Log($"{this.companion.Name}: care task {this.Current.Id} started", LogLevel.Info);
            return this.Current;
        }

        public void Cancel(string reason)
        {
            if (this.Current == null) return;
            this.Current.AbortReason = reason;
            this.Current.LastResult = reason;
            this.Current.Finished = true;
            this.lastSnapshot = this.MakeSnapshot(this.Current, active: false);
            this.Current = null;
        }

        /// <summary>Returns true while the task is still running.</summary>
        public bool Tick()
        {
            var task = this.Current;
            if (task == null) return false;
            if (task.Finished || task.AbortReason != null)
            {
                this.lastSnapshot = this.MakeSnapshot(task, active: false);
                this.Current = null;
                return false;
            }

            this.companion.SyncFromNpc();
            this.ticks++;

            try
            {
                switch (task.Task)
                {
                    case CareTask.TendAnimals: this.TendAnimals(task); break;
                    case CareTask.FeedAnimals: this.FeedAnimals(task); break;
                    case CareTask.InspectCrops: this.InspectCrops(task); break;
                    case CareTask.ScanTiles: this.ScanTiles(task); break;
                    case CareTask.ChestDeposit: this.ChestTransfer(task, deposit: true); break;
                    case CareTask.ChestWithdraw: this.ChestTransfer(task, deposit: false); break;
                }
            }
            catch (Exception ex)
            {
                task.AbortReason = ex.Message;
                this.monitor.Log($"{this.companion.Name}: care task error: {ex.Message}", LogLevel.Error);
            }

            task.Finished = true;
            this.monitor.Log($"{this.companion.Name}: care task {task.Id} done — {task.LastResult}", LogLevel.Info);
            this.lastSnapshot = this.MakeSnapshot(task, active: false);
            this.Current = null;
            return false;
        }

        // =====================================================================
        // ANIMALS
        // =====================================================================

        /// <summary>
        /// Pet every animal in the target building (or all buildings when none named)
        /// and pick up loose produce lying in the interior.
        /// </summary>
        private void TendAnimals(CareState task)
        {
            var shadow = this.companion.Shadow;
            int petted = 0, collected = 0;
            var buildings = this.ResolveAnimalBuildings(task.BuildingName);

            if (buildings.Count == 0)
            {
                task.LastResult = "no coop/barn found";
                task.Log.Add(new { error = task.LastResult });
                return;
            }

            foreach (var (house, indoors) in buildings)
            {
                // Travel to the interior first; animals live inside, not on the farm map.
                this.EnterBuildingIndoors(house, indoors);

                // Collect loose produce (eggs, milk left on the floor, truffles, etc.).
                if (indoors != null)
                {
                    collected += this.CollectProduce(indoors, shadow);
                }

                // Animals live on the interior GameLocation (AnimalHouse : GameLocation),
                // not on the farm-level Building.
                var herd = indoors?.Animals ?? new StardewValley.Network.NetLongDictionary<FarmAnimal, Netcode.NetRef<FarmAnimal>>();
                foreach (var pair in herd.Pairs.ToList())
                {
                    var animal = pair.Value;
                    if (animal == null) continue;

                    try
                    {
                        animal.pet(shadow);
                        // Animals with a tool harvest (cow/goat/sheep) need the tool swung;
                        // petting alone doesn't yield milk or wool.
                        this.CollectFromAnimal(animal, shadow);
                        petted++;
                    }
                    catch (Exception ex)
                    {
                        task.Log.Add(new { animal = animal.Name, error = ex.Message });
                    }
                }

                task.Log.Add(new { building = house.buildingType.Value, animals = herd.Count(), indoors = indoors?.Name });
            }

            task.LastResult = $"petted {petted} animal(s), collected {collected} produce";
            task.Log.Add(new { petted, collected });
        }

        /// <summary>Walk the companion into the building interior so animal calls succeed.</summary>
        private void EnterBuildingIndoors(Building building, GameLocation indoors)
        {
            if (indoors == null) return;
            if (this.companion.Visual.currentLocation?.Name == indoors.Name) return;

            try
            {
                var door = new Vector2(building.tileX.Value + 1, building.tileY.Value + 1);
                // Interiors have their own coordinate space; the human door is the entry
                // tile in vanilla layouts.
                var entry = new Vector2(indoors.map.Properties.ContainsKey("EntryX")
                    ? int.Parse(indoors.map.Properties["EntryX"].ToString())
                    : 3f, 0f);
                this.companion.WarpTo(indoors.Name, (int)entry.X, (int)(entry.Y + 4));
            }
            catch (Exception ex)
            {
                this.monitor.Log($"{this.companion.Name}: couldn't enter {indoors.Name}: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>Swing the right tool to collect milk/wool; eggs are picked up (Object).</summary>
        private void CollectFromAnimal(FarmAnimal animal, BotFarmer shadow)
        {
            var harvestType = animal.GetHarvestType();
            if (harvestType == null) return;

            switch (harvestType.Value)
            {
                case StardewValley.GameData.FarmAnimals.FarmAnimalHarvestType.DropOvernight:
                    // Produces an object on the floor; handled by CollectProduce.
                    return;

                case StardewValley.GameData.FarmAnimals.FarmAnimalHarvestType.HarvestWithTool:
                {
                    var pail = shadow.Items.OfType<MilkPail>().FirstOrDefault();
                    var shears = shadow.Items.OfType<Shears>().FirstOrDefault();
                    Tool tool = animal.Name?.Contains("Sheep") == true ? shears as Tool : pail as Tool;
                    if (tool == null) return;

                    shadow.FaceToward(animal.Tile);
                    tool.DoFunction(shadow.currentLocation, (int)animal.Tile.X, (int)animal.Tile.Y, 1, shadow);
                    break;
                }

                case StardewValley.GameData.FarmAnimals.FarmAnimalHarvestType.DigUp:
                    // Digging animals (e.g. pig truffles) — the object drops via CollectProduce.
                    return;
            }
        }

        /// <summary>Pick up every shippable object lying in the location.</summary>
        private int CollectProduce(GameLocation location, BotFarmer shadow)
        {
            int collected = 0;
            foreach (var pair in location.objects.Pairs.ToList())
            {
                var obj = pair.Value;
                if (obj == null || obj.bigCraftable.Value) continue;
                // Troughs and machines stay; only pick genuine produce.
                if (obj.Category == SObject.CraftingCategory) continue;

                try
                {
                    var inventoryItem = obj.getOne();
                    if (CompanionInventory.TryAdd(shadow, inventoryItem))
                    {
                        location.objects.Remove(pair.Key);
                        collected++;
                    }
                }
                catch
                {
                    // Some objects aren't movable; leave them.
                }
            }
            return collected;
        }

        private void FeedAnimals(CareState task)
        {
            int fed = 0;
            foreach (var (house, indoors) in this.ResolveAnimalBuildings(task.BuildingName))
            {
                if (indoors is not AnimalHouse animalHouse) continue;
                try
                {
                    animalHouse.feedAllAnimals();
                    fed++;
                }
                catch (Exception ex)
                {
                    task.Log.Add(new { building = house.buildingType.Value, error = ex.Message });
                }
            }
            task.LastResult = fed > 0 ? $"fed animals in {fed} building(s)" : "no animal buildings found";
        }

        private List<(Building house, GameLocation indoors)> ResolveAnimalBuildings(string name)
        {
            var result = new List<(Building, GameLocation)>();
            var farm = Game1.getFarm();
            if (farm == null) return result;

            foreach (var building in farm.buildings)
            {
                // Barn/Coop classes are obsolete in 1.6 — every barn is a plain Building
                // whose interior is an AnimalHouse. Detect via the interior type.
                var indoors = building.indoors.Value;
                if (indoors is not AnimalHouse) continue;
                if (!string.IsNullOrEmpty(name) && !building.buildingType.Value.Contains(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add((building, indoors));
            }
            return result;
        }

        // =====================================================================
        // CROPS (read-only)
        // =====================================================================

        /// <summary>Report crop growth so the agent can plan instead of guessing.</summary>
        private void InspectCrops(CareState task)
        {
            var location = this.companion.Visual.currentLocation;
            if (location == null) { task.LastResult = "no location"; return; }

            var crops = new List<object>();
            foreach (var pair in location.terrainFeatures.Pairs)
            {
                if (pair.Value is not HoeDirt dirt || dirt.crop == null) continue;

                var data = dirt.crop.GetData();
                int daysLeft = dirt.crop.fullyGrown.Value
                    ? 0
                    : Math.Max(0, dirt.crop.phaseDays.Count - dirt.crop.currentPhase.Value - 1);

                crops.Add(new
                {
                    x = (int)pair.Key.X,
                    y = (int)pair.Key.Y,
                    harvest = dirt.crop.indexOfHarvest.Value,
                    phase = dirt.crop.currentPhase.Value,
                    daysToMature = daysLeft,
                    fullyGrown = dirt.crop.fullyGrown.Value,
                    regrowDays = data?.RegrowDays ?? -1,
                    watered = dirt.state.Value == HoeDirt.watered,
                });
            }

            task.LastResult = $"{crops.Count} crop(s) inspected";
            task.Log.Add(new { crops });
        }

        // =====================================================================
        // REGION SCAN
        // =====================================================================

        /// <summary>
        /// Read every tile in a region — objects, terrain features, crops. This is the
        /// ground truth of what the player sees, as an agent-callable API.
        /// The location and coordinates are provided explicitly so the scan matches the
        /// requested map region rather than whichever map the companion currently occupies.
        /// </summary>
        private void ScanTiles(CareState task)
        {
            var location = string.IsNullOrWhiteSpace(task.LocationName)
                ? this.companion.Visual.currentLocation
                : Game1.getLocationFromName(task.LocationName);
            if (location == null) { task.LastResult = "no location"; return; }
            var tiles = new List<object>();
            var scanTiles = task.Tiles;
            if (scanTiles.Count == 0 && TryParseTile(task.ChestTile, out var c1) && TryParseTile(task.ItemFilter, out var c2))
            {
                int x1 = (int)c1.X, y1 = (int)c1.Y, x2 = (int)c2.X, y2 = (int)c2.Y;
                int left = Math.Min(x1, x2), right = Math.Max(x1, x2);
                int top = Math.Min(y1, y2), bottom = Math.Max(y1, y2);
                for (int y = top; y <= bottom && scanTiles.Count < WorkLimits.MaxTiles; y++)
                    for (int x = left; x <= right && scanTiles.Count < WorkLimits.MaxTiles; x++)
                        scanTiles.Add(new Vector2(x, y));
            }
            if (scanTiles.Count == 0) { task.LastResult = "provide area or tiles to scan"; return; }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var debrisByTile = new Dictionary<Vector2, List<string>>();
            foreach (var debris in location.debris.ToList())
            {
                if (debris == null) continue;
                string label = debris.item?.Name ?? debris.itemId.Value ?? debris.debrisType.Value.ToString();
                foreach (var chunk in debris.Chunks)
                {
                    var pos = chunk.position.Value;
                    var key = new Vector2((int)(pos.X / 64f), (int)(pos.Y / 64f));
                    if (!debrisByTile.TryGetValue(key, out var names))
                        debrisByTile[key] = names = new List<string>();
                    if (!names.Contains(label)) names.Add(label);
                }
            }

            foreach (var t in scanTiles.Distinct().Take(WorkLimits.MaxTiles))
            {
                int x = (int)t.X, y = (int)t.Y;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                string obj = null, terrain = null, crop = null;
                if (location.objects.TryGetValue(t, out var o))
                    obj = o.Name;
                if (location.terrainFeatures.TryGetValue(t, out var f))
                {
                    terrain = f.GetType().Name;
                    if (f is HoeDirt d && d.crop != null)
                        crop = d.crop.indexOfHarvest.Value
                            + (d.readyForHarvest() ? " (ready)" : $" (phase {d.crop.currentPhase.Value})");
                }
                debrisByTile.TryGetValue(t, out var debrisNames);
                if (obj == null && terrain == null && debrisNames == null) continue;
                tiles.Add(new { x, y, obj, terrain, crop, debris = debrisNames });
            }

            task.LastResult = $"{tiles.Count} non-empty tile(s)";
            string region = $"{minX},{minY}..{maxX},{maxY}";
            task.Log.Add(new { region, tiles });

            // Persist the scan so agents (and humans) can read it without a console.
            try
            {
                var logicDir = Path.GetDirectoryName(typeof(CompanionCareRunner).Assembly.Location);
                var outDir = Path.GetFullPath(Path.Combine(logicDir, "..", ".."));
                var path = Path.Combine(outDir, "scan_result.json");
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    location = location.Name,
                    region,
                    scannedAt = DateTime.UtcNow.ToString("o"),
                    tiles,
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, path, true);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"scan write failed: {ex.Message}", LogLevel.Warn);
            }
        }

        private static bool TryParseTilePair(string s, out Vector2 tile) => TryParseTile(s, out tile);

        // =====================================================================
        // CHESTS
        // =====================================================================

        private void ChestTransfer(CareState task, bool deposit)
        {
            if (!TryParseTile(task.ChestTile, out var tile))
            {
                task.LastResult = "chest x,y required";
                return;
            }

            var location = this.companion.Visual.currentLocation;
            if (!location.objects.TryGetValue(tile, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                task.LastResult = $"no chest at ({tile.X},{tile.Y})";
                return;
            }

            var shadow = this.companion.Shadow;
            int moved = 0;

            if (deposit)
            {
                for (int i = 0; i < shadow.Items.Count; i++)
                {
                    if (shadow.Items[i] is not SObject item) continue;
                    if (!Matches(item, task.ItemFilter)) continue;
                    var contents = chest.GetItemsForPlayer(shadow.UniqueMultiplayerID);
                    if (contents.Count >= chest.GetActualCapacity()) break;
                    contents.Add(item);
                    shadow.Items[i] = null;
                    moved++;
                }
            }
            else
            {
                var contents = chest.GetItemsForPlayer(shadow.UniqueMultiplayerID);
                foreach (var entry in contents.ToList())
                {
                    if (entry is not SObject item) continue;
                    if (!Matches(item, task.ItemFilter)) continue;
                    if (!CompanionInventory.TryAdd(shadow, item)) continue;
                    contents.Remove(item);
                    moved++;
                }
            }

            task.LastResult = $"{(deposit ? "deposited" : "withdrew")} {moved} stack(s)";
            task.Log.Add(new { moved, chest = $"{tile.X},{tile.Y}" });
        }

        private static bool Matches(SObject item, string filter)
        {
            if (string.IsNullOrEmpty(filter) || filter == "all") return true;
            return item.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
        }

        private static bool TryParseTile(string s, out Vector2 tile)
        {
            tile = Vector2.Zero;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(',');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return false;
            tile = new Vector2(x, y);
            return true;
        }

        public object Snapshot()
        {
            var t = this.Current;
            return t == null ? this.lastSnapshot ?? (object)new { active = false } : this.MakeSnapshot(t, active: true);
        }

        private object MakeSnapshot(CareState t, bool active)
        {
            return new
            {
                active,
                status = active ? "running" : t.AbortReason == null ? "completed" : "aborted",
                id = t.Id,
                task = t.Task.ToString(),
                location = t.LocationName,
                building = t.BuildingName,
                lastResult = t.LastResult,
                aborted = t.AbortReason,
                log = t.Log,
            };
        }
    }
}
