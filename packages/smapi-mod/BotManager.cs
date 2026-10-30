// Migrated from StardewValley-MCP (source Apache-2.0, https://github.com/amarisaster/StardewValley-MCP).
// This file: GPL-3.0-or-later. See LICENSE.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Pathfinding;
using StardewValley.Tools;

namespace StardewMCPBridge
{
    public class BotManager
    {
        private readonly IMonitor monitor;
        private readonly IModHelper helper;
        private readonly Dictionary<string, CompanionAI> companions = new Dictionary<string, CompanionAI>();

        /// <summary>Live companions, for logic hot-reload.</summary>
        public System.Collections.Generic.IEnumerable<CompanionAI> AIs => this.companions.Values;

        /// <summary>Save-folder key for the persisted companion snapshot.</summary>
        private const string StateFileName = "companion_state.json";

        /// <summary>Per-companion last command results, written to bridge data.</summary>
        private readonly Dictionary<string, object> commandResults = new Dictionary<string, object>();

        /// <summary>Whether AI ticks are paused (e.g. while a menu is open).</summary>
        private bool aiPaused = false;

        public BotManager(IMonitor monitor, IModHelper helper)
        {
            this.monitor = monitor;
            this.helper = helper;
        }

        public void SpawnBot(string name, string type)
        {
            this.SpawnBot(name, type, CompanionMode.Follow, autoCombat: false);
        }

        /// <summary>
        /// Spawn a companion, optionally restoring a persisted mode/auto-combat flag.
        /// Used both by the live "spawn" action and by save-load restoration.
        /// </summary>
        public void SpawnBot(string name, string type, CompanionMode mode, bool autoCombat)
        {
            if (!Context.IsWorldReady) return;
            if (this.companions.ContainsKey(name)) return;

            this.monitor.Log($"Spawning {name} with shadow farmer", LogLevel.Info);

            try
            {
                // Remove any game-auto-created NPCs with this name from ALL locations
                // (Data/Characters injection causes the game to auto-spawn them)
                foreach (var loc in Game1.locations)
                {
                    var dupes = loc.characters.Where(c => c.Name == name).ToList();
                    foreach (var dupe in dupes)
                    {
                        loc.characters.Remove(dupe);
                        this.monitor.Log($"Removed duplicate {name} from {loc.Name}", LogLevel.Debug);
                    }
                }

                var portrait = this.helper.ModContent.Load<Microsoft.Xna.Framework.Graphics.Texture2D>($"assets/{name}_portrait.png");

                var spawnPos = Game1.player.Position;
                if (name == "Companion2") spawnPos += new Vector2(64, 0);
                else if (name == "Companion1") spawnPos += new Vector2(-64, 0);

                var botNpc = new CompanionNPC(
                    new AnimatedSprite($"Characters\\{name}", 0, 16, 32),
                    spawnPos,
                    Game1.player.currentLocation.Name,
                    2, name, portrait, false
                );
                botNpc.displayName = name;
                Game1.player.currentLocation.addCharacter(botNpc);
                this.monitor.Log($"{name} placed at pixel ({spawnPos.X},{spawnPos.Y}), tile ({spawnPos.X / 64f:F1},{spawnPos.Y / 64f:F1})", LogLevel.Info);

                var companionFarmer = new CompanionFarmer(botNpc, name, this.monitor, this.helper);
                var ai = new CompanionAI(companionFarmer, this.monitor);
                ai.Mode = mode;
                ai.AutoCombat = autoCombat;

                this.companions.Add(name, ai);
                this.monitor.Log($"{name} spawned with shadow farmer at {Game1.player.currentLocation.Name}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Failed to spawn {name}: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);
            }
        }

        public void Update()
        {
            if (this.aiPaused) return;

            foreach (var kvp in this.companions)
            {
                try
                {
                    kvp.Value.Tick();
                }
                catch (Exception ex)
                {
                    this.monitor.Log($"{kvp.Key}: AI tick error (recovering): {ex.Message}", LogLevel.Error);
                }
            }
        }

        /// <summary>Pause or resume companion AI ticks (used while a menu is open).</summary>
        public void PauseAI(bool paused)
        {
            if (this.aiPaused == paused) return;
            this.aiPaused = paused;
            this.monitor.Log(paused ? "Companion AI paused (menu open)" : "Companion AI resumed (menu closed)", LogLevel.Debug);
        }

        /// <summary>Get expanded per-companion status for bridge data (includes surroundings in Player mode).</summary>
        public object GetBotStatus()
        {
            var statuses = new List<object>();
            foreach (var kvp in this.companions)
            {
                var ai = kvp.Value;
                var companion = ai.Companion;
                var pos = companion.Visual.Position;
                var tile = companion.Visual.Tile;

                var status = new Dictionary<string, object>
                {
                    ["name"] = kvp.Key,
                    ["position"] = new { x = pos.X, y = pos.Y },
                    ["tile"] = new { x = (int)tile.X, y = (int)tile.Y },
                    ["location"] = companion.Visual.currentLocation?.Name ?? "unknown",
                    ["status"] = ai.GetStatusDescription(),
                    ["mode"] = ai.Mode.ToString().ToLower(),
                    ["stamina"] = companion.GetStaminaPercent(),
                    ["health"] = companion.Shadow.health,
                    ["maxHealth"] = companion.Shadow.maxHealth,
                    ["autoCombat"] = ai.AutoCombat,
                    ["combat"] = ai.Combat.InCombat,
                    ["combatStatus"] = ai.Combat.Status,
                    ["job"] = ai.Jobs.Snapshot(),
                    ["care"] = ai.Care.Snapshot()
                };

                // Include surroundings scan in Player mode (the companion's "eyes")
                if (ai.Mode == CompanionMode.Player)
                {
                    var scan = companion.GetSurroundings(8);
                    if (scan != null)
                    {
                        status["surroundings"] = new
                        {
                            tiles = scan.Tiles.Select(t => new
                            {
                                x = t.X, y = t.Y,
                                passable = t.Passable,
                                water = t.IsWater,
                                terrain = t.Terrain,
                                crop = t.CropName,
                                cropReady = t.CropReady,
                                waterState = t.WaterState,
                                obj = t.ObjectName,
                                objType = t.ObjectType,
                                breakable = t.Breakable,
                                interactable = t.Interactable
                            }),
                            monsters = scan.Monsters.Select(m => new
                            {
                                name = m.Name,
                                x = m.X, y = m.Y,
                                health = m.Health,
                                maxHealth = m.MaxHealth
                            }),
                            npcs = scan.Npcs.Select(n => new
                            {
                                name = n.Name,
                                x = n.X, y = n.Y
                            })
                        };
                    }

                    status["inventory"] = companion.GetInventoryData();

                    // Include last command result if any
                    if (this.commandResults.TryGetValue(kvp.Key, out var result))
                        status["lastCommandResult"] = result;
                }

                statuses.Add(status);
            }
            return statuses;
        }

        // ======================
        // SLEEP SIGNAL
        // ======================

        /// <summary>Signal all bot farmers as sleep-ready. Call when host goes to bed.</summary>
        public void SignalAllSleepReady()
        {
            foreach (var kvp in this.companions)
            {
                kvp.Value.Companion.SignalSleepReady();
                this.monitor.Log($"{kvp.Key}: Signaled sleep ready", LogLevel.Debug);
            }
        }

        // ======================
        // DAY TRANSITION
        // ======================

        /// <summary>Restore companion stamina on new day; reset state.</summary>
        public void OnDayStarted()
        {
            foreach (var kvp in this.companions)
            {
                var ai = kvp.Value;
                var shadow = ai.Companion.Shadow;
                shadow.WakeUp();

                // Mode intentionally persists across days: the player (or agent) set it,
                // and it is what companion_state.json restores after a save/load cycle.
                // Only transient AI state (path, target, stuck counters) resets — that
                // happens inside CompanionAI's own tick bookkeeping.

                // If companion was in a mine/dungeon, warp to farm
                var loc = ai.Companion.Visual.currentLocation;
                if (loc is StardewValley.Locations.MineShaft || loc?.Name == "VolcanoDungeon")
                {
                    ai.Companion.WarpTo("Farm", 64, 15);
                    this.monitor.Log($"{kvp.Key}: Was in {loc.Name} at day end — warped to Farm", LogLevel.Info);
                }

                this.monitor.Log($"{kvp.Key}: New day — stamina restored, mode: {ai.Mode}", LogLevel.Info);
            }
        }

        /// <summary>
        /// Temporarily remove companion NPCs from their locations while the game is
        /// serializing the save. <see cref="CompanionNPC"/> derives from NPC and
        /// therefore lives in <c>location.characters</c>, which the save writer walks
        /// with XmlSerializer — it throws "cannot be serialized because it does not
        /// have a parameterless constructor" and aborts the whole save. Companions are
        /// rebuilt from companion_state.json on load, so they must not be persisted.
        /// </summary>
        public void DetachForSave()
        {
            foreach (var kvp in this.companions)
            {
                try
                {
                    var npc = kvp.Value.Companion.Visual;
                    npc.currentLocation?.characters.Remove(npc);
                }
                catch (Exception ex)
                {
                    this.monitor.Log($"{kvp.Key}: failed to detach for save: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        /// <summary>Put companion NPCs back after the save finished (or failed).</summary>
        public void ReattachAfterSave()
        {
            foreach (var kvp in this.companions)
            {
                try
                {
                    var npc = kvp.Value.Companion.Visual;
                    var loc = npc.currentLocation;
                    if (loc != null && !loc.characters.Contains(npc))
                        loc.addCharacter(npc);
                }
                catch (Exception ex)
                {
                    this.monitor.Log($"{kvp.Key}: failed to reattach after save: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        /// <summary>
        /// Persist which companions are alive plus their mode / auto-combat flag so
        /// <see cref="RestoreState"/> can rebuild them when the save is loaded.
        /// Called on save, on return-to-title, and after any mode/combat change.
        /// </summary>
        public void SaveState()
        {
            if (!Context.IsWorldReady) return;

            try
            {
                var state = new CompanionState();
                foreach (var kvp in this.companions)
                {
                    state.Companions.Add(new CompanionSnapshot
                    {
                        Name = kvp.Key,
                        Mode = kvp.Value.Mode.ToString().ToLowerInvariant(),
                        AutoCombat = kvp.Value.AutoCombat,
                    });
                }

                // Save data is per-save and only writable by the host. A farmhand
                // connected to a remote host throws here, so fall back to global data:
                // it doesn't travel with the save, but the companion still comes back.
                if (Context.IsMainPlayer)
                {
                    this.helper.Data.WriteSaveData(StateFileName, state);
                    this.monitor.Log($"Saved companion state ({state.Companions.Count} companion(s)) with the save.", LogLevel.Debug);
                }
                else
                {
                    this.helper.Data.WriteGlobalData(StateFileName, state);
                    this.monitor.Log($"Saved companion state ({state.Companions.Count} companion(s)) locally — multiplayer guest mode cannot write save data.", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                // Never let persistence break a save.
                this.monitor.Log($"Failed to save companion state: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// Rebuild companions recorded by a previous session. Runs after the save is
        /// loaded; leftover NPCs are swept first so we never end up with duplicates.
        /// </summary>
        public void RestoreState()
        {
            if (!Context.IsWorldReady) return;

            CompanionState state;
            try
            {
                state = Context.IsMainPlayer
                    ? this.helper.Data.ReadSaveData<CompanionState>(StateFileName)
                    : this.helper.Data.ReadGlobalData<CompanionState>(StateFileName);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Failed to read companion state: {ex.Message}", LogLevel.Warn);
                return;
            }

            // Always start from a clean slate — a loaded save may still hold NPCs
            // from the previous session.
            this.Cleanup();

            if (state?.Companions == null || state.Companions.Count == 0)
            {
                this.monitor.Log("Save loaded: no companions were saved. Send a 'spawn' action to add them.", LogLevel.Debug);
                return;
            }

            foreach (var snapshot in state.Companions)
            {
                if (string.IsNullOrEmpty(snapshot?.Name)) continue;

                CompanionMode mode = Enum.TryParse<CompanionMode>(snapshot.Mode, ignoreCase: true, out var parsed)
                    ? parsed
                    : CompanionMode.Follow;

                this.SpawnBot(snapshot.Name, "Restored", mode, snapshot.AutoCombat);
            }

            this.monitor.Log($"Save loaded: restored {state.Companions.Count} companion(s) with their previous mode.", LogLevel.Info);
        }

        /// <summary>
        /// Clean up companions (return to title, save load, or console command).
        /// Also sweeps world locations for leftover companion NPCs that aren't
        /// tracked in this.companions (e.g. NPCs from a previous session).
        /// </summary>
        public void Cleanup()
        {
            try
            {
                foreach (var loc in Game1.locations)
                {
                    for (int i = loc.characters.Count - 1; i >= 0; i--)
                    {
                        var c = loc.characters[i];
                        if (c is CompanionNPC || c.Name == "Companion1" || c.Name == "Companion2")
                            loc.characters.RemoveAt(i);
                    }
                }
            }
            catch (Exception ex)
            {
                // World may be mid-teardown (e.g. ReturnedToTitle) — tracked
                // companions below are still cleaned up.
                this.monitor.Log($"Cleanup location sweep skipped: {ex.Message}", LogLevel.Debug);
            }

            this.companions.Clear();
            this.commandResults.Clear();
            this.monitor.Log("All companions cleaned up", LogLevel.Info);
        }

        // ======================
        // ACTION PROCESSING
        // ======================

        public void ProcessAction(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("actionType", out var actionType)) return;

            string action = actionType.GetString();

            // Check for companion-targeted commands (Player mode direct control)
            if (root.TryGetProperty("companion", out var companionProp))
            {
                string companionName = companionProp.GetString();
                if (this.companions.TryGetValue(companionName, out var targetAi))
                {
                    this.ProcessCompanionCommand(action, root, companionName, targetAi);
                    return;
                }
                else
                {
                    this.monitor.Log($"Unknown companion: {companionName}", LogLevel.Warn);
                    return;
                }
            }

            // Global commands (existing behavior)
            switch (action)
            {
                case "spawn":
                    this.SpawnBot("Companion1", "Guard");
                    this.SpawnBot("Companion2", "Anchor");
                    this.SetAllMode(CompanionMode.Follow);
                    this.monitor.Log("Spawned and following", LogLevel.Info);
                    break;

                case "follow":
                    this.SetAllMode(CompanionMode.Follow);
                    break;

                case "stay":
                    this.SetAllMode(CompanionMode.Idle);
                    break;

                case "farm":
                    this.SetAllMode(CompanionMode.Farm);
                    break;

                case "mine":
                    this.SetAllMode(CompanionMode.Mine);
                    break;

                case "fish":
                    this.SetAllMode(CompanionMode.Fish);
                    break;

                case "water_all":
                    this.WaterAll();
                    break;

                case "harvest_all":
                    this.HarvestAll();
                    break;

                // Set individual companion mode
                case "set_mode":
                    if (root.TryGetProperty("target", out var target) &&
                        root.TryGetProperty("mode", out var modeStr))
                    {
                        string targetName = target.GetString();
                        if (this.companions.TryGetValue(targetName, out var ai) &&
                            Enum.TryParse<CompanionMode>(modeStr.GetString(), true, out var mode))
                        {
                            ai.Mode = mode;
                            this.monitor.Log($"Set {targetName} to {mode}", LogLevel.Info);
                            this.SaveState();
                        }
                    }
                    break;

                // Warp companions to a specific location
                case "warp":
                    if (root.TryGetProperty("location", out var loc) &&
                        root.TryGetProperty("x", out var wx) &&
                        root.TryGetProperty("y", out var wy))
                    {
                        // Same reasoning as warp_to: switch to idle so follow mode doesn't
                        // drag everyone back to the player on the next AI tick.
                        foreach (var kvp in this.companions)
                        {
                            this.StopCompanionTasks(kvp.Value, "companion warped");
                            kvp.Value.Companion.WarpTo(loc.GetString(), wx.GetInt32(), wy.GetInt32());
                            kvp.Value.Mode = CompanionMode.Idle;
                        }
                        this.SaveState();
                    }
                    break;

                // Single-tile actions (from stardew_action tool)
                case "water":
                case "harvest":
                case "clear":
                case "hoe":
                    if (root.TryGetProperty("x", out var ax) && root.TryGetProperty("y", out var ay))
                    {
                        // Honour an explicit location: the player may be indoors while the
                        // agent wants a tile on the farm, and the old code always used the
                        // player's current location (so outdoor tiles silently "failed").
                        GameLocation location = null;
                        if (root.TryGetProperty("location", out var locProp) && locProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string locName = locProp.GetString();
                            location = !string.IsNullOrEmpty(locName) ? Game1.getLocationFromName(locName) : null;
                            if (location == null)
                            {
                                this.monitor.Log($"Action {action}: unknown location '{locName}'", LogLevel.Warn);
                                break;
                            }
                        }
                        location ??= Game1.player.currentLocation;
                        if (location != null)
                        {
                            var tile = new Vector2(ax.GetInt32(), ay.GetInt32());
                            bool ok = action switch
                            {
                                "water" => CompanionActions.WaterTile(location, tile, this.monitor),
                                "harvest" => CompanionActions.HarvestTile(location, tile, this.monitor),
                                "clear" => CompanionActions.ClearDebris(location, tile, this.monitor),
                                "hoe" => CompanionActions.HoeTile(location, tile, this.monitor),
                                _ => false
                            };
                            this.monitor.Log($"Action {action} at ({ax.GetInt32()},{ay.GetInt32()}): {(ok ? "success" : "failed")}", LogLevel.Info);
                        }
                    }
                    break;

                // Companion speech — chat line + optional villager-style dialogue popup
                case "say":
                {
                    if (root.TryGetProperty("message", out var sayMsg) && !string.IsNullOrEmpty(sayMsg.GetString()))
                    {
                        string text = sayMsg.GetString();
                        string speaker = null;
                        if (root.TryGetProperty("target", out var sayTarget))
                            speaker = sayTarget.GetString();

                        bool popup = false;
                        if (root.TryGetProperty("popup", out var sayPopup))
                            popup = sayPopup.ValueKind == System.Text.Json.JsonValueKind.True;

                        this.Say(speaker, text, popup);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Make a companion speak. Always posts a "{name}: text" chat line;
        /// when <paramref name="popup"/> is true (or the player clicks the companion),
        /// shows a villager-style dialogue box with the companion's portrait.
        /// The dialogue stays queued on the NPC so interacting with them replays it.
        /// </summary>
        private void Say(string companionName, string text, bool popup)
        {
            Color chatColor = popup ? Microsoft.Xna.Framework.Color.LightBlue : Microsoft.Xna.Framework.Color.Gold;

            if (companionName != null && this.companions.TryGetValue(companionName, out var ai))
            {
                var npc = ai.Companion.Visual;
                npc.CurrentDialogue.Push(new StardewValley.Dialogue(npc, $"MCP_{companionName}", text));
                Game1.chatBox?.addMessage($"{companionName}: {text}", chatColor);

                if (popup)
                    Game1.drawDialogue(npc);
            }
            else if (companionName == null)
            {
                // No specific companion: speak as a group line in chat only
                Game1.chatBox?.addMessage(text, chatColor);
            }
            else
            {
                this.monitor.Log($"Say: unknown companion {companionName}", LogLevel.Warn);
            }
        }

        // ======================
        // DIRECT COMPANION COMMANDS (Player mode)
        // ======================

        private void ProcessCompanionCommand(string action, System.Text.Json.JsonElement root, string companionName, CompanionAI ai)
        {
            var companion = ai.Companion;
            bool success = false;
            string detail = "";

            try
            {
                switch (action)
                {
                    // --- Movement ---
                    case "move_to":
                    {
                        this.StopCompanionTasks(ai, "replaced by direct movement");
                        int tx = root.GetProperty("x").GetInt32();
                        int ty = root.GetProperty("y").GetInt32();
                        var npc = companion.Visual;
                        var location = npc.currentLocation ?? Game1.player.currentLocation;
                        // A* for the route, NpcWalker for the execution — the vanilla
                        // PathFindController freezes NPCs on multiplayer hosts.
                        var planner = new PathFindController(
                            npc, location, new Microsoft.Xna.Framework.Point(tx, ty), 2);
                        if (planner.pathToEndPoint != null && planner.pathToEndPoint.Count > 0)
                        {
                            ai.Walker.Start(planner.pathToEndPoint);
                            success = true;
                            detail = $"Pathing to ({tx},{ty}) ({planner.pathToEndPoint.Count} steps)";
                        }
                        else
                        {
                            // No route — teleport as before.
                            npc.Position = new Vector2(tx * 64f, ty * 64f);
                            companion.SyncFromNpc();
                            success = true;
                            detail = $"Teleported to ({tx},{ty}) (path failed)";
                        }
                        break;
                    }

                    case "warp_to":
                    {
                        string loc = root.GetProperty("location").GetString();
                        int wx = root.GetProperty("x").GetInt32();
                        int wy = root.GetProperty("y").GetInt32();
                        this.StopCompanionTasks(ai, "companion warped");
                        companion.WarpTo(loc, wx, wy);
                        // Follow mode pulls the companion back to the player on the next
                        // tick, which would undo the warp immediately if the player is in
                        // another location. Warping is an explicit placement request, so
                        // park the companion there instead.
                        ai.Mode = CompanionMode.Idle;
                        this.SaveState();
                        success = true;
                        detail = $"Warped to {loc} ({wx},{wy}) — mode set to idle so it stays";
                        break;
                    }

                    case "face_direction":
                    {
                        int dir = root.GetProperty("direction").GetInt32(); // 0=up,1=right,2=down,3=left
                        if (dir < 0 || dir > 3)
                        {
                            detail = $"Invalid direction: {dir} (must be 0-3)";
                            break;
                        }
                        companion.Visual.FacingDirection = dir;
                        companion.Shadow.FacingDirection = dir;
                        success = true;
                        detail = $"Facing {new[] { "up", "right", "down", "left" }[dir]}";
                        break;
                    }

                    // --- Tools ---
                    case "use_tool":
                    {
                        string toolName = root.GetProperty("tool").GetString();
                        int tx = root.GetProperty("x").GetInt32();
                        int ty = root.GetProperty("y").GetInt32();
                        var tile = new Vector2(tx, ty);

                        Type toolType = toolName.ToLower() switch
                        {
                            "pickaxe" => typeof(Pickaxe),
                            "axe" => typeof(Axe),
                            "hoe" => typeof(Hoe),
                            "wateringcan" or "watering_can" => typeof(WateringCan),
                            "sword" or "weapon" => typeof(MeleeWeapon),
                            _ => null
                        };

                        if (toolType != null)
                        {
                            success = companion.UseToolAt(tile, toolType);
                            detail = success ? $"Used {toolName} at ({tx},{ty})" : $"Failed to use {toolName}";
                        }
                        else
                        {
                            detail = $"Unknown tool: {toolName}";
                        }
                        break;
                    }

                    case "attack":
                    {
                        success = companion.AttackNearbyMonsters();
                        detail = success ? "Attacked nearby monster" : "No monsters in range";
                        break;
                    }

                    // --- Long-running tile job (chop / mine / clear / hoe / water / harvest) ---
                    case "set_chest":
                    {
                        // Pin the auto-deposit chest for sweep loot; -1 clears the pin.
                        int cx = root.TryGetProperty("x", out var cxE) && cxE.ValueKind == System.Text.Json.JsonValueKind.Number ? cxE.GetInt32() : -1;
                        int cy = root.TryGetProperty("y", out var cyE) && cyE.ValueKind == System.Text.Json.JsonValueKind.Number ? cyE.GetInt32() : -1;
                        ai.Jobs.SetDepositChest(cx, cy);
                        success = true;
                        detail = cx < 0 ? "deposit chest pin cleared" : $"deposit chest pinned to ({cx},{cy})";
                        break;
                    }

                    case "work":
                    {
                        // The agent states an intent; the runner decides per tile what
                        // that means (which tool, already done, needs a refill...).
                        if (!root.TryGetProperty("task", out var taskProp))
                        {
                            detail = "task is required (clear_area, plant, water, harvest, sell, chop, mine)";
                            break;
                        }

                        string taskName = taskProp.GetString()?.Replace("_", "");
                        if (!Enum.TryParse<WorkTask>(taskName, ignoreCase: true, out var task))
                        {
                            detail = $"Unknown task: {taskProp.GetString()} (clear_area, plant, water, harvest, sell, chop, mine)";
                            break;
                        }

                        string locName = root.TryGetProperty("location", out var locEl) && locEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? locEl.GetString()
                            : companion.Visual.currentLocation?.Name;

                        string seedId = root.TryGetProperty("seed", out var seedEl) && seedEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? seedEl.GetString()
                            : null;
                        string sellFilter = root.TryGetProperty("filter", out var filterEl) && filterEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? filterEl.GetString()
                            : null;

                        var tiles = ParseTiles(root);
                        if (tiles.Count == 0 && task != WorkTask.Sell)
                        {
                            detail = "Provide a tile (x,y), a region (x1,y1,x2,y2) or a tiles array";
                            break;
                        }
                        var workLocation = task == WorkTask.Sell ? null : Game1.getLocationFromName(locName);
                        if (task != WorkTask.Sell && workLocation == null)
                        {
                            detail = $"Location '{locName}' was not found";
                            break;
                        }
                        if (task != WorkTask.Sell && !TilesInsideMap(workLocation, tiles, out var workAreaError))
                        {
                            detail = workAreaError;
                            break;
                        }

                        this.StopCompanionTasks(ai, "replaced by new work task");
                        var job = ai.Jobs.Start(task, locName, tiles, seedId, sellFilter);
                        success = true;
                        detail = $"{task} started over {job.TotalTiles} tile(s) in {locName}"
                            + (tiles.Count > WorkLimits.MaxTiles
                                ? $" (capped at {WorkLimits.MaxTiles})"
                                : "");
                        break;
                    }

                    // --- Building / animal / chest tasks ---
                    case "care":
                    {
                        if (!root.TryGetProperty("task", out var careProp))
                        {
                            detail = "task is required (tend_animals, feed_animals, inspect_crops, chest_deposit, chest_withdraw)";
                            break;
                        }

                        string careName = careProp.GetString()?.Replace("_", "");
                        if (!Enum.TryParse<CareTask>(careName, ignoreCase: true, out var careTask))
                        {
                            detail = $"Unknown care task: {careProp.GetString()}";
                            break;
                        }

                        string building = root.TryGetProperty("building", out var bEl) && bEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? bEl.GetString() : null;
                        string chest = root.TryGetProperty("chest", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? cEl.GetString() : null;
                        string filter = root.TryGetProperty("filter", out var fEl) && fEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? fEl.GetString() : null;

                        string careLocation = root.TryGetProperty("location", out var careLocEl)
                            && careLocEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? careLocEl.GetString()
                            : companion.Visual.currentLocation?.Name;
                        var careTiles = ParseCareTiles(root, chest, filter);
                        if (careTask == CareTask.ScanTiles && careTiles.Count == 0
                            && (string.IsNullOrWhiteSpace(chest) || string.IsNullOrWhiteSpace(filter)))
                        {
                            detail = "scan_tiles requires an area or a tiles array";
                            break;
                        }
                        var careGameLocation = careTask == CareTask.ScanTiles
                            ? Game1.getLocationFromName(careLocation) : null;
                        if (careTask == CareTask.ScanTiles && careGameLocation == null)
                        {
                            detail = $"Location '{careLocation}' was not found";
                            break;
                        }
                        if (careTask == CareTask.ScanTiles
                            && !TilesInsideMap(careGameLocation, careTiles, out var careAreaError))
                        {
                            detail = careAreaError;
                            break;
                        }

                        this.StopCompanionTasks(ai, "replaced by new care task");
                        ai.Care.Start(careTask, building, chest, filter, careLocation, careTiles);
                        success = true;
                        detail = $"Care task started: {careTask}";
                        break;
                    }

                    case "cancel_job":
                    {
                        this.StopCompanionTasks(ai, "cancelled by agent");
                        success = true;
                        detail = "Job cancelled";
                        break;
                    }

                    case "get_job":
                    {
                        success = true;
                        detail = System.Text.Json.JsonSerializer.Serialize(ai.Jobs.Snapshot());
                        break;
                    }

                    // --- Talk to an NPC: walk over, open the dialogue, report what they said ---
                    case "talk_to":
                    {
                        string npcName = root.GetProperty("npc").GetString();
                        var location = companion.Visual.currentLocation ?? Game1.player?.currentLocation;
                        if (location == null)
                        {
                            detail = "No current location";
                            break;
                        }

                        var target = location.characters
                            .OfType<NPC>()
                            .FirstOrDefault(c => !(c is Monster)
                                && (string.Equals(c.Name, npcName, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(c.displayName, npcName, StringComparison.OrdinalIgnoreCase)));

                        if (target == null)
                        {
                            detail = $"NPC '{npcName}' not found in {location.Name}";
                            break;
                        }

                        float dist = Vector2.Distance(companion.Visual.Tile, target.Tile);
                        if (dist > 1.45f)
                        {
                            // Walk to an adjacent tile, then let the agent retry.
                            var adjacent = GetAdjacentTile(location, target.Tile);
                            if (adjacent.HasValue)
                            {
                                companion.Visual.controller = new StardewValley.Pathfinding.PathFindController(
                                    companion.Visual, location, new Microsoft.Xna.Framework.Point((int)adjacent.Value.X, (int)adjacent.Value.Y), 2);
                                success = true;
                                detail = $"Walking to {target.Name} (now {dist:F1} tiles away) — call talk_to again when in range";
                            }
                            else
                            {
                                detail = $"Cannot reach {target.Name} (no adjacent walkable tile)";
                            }
                            break;
                        }

                        // In range: trigger the vanilla interaction so the game builds the
                        // dialogue, then read back what was said.
                        companion.Visual.controller = null;
                        companion.Shadow.FaceToward(target.Tile);
                        try
                        {
                            target.checkAction(companion.Shadow, location);
                            string spoken = target.CurrentDialogue.Count > 0
                                ? target.CurrentDialogue.Peek().getCurrentDialogue()
                                : null;
                            success = true;
                            detail = spoken != null
                                ? $"{target.Name}: {spoken}"
                                : $"Talked to {target.Name} (no dialogue returned)";
                        }
                        catch (Exception ex)
                        {
                            detail = $"Talk to {target.Name} failed: {ex.Message}";
                        }
                        break;
                    }

                    // --- Interact ---
                    case "interact":
                    {
                        int tx = root.GetProperty("x").GetInt32();
                        int ty = root.GetProperty("y").GetInt32();
                        var tile = new Vector2(tx, ty);
                        var location = companion.Visual.currentLocation ?? Game1.player.currentLocation;

                        // Try harvest
                        if (location.terrainFeatures.TryGetValue(tile, out var feature)
                            && feature is StardewValley.TerrainFeatures.HoeDirt dirt
                            && dirt.crop != null && dirt.readyForHarvest())
                        {
                            success = CompanionActions.HarvestTile(location, tile, this.monitor);
                            detail = success ? $"Harvested at ({tx},{ty})" : "Harvest failed";
                        }
                        // Try pick up item
                        else if (location.objects.TryGetValue(tile, out var obj))
                        {
                            if (obj.Name != null && (obj.Name.Contains("Ladder") || obj.Name.Contains("Shaft")))
                            {
                                // Descend mine
                                if (location is StardewValley.Locations.MineShaft shaft)
                                {
                                    int nextLevel = shaft.mineLevel + 1;
                                    string nextName = "UndergroundMine" + nextLevel;
                                    var nextLoc = Game1.getLocationFromName(nextName)
                                        ?? StardewValley.Locations.MineShaft.GetMine(nextName);
                                    if (nextLoc != null)
                                    {
                                        companion.WarpTo(nextLoc.Name, 6, 6);
                                        success = true;
                                        detail = $"Descended to mine level {nextLevel}";
                                    }
                                    else
                                    {
                                        detail = $"Can't find mine level {nextLevel}";
                                    }
                                }
                            }
                            else if (obj is StardewValley.Objects.Chest chest)
                            {
                                // Return chest contents as the command result
                                var items = chest.Items.Where(i => i != null).Select(i => new
                                {
                                    name = i.DisplayName ?? i.Name,
                                    stack = i.Stack,
                                    qualifiedId = i.QualifiedItemId
                                }).ToList();
                                success = true;
                                detail = $"Chest at ({tx},{ty}): {items.Count} items";
                                this.commandResults[companionName] = new { action, success, detail, items };
                                this.monitor.Log($"{companionName}: {detail}", LogLevel.Info);
                                return; // early return — custom result
                            }
                            else
                            {
                                // Try to check/interact with the object using companion's shadow farmer
                                success = obj.checkForAction(companion.Shadow);
                                detail = success ? $"Interacted with {obj.DisplayName}" : $"Can't interact with {obj.DisplayName}";
                            }
                        }
                        else
                        {
                            detail = $"Nothing to interact with at ({tx},{ty})";
                        }
                        break;
                    }

                    // --- Fishing ---
                    case "cast_fishing_rod":
                    {
                        success = ai.StartFishing();
                        detail = success ? "Cast fishing rod" : "Failed to cast";
                        break;
                    }

                    // --- Combat toggle ---
                    case "set_auto_combat":
                    {
                        bool enabled = root.GetProperty("enabled").GetBoolean();
                        ai.AutoCombat = enabled;
                        success = true;
                        detail = $"Auto-combat {(enabled ? "enabled" : "disabled")}";
                        this.SaveState();
                        break;
                    }

                    // --- Inventory ---
                    case "eat_item":
                    {
                        int slot = root.TryGetProperty("slot", out var slotProp) ? slotProp.GetInt32() : -1;
                        var items = companion.Shadow.Items;
                        StardewValley.Object food = null;

                        if (slot >= 0 && slot < items.Count && items[slot] is StardewValley.Object o1 && o1.Edibility > 0)
                            food = o1;
                        else
                            food = items.FirstOrDefault(i => i is StardewValley.Object o && o.Edibility > 0) as StardewValley.Object;

                        if (food != null)
                        {
                            try
                            {
                                companion.Shadow.eatObject(food);
                                success = true;
                                detail = $"Ate {food.DisplayName}";
                            }
                            catch (Exception eatEx)
                            {
                                // eatObject may access UI elements on headless farmer
                                // Fall back to direct stamina/health restoration
                                companion.Shadow.Stamina = Math.Min(companion.Shadow.MaxStamina, companion.Shadow.Stamina + food.Edibility);
                                companion.Shadow.health = Math.Min(companion.Shadow.maxHealth, companion.Shadow.health + (int)(food.Edibility * 0.4f));

                                // Consume the food item (eatObject normally handles this)
                                food.Stack--;
                                if (food.Stack <= 0)
                                {
                                    int idx = items.IndexOf(food);
                                    if (idx >= 0) items[idx] = null;
                                }

                                success = true;
                                detail = $"Ate {food.DisplayName} (manual restore, eatObject failed: {eatEx.Message})";
                            }
                        }
                        else
                        {
                            detail = "No edible items in inventory";
                        }
                        break;
                    }

                    // --- Speech (companion-targeted) ---
                    case "say":
                    {
                        string text = root.TryGetProperty("message", out var sayMsg) ? sayMsg.GetString() : null;
                        bool popup = root.TryGetProperty("popup", out var sayPopup) && sayPopup.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (!string.IsNullOrEmpty(text))
                        {
                            this.Say(companionName, text, popup);
                            success = true;
                            detail = popup ? $"Said with dialogue box: {text}" : $"Said: {text}";
                        }
                        else
                        {
                            detail = "say requires a message";
                        }
                        break;
                    }

                    default:
                        detail = $"Unknown companion command: {action}";
                        this.monitor.Log($"{companionName}: {detail}", LogLevel.Warn);
                        break;
                }
            }
            catch (Exception ex)
            {
                detail = $"Command error: {ex.Message}";
                this.monitor.Log($"{companionName}: {action} failed: {ex.Message}", LogLevel.Error);
            }

            this.commandResults[companionName] = new { action, success, detail };
            this.monitor.Log($"{companionName}: {action} — {detail}", LogLevel.Info);
        }

        // ======================
        // GLOBAL HELPERS
        // ======================

        private void StopCompanionTasks(CompanionAI ai, string reason)
        {
            ai.Jobs.Cancel(reason);
            ai.Care.Cancel(reason);
            ai.Walker.Cancel();
            ai.Companion.Visual.controller = null;
        }

        private static List<Vector2> ParseCareTiles(System.Text.Json.JsonElement root, string chest, string filter)
        {
            var tiles = ParseTiles(root);
            if (tiles.Count > 0 || !TryParseTilePair(chest, out var start) || !TryParseTilePair(filter, out var end))
                return tiles;

            int left = Math.Min((int)start.X, (int)end.X);
            int right = Math.Max((int)start.X, (int)end.X);
            int top = Math.Min((int)start.Y, (int)end.Y);
            int bottom = Math.Max((int)start.Y, (int)end.Y);
            for (int y = top; y <= bottom && tiles.Count <= WorkLimits.MaxTiles; y++)
                for (int x = left; x <= right && tiles.Count <= WorkLimits.MaxTiles; x++)
                    tiles.Add(new Vector2(x, y));
            return tiles;
        }

        private static bool TryParseTilePair(string value, out Vector2 tile)
        {
            tile = Vector2.Zero;
            var parts = value?.Split(',');
            if (parts == null || parts.Length != 2
                || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y))
                return false;
            tile = new Vector2(x, y);
            return true;
        }

        private static bool TilesInsideMap(GameLocation location, IEnumerable<Vector2> tiles, out string error)
        {
            var layer = location?.map?.GetLayer("Back");
            if (layer == null)
            {
                error = $"Location '{location?.Name}' has no Back map layer";
                return false;
            }

            foreach (var tile in tiles)
            {
                int x = (int)tile.X, y = (int)tile.Y;
                if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight)
                {
                    error = $"Tile ({x},{y}) is outside {location.Name} map bounds (0..{layer.LayerWidth - 1}, 0..{layer.LayerHeight - 1})";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Tiles from a single (x,y), a region (x1,y1,x2,y2) or an explicit tiles array.
        /// Regions are normalised so the caller doesn't have to order the corners.
        /// </summary>
        private static List<Vector2> ParseTiles(System.Text.Json.JsonElement root)
        {
            var tiles = new List<Vector2>();

            if (root.TryGetProperty("tiles", out var tilesEl) && tilesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in tilesEl.EnumerateArray())
                {
                    if (tiles.Count > WorkLimits.MaxTiles) break;
                    if (entry.ValueKind == System.Text.Json.JsonValueKind.Array && entry.GetArrayLength() >= 2)
                    {
                        var parts = entry.EnumerateArray().ToArray();
                        tiles.Add(new Vector2(parts[0].GetInt32(), parts[1].GetInt32()));
                    }
                    else if (entry.ValueKind == System.Text.Json.JsonValueKind.Object
                        && entry.TryGetProperty("x", out var ex) && entry.TryGetProperty("y", out var ey))
                    {
                        tiles.Add(new Vector2(ex.GetInt32(), ey.GetInt32()));
                    }
                }
                return tiles;
            }

            if (root.TryGetProperty("x1", out var x1) && root.TryGetProperty("y1", out var y1)
                && root.TryGetProperty("x2", out var x2) && root.TryGetProperty("y2", out var y2))
            {
                int left = Math.Min(x1.GetInt32(), x2.GetInt32());
                int right = Math.Max(x1.GetInt32(), x2.GetInt32());
                int top = Math.Min(y1.GetInt32(), y2.GetInt32());
                int bottom = Math.Max(y1.GetInt32(), y2.GetInt32());
                for (int y = top; y <= bottom && tiles.Count <= WorkLimits.MaxTiles; y++)
                    for (int x = left; x <= right && tiles.Count <= WorkLimits.MaxTiles; x++)
                        tiles.Add(new Vector2(x, y));
                return tiles;
            }

            if (root.TryGetProperty("x", out var sx) && root.TryGetProperty("y", out var sy))
                tiles.Add(new Vector2(sx.GetInt32(), sy.GetInt32()));

            return tiles;
        }

        /// <summary>First walkable tile adjacent to <paramref name="tile"/>, or null.</summary>
        private static Vector2? GetAdjacentTile(GameLocation location, Vector2 tile)
        {
            var offsets = new[]
            {
                new Vector2(0, 1), new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, -1),
                new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1),
            };

            foreach (var offset in offsets)
            {
                var candidate = tile + offset;
                if (location.isTilePassable(new xTile.Dimensions.Location((int)candidate.X, (int)candidate.Y), Game1.viewport))
                    return candidate;
            }
            return null;
        }

        private void SetAllMode(CompanionMode mode)
        {
            foreach (var ai in this.companions.Values)
                ai.Mode = mode;
            this.monitor.Log($"All companions set to {mode}", LogLevel.Info);
            this.SaveState();
        }

        private void WaterAll()
        {
            var location = Game1.player.currentLocation;
            if (location == null) return;
            int count = 0;
            foreach (var pair in location.terrainFeatures.Pairs)
            {
                if (pair.Value is StardewValley.TerrainFeatures.HoeDirt dirt
                    && dirt.crop != null && dirt.state.Value != 1)
                {
                    if (CompanionActions.WaterTile(location, pair.Key, this.monitor))
                        count++;
                }
            }
            this.monitor.Log($"Watered {count} tiles", LogLevel.Info);
        }

        private void HarvestAll()
        {
            var location = Game1.player.currentLocation;
            if (location == null) return;
            int count = 0;
            foreach (var pair in location.terrainFeatures.Pairs.ToList())
            {
                if (pair.Value is StardewValley.TerrainFeatures.HoeDirt dirt
                    && dirt.crop != null && dirt.readyForHarvest())
                {
                    if (CompanionActions.HarvestTile(location, pair.Key, this.monitor))
                        count++;
                }
            }
            this.monitor.Log($"Harvested {count} crops", LogLevel.Info);
        }
    }
}
