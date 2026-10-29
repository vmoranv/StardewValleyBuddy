// AI combat state machine. This file: GPL-3.0-or-later. See LICENSE.txt.
using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Pathfinding;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCPBridge
{
    /// <summary>
    /// Close-quarters combat for a companion: closes the gap, swings when in weapon
    /// reach, and backs off / heals when health gets low.
    ///
    /// The previous implementation only faced the nearest monster and swung from
    /// wherever it stood, using a 256px "nearest monster" radius that is far wider
    /// than melee reach — so it whiffed constantly and never moved or healed.
    /// </summary>
    public class CompanionCombat : ICompanionCombat
    {
        /// <summary>Melee reach in tiles. A sword hits the tile in front of the farmer.</summary>
        private const float AttackRangeTiles = 1.35f;

        /// <summary>Stop chasing past this distance; the monster is no longer our problem.</summary>
        private const float DisengageRangeTiles = 8f;

        /// <summary>Below this health fraction the companion retreats and eats instead of fighting.</summary>
        private const float RetreatHealthFraction = 0.30f;

        /// <summary>Health fraction that ends a retreat.</summary>
        private const float SafeHealthFraction = 0.60f;

        private const int SwingCooldownMin = 12;   // ~0.2s at 60fps, weapon swing animation
        private const int SwingCooldownMax = 34;   // slower weapons
        private const int RepathCooldown = 10;

        private readonly CompanionFarmer companion;
        private readonly IMonitor monitor;

        private int swingCooldown = 0;
        private int repathCooldown = 0;
        private bool retreating = false;
        private int noMonsterTicks = 0;

        public CompanionCombat(CompanionFarmer companion, IMonitor monitor)
        {
            this.companion = companion;
            this.monitor = monitor;
        }

        /// <summary>True while the companion is actively engaging or retreating.</summary>
        public bool InCombat { get; private set; }

        /// <summary>Human-readable state for bridge/status output.</summary>
        public string Status { get; private set; } = "idle";

        public float HealthFraction =>
            this.companion.Shadow.maxHealth > 0
                ? (float)this.companion.Shadow.health / this.companion.Shadow.maxHealth
                : 1f;

        /// <summary>
        /// Run one combat tick. Returns false when there is nothing to fight, so the
        /// caller can fall back to its normal mode behaviour.
        /// </summary>
        public bool Tick()
        {
            if (this.swingCooldown > 0) this.swingCooldown--;
            if (this.repathCooldown > 0) this.repathCooldown--;

            this.companion.SyncFromNpc();
            var location = this.companion.Shadow.currentLocation;
            if (location == null) return false;

            // Low health always wins over fighting: break off, eat, and only re-engage
            // once we're back to a safe level.
            if (this.HealthFraction < RetreatHealthFraction)
                this.retreating = true;
            else if (this.retreating && this.HealthFraction >= SafeHealthFraction)
                this.retreating = false;

            if (this.retreating)
            {
                this.InCombat = true;
                this.Status = "retreating (low health)";
                if (this.TryHeal()) return true;
                this.MoveAwayFromMonsters(location);
                return true;
            }

            Monster target = this.FindTarget(location);
            if (target == null)
            {
                this.noMonsterTicks++;
                // Require a couple of quiet ticks before declaring victory, so a brief
                // gap between spawns doesn't thrash the status.
                if (this.noMonsterTicks > 60)
                {
                    this.InCombat = false;
                    this.Status = "idle";
                }
                return false;
            }

            this.noMonsterTicks = 0;
            this.InCombat = true;

            var npc = this.companion.Visual;
            float distance = Vector2.Distance(npc.Tile, target.Tile);

            if (distance > DisengageRangeTiles)
            {
                this.Status = "disengaged";
                this.InCombat = false;
                return false;
            }

            if (distance > AttackRangeTiles)
            {
                // Close the gap. Pathfinding every tick would thrash the controller,
                // so it runs on a short cooldown.
                this.Status = $"closing on {target.Name} ({distance:F1} tiles)";
                if (this.repathCooldown <= 0)
                {
                    this.MoveToward(location, target.Tile);
                    this.repathCooldown = RepathCooldown;
                }
                return true;
            }

            // In reach: face the monster and swing. Never stand inside its tile.
            this.Status = $"attacking {target.Name}";
            npc.controller = null;

            if (Vector2.Distance(npc.Tile, target.Tile) < 0.9f)
            {
                this.StepBack(location, target.Tile);
                return true;
            }

            this.companion.Shadow.FaceToward(target.Tile);
            npc.FacingDirection = this.companion.Shadow.FacingDirection;

            if (this.swingCooldown <= 0 && this.companion.AttackTarget(target))
                this.swingCooldown = this.GetSwingCooldown();

            return true;
        }

        /// <summary>
        /// Nearest living monster within disengage range. Uses tile distance the same
        /// way the swing does, so "found" and "can hit" agree.
        /// </summary>
        private Monster FindTarget(GameLocation location)
        {
            var origin = this.companion.Visual.Tile;
            Monster best = null;
            float bestDist = DisengageRangeTiles;

            foreach (var character in location.characters)
            {
                if (character is Monster candidate && !candidate.IsInvisible && candidate.Health > 0)
                {
                    float dist = Vector2.Distance(origin, candidate.Tile);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = candidate;
                    }
                }
            }

            return best;
        }

        private int GetSwingCooldown()
        {
            var weapon = this.companion.Shadow.Items.OfType<MeleeWeapon>().FirstOrDefault();
            // Longer, heavier weapons take longer to swing again.
            float speed = weapon?.speed.Value ?? 0;
            int cooldown = (int)(SwingCooldownMin + Math.Max(0, -speed) * 2);
            return Math.Min(SwingCooldownMax, Math.Max(SwingCooldownMin, cooldown));
        }

        /// <summary>Path one step toward the monster (reuses the game's pathfinder).</summary>
        private void MoveToward(GameLocation location, Vector2 targetTile)
        {
            var npc = this.companion.Visual;
            try
            {
                npc.controller = new PathFindController(
                    npc, location, new Point((int)targetTile.X, (int)targetTile.Y), 2);
            }
            catch
            {
                // Unreachable (monster on a ledge, etc.) — give up this tick and let
                // the AI decide again next frame rather than teleporting into a mob.
                npc.controller = null;
                this.Status = "target unreachable";
            }
        }

        /// <summary>Step to an adjacent passable tile away from the monster.</summary>
        private void StepBack(GameLocation location, Vector2 monsterTile)
        {
            var npc = this.companion.Visual;
            var myTile = npc.Tile;
            Vector2 away = myTile - monsterTile;

            // Prefer the dominant axis, then the other one, then any adjacent tile.
            var candidates = new[]
            {
                myTile + new Vector2(Math.Sign(away.X), 0),
                myTile + new Vector2(0, Math.Sign(away.Y)),
                myTile + new Vector2(-Math.Sign(away.Y), Math.Sign(away.X)),
                myTile + new Vector2(Math.Sign(away.Y), -Math.Sign(away.X)),
            };

            foreach (var tile in candidates)
            {
                if (tile == myTile) continue;
                if (!location.isTilePassable(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport)) continue;
                if (tile == monsterTile) continue;

                npc.Position = tile * 64f;
                this.companion.SyncFromNpc();
                return;
            }
        }

        /// <summary>Retreat from the closest monster's direction.</summary>
        private void MoveAwayFromMonsters(GameLocation location)
        {
            var monster = this.FindTarget(location);
            if (monster == null) return;
            this.StepBack(location, monster.Tile);
        }

        /// <summary>
        /// Eat the best healing food we carry. Returns true if something was eaten
        /// (or if the companion has nothing left to try, so the caller stops stalling).
        /// </summary>
        private bool TryHeal()
        {
            int missing = this.companion.Shadow.maxHealth - this.companion.Shadow.health;
            if (missing <= 0) return true;

            var shadow = this.companion.Shadow;
            int bestIndex = -1;
            int bestHeal = 0;

            for (int i = 0; i < shadow.Items.Count; i++)
            {
                if (shadow.Items[i] is not SObject obj) continue;
                var heal = obj.Edibility > 0 ? obj.Edibility * 2 : 0;
                if (heal <= 0) continue;
                if (heal > bestHeal)
                {
                    bestHeal = heal;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                this.Status = "retreating (no food)";
                return false;
            }

            try
            {
                var food = shadow.Items[bestIndex];
                var edible = new SObject(food.ItemId, food.Stack);
                shadow.eatObject(edible, true);
                if (food.Stack > 1) food.Stack--;
                else shadow.Items[bestIndex] = null;

                this.monitor.Log($"{this.companion.Name}: ate {food.DisplayName} to recover health", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                this.monitor.Log($"{this.companion.Name}: failed to eat: {ex.Message}", LogLevel.Warn);
                return false;
            }
        }

        /// <summary>Reset transient combat state (mode change, warp, new day).</summary>
        public void Reset()
        {
            this.swingCooldown = 0;
            this.repathCooldown = 0;
            this.retreating = false;
            this.noMonsterTicks = 0;
            this.InCombat = false;
            this.Status = "idle";
        }
    }
}
