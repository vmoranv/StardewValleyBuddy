// Intent-level work tasks over tile regions. GPL-3.0-or-later. See LICENSE.txt.
//
// The agent says WHAT it wants ("clear this area", "water this field"); the runner
// decides HOW per tile — which tool, whether the tile is already done, when to go
// refill the can. Exposing per-tile operations to the agent was the old design, and
// it put the decision where it doesn't belong: the agent can't see the tile, the mod can.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCPBridge
{


    /// <summary>What the runner decided to do at one tile.</summary>
    internal enum TileAction
    {
        SwingAxe, SwingPickaxe, SwingHoe, SwingScythe,
        Water, Plant, ShakeBush, HarvestCrop, HarvestFruit, Nothing,
    }

    /// <summary>
    /// Executes a <see cref="WorkTaskState"/> one tile at a time across many ticks,
    /// deciding per tile what actually needs doing and handling its own logistics
    /// (refilling the watering can, walking to the next tile).
    /// </summary>
    public class CompanionJobRunner : ICompanionJobs
    {
        private const float ReachTiles = 1.45f;
        private const int StuckTickLimit = 600;        // ~10s without any progress
        private const int StalledWalkTickLimit = 120;  // ~2s of standing still while "walking"
        private const int RepathCooldown = 8;
        private const int RefillTickLimit = 900;

        public int MaxTiles => WorkLimits.MaxTiles;

        private readonly CompanionFarmer companion;
        private readonly IMonitor monitor;
        private object lastSnapshot;

        private int ticksOnTile;
        /// <summary>Last recorded distance to the current tile, used to detect real progress.</summary>
        private float lastDistanceToTarget = float.MaxValue;
        private int repathCooldown;
        private int actionCooldown;

        /// <summary>When set, the runner is walking to a water source to refill.</summary>
        private Vector2? refillTarget;

        /// <summary>Frame-driven walking along an A* path (vanilla controller is unreliable on MP hosts).</summary>
        private readonly NpcWalker walker;

        /// <summary>Last time a tile resolved (done/skip/fail) — job watchdog reference.</summary>
        private DateTime lastProgress = DateTime.UtcNow;
        /// <summary>How many times each tile was deferred as unreachable.</summary>
        private readonly Dictionary<string, int> deferCount = new Dictionary<string, int>();
        /// <summary>Recent movement heading, for the scheduler's momentum term.</summary>
        private Vector2 lastHeading = Vector2.UnitX;
        private Vector2 headingSamplePosition = Vector2.Zero;
        /// <summary>Net displacement sampler for the anti-spin watchdog.</summary>
        private Vector2 spinCheckPosition = Vector2.Zero;
        private DateTime spinCheckTime = DateTime.UtcNow;
        private Vector2 lastStepPosition = Vector2.Zero;
        /// <summary>Tiles effectively walked during this job.</summary>
        private double tilesWalked;
        /// <summary>Bedtime gate already handled for the current night.</summary>
        private bool bedtimeHandled;
        private int lastSeenDay = -1;
        private int refillTicks;

        public WorkTaskState Current { get; private set; }

        public CompanionJobRunner(CompanionFarmer companion, IMonitor monitor)
        {
            this.companion = companion;
            this.monitor = monitor;
            this.walker = new NpcWalker(companion.Visual);
        }

        /// <summary>Start a task, replacing whatever was running.</summary>
        public WorkTaskState Start(WorkTask task, string locationName, IEnumerable<Vector2> tiles,
            string seedId = null, string sellFilter = null)
        {
            if (this.Current != null)
                this.Cancel("replaced by new work");
            this.walker.Cancel();
            this.companion.Visual.controller = null;

            var state = new WorkTaskState
            {
                Id = $"{task.ToString().ToLowerInvariant()}-{DateTime.UtcNow.Ticks}",
                Task = task,
                LocationName = locationName,
                SeedId = seedId,
                SellFilter = sellFilter,
                StartedAtUtc = DateTime.UtcNow,
            };

            var from = this.companion.Visual.Tile;
            var requested = tiles?.Distinct().ToList() ?? new List<Vector2>();
            // Nearest first: reachable work happens up front, and tiles that turn out
            // to be walled off sink to the tail instead of stalling the whole job.
            foreach (var tile in requested.OrderBy(t => Vector2.Distance(from, t)))
            {
                if (state.Remaining.Count >= MaxTiles) break;
                state.Remaining.Add(tile);
            }
            state.RequestedTiles.AddRange(state.Remaining);
            state.TotalTiles = state.Remaining.Count;

            this.Current = state;
            this.lastProgress = DateTime.UtcNow;
            this.deferCount.Clear();
            this.tilesWalked = 0;
            this.itemsCollected = 0;
            this.lastStepPosition = this.companion.Visual.Tile;
            this.spinCheckPosition = this.companion.Visual.Tile;
            this.spinCheckTime = DateTime.UtcNow;
            this.ResetTile();
            this.monitor.Log($"{this.companion.Name}: {state.Id} started — {task} over {state.TotalTiles} tile(s) in {locationName}", LogLevel.Info);
            return state;
        }

        public void Cancel(string reason)
        {
            if (this.Current == null)
            {
                this.walker.Cancel();
                this.companion.Visual.controller = null;
                return;
            }
            this.Current.AbortReason = reason;
            this.Current.PendingAtStop.AddRange(this.Current.Remaining);
            this.Current.Remaining.Clear();
            this.monitor.Log($"{this.companion.Name}: {this.Current.Id} cancelled — {reason}", LogLevel.Info);
            this.Finish();
        }

        /// <summary>Advance one tick. Returns true while the task is still running.</summary>
        public bool Tick()
        {
            var job = this.Current;
            if (job == null) return false;

            if (job.AbortReason != null || (job.IsFinished && job.Task != WorkTask.Sell))
            {
                this.Finish();
                return false;
            }

            this.CheckJobWatchdog(job);
            if (this.Current == null) return false;

            // 1:50 AM hard curfew: abort whatever we're doing, follow the player home
            // and put the shadow to sleep — at 2600 the game force-passes everyone and
            // an awake shadow deadlocks the save.
            // New day → re-arm the bedtime gate without any shell involvement.
            if (Game1.dayOfMonth != this.lastSeenDay)
            {
                this.lastSeenDay = Game1.dayOfMonth;
                this.bedtimeHandled = false;
            }

            if (Game1.timeOfDay >= 2550 && !this.bedtimeHandled)
            {
                this.bedtimeHandled = true;
                this.Cancel("bedtime");
                this.companion.Shadow.SignalSleepReady();
                // Per-companion offset — both warping onto the same tile stacks their
                // bounding boxes and seals each other's A* start tile.
                var bedOffset = this.companion.Name == "Companion2" ? 2 : 1;
                this.companion.WarpTo(Game1.player.currentLocation?.Name ?? "FarmHouse",
                    (int)Game1.player.Tile.X + bedOffset, (int)Game1.player.Tile.Y);
                this.monitor.Log($"{this.companion.Name}: bedtime — following the player home", LogLevel.Info);
                return false;
            }

            this.companion.SyncFromNpc();
            var shadow = this.companion.Shadow;

            if (this.walker.Active)
            {
                if (this.walker.Tick(this.companion.Visual.currentLocation, Game1.currentGameTime))
                    return true;

                // Walled in mid-route: whatever blocked us IS the work for a clearing
                // clear task only when it belongs to the requested region.
                var hit = this.walker.BlockedTile;
                this.walker.Cancel();
                this.companion.Visual.controller = null;
                if (hit.HasValue && job.Task == WorkTask.ClearArea
                    && job.RequestedTiles.Contains(hit.Value)
                    && job.Reroutes < WorkLimits.MaxReroutes
                    && this.IsClearableAt(this.companion.Visual.currentLocation, hit.Value))
                {
                    job.Remaining.Remove(hit.Value);
                    job.Remaining.Insert(0, hit.Value);
                    job.Reroutes++;
                    this.ResetTile();
                    this.monitor.Log($"{this.companion.Name}: route blocked at {hit} — clearing it first", LogLevel.Debug);
                    return true;
                }
                if (hit.HasValue)
                {
                    job.BlockedBy = job.Task == WorkTask.ClearArea
                        && job.RequestedTiles.Contains(hit.Value)
                        && this.IsClearableAt(this.companion.Visual.currentLocation, hit.Value)
                        ? $"reroute limit reached at {(int)hit.Value.X},{(int)hit.Value.Y}"
                        : $"{(int)hit.Value.X},{(int)hit.Value.Y}";
                    this.FailCurrentTile(job, $"blocked by {job.BlockedBy}");
                    return job.IsFinished ? this.EndOrContinue() : true;
                }
                // Otherwise fall through so re-pathfinding / stall logic takes over.
            }

            // Debris sweep runs on a cadence of its own: drops fly after a tree falls,
            // so collection must not be tied to the instant a tile resolves.
            if ((DateTime.UtcNow - this.lastDebrisSweep).TotalSeconds >= 2)
            {
                this.lastDebrisSweep = DateTime.UtcNow;
                this.CollectNearbyDebris();
            }

            // Selling doesn't involve walking tiles at all.
            if (job.Task == WorkTask.Sell)
            {
                this.SellInventory(job);
                job.Remaining.Clear();
                this.Finish();
                return false;
            }

            if (shadow.Stamina <= 0)
            {
                job.AbortReason = "out of energy";
                this.Finish();
                return false;
            }

            // Refilling outranks everything: a dry can turns every water tile into a
            // failure, which used to show up to the agent as a wall of "failed".
            if (this.refillTarget.HasValue && this.TryRefill(shadow))
                return true;

            if (this.actionCooldown > 0) { this.actionCooldown--; return true; }
            if (this.repathCooldown > 0) this.repathCooldown--;

            var tile = job.Remaining[0];
            var tileKey = $"{tile.X},{tile.Y}";
            if (job.CurrentTile != tileKey)
                job.CurrentTileSwings = 0;
            job.CurrentTile = tileKey;

            var location = this.ResolveLocation(job.LocationName);
            if (location == null)
            {
                job.AbortReason = $"location '{job.LocationName}' not found";
                this.Finish();
                return false;
            }

            if (this.companion.Visual.currentLocation?.Name != location.Name)
            {
                // The agent said "work this area", not "and also get there first". Move
                // the companion next to its first work tile instead of bouncing the
                // problem back as an error it can only fix by issuing a manual warp.
                var landing = FindWalkableAdjacent(location, job.Remaining[0]) ?? job.Remaining[0];
                this.companion.WarpTo(location.Name, (int)landing.X, (int)landing.Y);
                this.monitor.Log($"{this.companion.Name}: moved to {location.Name} for the task", LogLevel.Info);
                this.ResetTile();
                return true;
            }

            if (Vector2.Distance(this.companion.Visual.Tile, tile) > ReachTiles)
            {
                if (this.repathCooldown <= 0)
                {
                    if (!this.WalkTo(location, tile))
                    {
                        // No route. For a clearing task the blocker IS work:
                        // clear whatever stands between here and the tile, then retry.
                        if (this.TryClearPathTo(job, location, tile)) return true;

                        // A stone field seals paths in layers — a tile that is
                        // unreachable NOW opens up once neighbours get cleared. Defer
                        // to the back of the queue instead of failing it forever.
                        var key = $"{tile.X},{tile.Y}";
                        var attempts = this.deferCount.GetValueOrDefault(key) + 1;
                        this.deferCount[key] = attempts;
                        job.Remaining.RemoveAt(0);
                        if (attempts < 3)
                        {
                            job.Remaining.Add(tile);   // retry later — paths may have opened
                            this.ResetTile();
                            return true;
                        }

                        this.deferCount.Remove(key);
                        job.Progress.Failed.Add(new { x = tile.X, y = tile.Y, reason = job.BlockedBy == null ? "unreachable" : $"blocked by {job.BlockedBy}" });
                        this.lastProgress = DateTime.UtcNow;
                        this.ResetTile();
                        this.monitor.Log($"{this.companion.Name}: {job.CurrentTile} unreachable after {attempts} deferrals", LogLevel.Debug);
                        return job.IsFinished ? this.EndOrContinue() : true;
                    }
                    this.repathCooldown = RepathCooldown;
                }
                // Distance is the progress signal: a path that covers 20 tiles takes
                // longer than one that covers 2, so a fixed tick budget would abandon
                // legitimate walks.
                if (this.ticksOnTile % 60 == 0)
                    this.monitor.Log($"{this.companion.Name}: walking to {tile} — at {this.companion.Visual.Tile}, d={Vector2.Distance(this.companion.Visual.Tile, tile):F2}, controller={(this.companion.Visual.controller != null)}", LogLevel.Trace);
                this.NoteNoProgress(job, "walking", Vector2.Distance(this.companion.Visual.Tile, tile));
                return true;
            }

            this.companion.Visual.controller = null;

            if (this.refillTarget.HasValue)
            {
                // A water tile asked for a refill this tick; don't act on it yet.
                return true;
            }

            var outcome = this.RunTile(location, tile, job);

            if (job.AbortReason != null)
            {
                this.Finish();
                return false;
            }

            if (outcome == TileOutcome.Failed)
            {
                // A tree takes several swings, so failure is usually just "not yet".
                this.NoteNoProgress(job, job.LastResult ?? "retrying");
                return true;
            }

            if (outcome == TileOutcome.Done)
                job.Progress.Done.Add(new { x = tile.X, y = tile.Y, what = job.LastResult });
            else
                job.Progress.Skipped.Add(new { x = tile.X, y = tile.Y, reason = job.LastResult });
            this.lastProgress = DateTime.UtcNow;

            job.Remaining.RemoveAt(0);
            this.deferCount.Remove($"{tile.X},{tile.Y}");
            this.ResetTile();

            // EEVDF-lite replanning: deadline = euclidean distance − heading momentum
            // (±2 tiles) − starvation aging (up to 5 tiles). Pathfind only when the
            // selected tile is actually next; probing 15 candidates after every tile
            // multiplied A* work without improving the route execution.
            if (job.Remaining.Count > 2)
            {
                var here = this.companion.Visual.Tile;
                var heading = this.lastHeading == Vector2.Zero
                    ? Vector2.UnitX
                    : Vector2.Normalize(this.lastHeading);

                var aged = job.Remaining
                    .Select((t, index) => new { t, index })
                    .OrderBy(x =>
                    {
                        float dist = Vector2.Distance(here, x.t);
                        var dir = x.t - here;
                        dir = dir == Vector2.Zero ? Vector2.UnitX : Vector2.Normalize(dir);
                        float momentum = Vector2.Dot(heading, dir) * 2f;
                        float starvation = Math.Min(x.index / 10f, 5f);
                        return dist - momentum - starvation;
                    })
                    .Select(x => x.t)
                    .ToList();

                job.Remaining.Clear();
                job.Remaining.AddRange(aged);
            }

            this.CollectNearbyDebris();

            if (!job.IsFinished) return true;
            this.Finish();
            return false;
        }

        /// <summary>
        /// Nearest clearable tile (tree / rock / weed / bush / clump) blocking the route.
        /// The wall usually sits next to the TARGET, not next to us — so scan around the
        /// target first, then around the companion, preferring the shortest detour.
        /// Returns null when nothing clearable is standing around.
        /// </summary>
        private Vector2? FindBlockingObstacle(GameLocation location, Vector2 target, WorkTaskState job)
        {
            var from = this.companion.Visual.Tile;
            Vector2? best = null;
            Vector2? bestOutsideScope = null;
            float bestScore = float.MaxValue;
            float bestOutsideScore = float.MaxValue;
            const int radius = 3;

            // Target-adjacent zone first (the wall), then companion surroundings.
            foreach (var (origin, rad) in new[] { (target, radius), (from, radius) })
            for (int dy = -rad; dy <= rad; dy++)
            {
                for (int dx = -rad; dx <= rad; dx++)
                {
                    var candidate = new Vector2(origin.X + dx, origin.Y + dy);
                    if (candidate == target) continue;
                    if (!this.IsClearableAt(location, candidate)) continue;
                    // Must be workable: we need to stand next to it.
                    var stand = FindWalkableAdjacent(location, candidate);
                    if (!stand.HasValue) continue;

                    // Score = distance from companion + distance from candidate onward to
                    // the target, so we chew through obstacles in the right direction.
                    float score = Vector2.Distance(from, candidate)
                                + Vector2.Distance(candidate, target);
                    if (job.RequestedTiles.Contains(candidate))
                    {
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                    else if (score < bestOutsideScore)
                    {
                        bestOutsideScore = score;
                        bestOutsideScope = candidate;
                    }
                }
            }
            if (!best.HasValue && bestOutsideScope.HasValue)
                job.BlockedBy = $"{(int)bestOutsideScope.Value.X},{(int)bestOutsideScope.Value.Y} outside requested area";
            return best;
        }

        /// <summary>
        /// Can the companion physically stand on this tile? Checks objects, terrain
        /// features AND the map layers — isTilePassable() alone misses trees and rocks.
        /// </summary>
        private static bool IsTileStandable(GameLocation location, Vector2 tile)
        {
            if (location.objects.ContainsKey(tile)) return false;
            if (location.terrainFeatures.TryGetValue(tile, out var feature) && !feature.isPassable())
                return false;
            return location.isTilePassable(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport);
        }

        private bool IsClearableAt(GameLocation location, Vector2 tile)
        {
            if (location.terrainFeatures.TryGetValue(tile, out var feature)
                && (feature is Tree || feature is ResourceClump
                    || feature is Grass || feature is Bush))
                return true;
            if (location.objects.TryGetValue(tile, out var obj))
                return IsStone(obj) || IsWeedObject(obj);
            return false;
        }

        private bool EndOrContinue()
        {
            var job = this.Current;
            if (job == null || !job.IsFinished) return true;
            this.Finish();
            return false;
        }

        private void NoteNoProgress(WorkTaskState job, string state, float distance = float.NaN)
        {
            // Walking counts as progress while the companion keeps getting closer, so a
            // long legitimate path isn't mistaken for a stuck one. Once the distance
            // stops improving, the tick budget below applies.
            if (!float.IsNaN(distance) && distance < this.lastDistanceToTarget - 0.05f)
            {
                this.lastDistanceToTarget = distance;
                this.ticksOnTile = 0;
                return;
            }
            if (!float.IsNaN(distance)) this.lastDistanceToTarget = distance;

            this.ticksOnTile++;
            // Walking with zero distance change for 2s means the path is bogus in
            // practice — don't sit through the full retry budget before rerouting.
            int limit = state == "walking" ? StalledWalkTickLimit : StuckTickLimit;
            if (this.ticksOnTile <= limit) return;

            // A resolved path can still be unwalkable in practice (an object A* counts
            // as passable, another character parked on the route). Before abandoning the
            // tile, try clearing whatever is in the way — same rule as no-route.
            var location = this.ResolveLocation(job.LocationName);
            if (location != null && (state == "walking" || state == "retrying")
                && this.TryClearPathTo(job, location, job.Remaining[0]))
                return;

            job.Progress.Failed.Add(new { x = job.Remaining[0].X, y = job.Remaining[0].Y, reason = $"no progress ({state})" });
            this.monitor.Log($"{this.companion.Name}: giving up on {job.CurrentTile} after {StuckTickLimit} ticks ({state})", LogLevel.Debug);
            job.Remaining.RemoveAt(0);
            this.ResetTile();
        }

        /// <summary>
        /// Queue the nearest clearable obstacle ahead of the tile we can't reach.
        /// Returns true when a blocker was queued (the caller just keeps ticking).
        /// </summary>
        private bool TryClearPathTo(WorkTaskState job, GameLocation location, Vector2 tile)
        {
            // Guard against ping-pong: two mutually "blocking" tiles would otherwise
            // re-queue each other forever. A blocker must (a) be reachable from where
            // we stand right now, (b) not already be queued, and (c) fit the reroute
            // budget. Anything else means the tile is genuinely out of reach.
            if (job.Task != WorkTask.ClearArea) return false;
            if (job.Reroutes >= WorkLimits.MaxReroutes)
            {
                job.BlockedBy = "reroute limit reached";
                return false;
            }

            var blocker = this.FindBlockingObstacle(location, tile, job);
            if (!blocker.HasValue) return false;
            if (job.Remaining.Contains(blocker.Value))
            {
                job.Remaining.Remove(blocker.Value);
                job.Remaining.Insert(0, blocker.Value);
                job.Reroutes++;
                this.ResetTile();
                return true;
            }
            if (!this.WalkTo(location, blocker.Value))
            {
                job.BlockedBy = $"{(int)blocker.Value.X},{(int)blocker.Value.Y}";
                return false;
            }

            int index = job.Remaining.IndexOf(tile);
            if (index < 0) index = 0;
            job.Remaining.RemoveAt(index);
            job.Remaining.Insert(0, blocker.Value);
            job.Remaining.Insert(1, tile);
            job.Reroutes++;
            this.ResetTile();
            this.monitor.Log($"{this.companion.Name}: {tile} blocked by {blocker} — clearing it first", LogLevel.Debug);
            return true;
        }

        private void ResetTile()
        {
            this.ticksOnTile = 0;
            this.lastDistanceToTarget = float.MaxValue;
            this.repathCooldown = 0;
            this.actionCooldown = 0;
        }

        private void FailCurrentTile(WorkTaskState job, string reason)
        {
            if (job.Remaining.Count == 0) return;
            var tile = job.Remaining[0];
            job.Progress.Failed.Add(new { x = (int)tile.X, y = (int)tile.Y, reason });
            job.LastResult = reason;
            job.Remaining.RemoveAt(0);
            this.lastProgress = DateTime.UtcNow;
            this.ResetTile();
        }

        /// <summary>Items picked up this job (telemetry).</summary>
        private int itemsCollected;
        private DateTime lastDebrisSweep = DateTime.MinValue;

        /// <summary>
        /// Pick up item drops near the companion, per chunk. Debris has no public
        /// position — every chunk carries its own — and resource drops (wood/stone/
        /// fiber) are CHUNKS debris where `item` stays null and the id lives in
        /// `itemId`. The old check matched neither, so nothing was ever collected.
        /// </summary>
        private void CollectNearbyDebris()
        {
            var location = this.companion.Visual.currentLocation;
            if (location == null) return;
            var pos = this.companion.Visual.Position;
            const float pickupRadius = 160f;   // 2.5 tiles

            foreach (var debris in location.debris.ToList())
            {
                if (debris == null || debris.Chunks.Count == 0) continue;

                string itemId = debris.itemId.Value;
                Item directItem = debris.item;

                if (debris.debrisType.Value != StardewValley.Debris.DebrisType.CHUNKS && directItem == null)
                    continue;

                foreach (var chunk in debris.Chunks.ToList())
                {
                    if (Vector2.Distance(chunk.position.Value, pos) > pickupRadius) continue;
                    try
                    {
                        Item toGive = directItem?.getOne() ?? new SObject(itemId, 1);
                        if (!this.companion.Shadow.addItemToInventoryBool(toGive, false)) continue;
                        debris.Chunks.Remove(chunk);
                        this.itemsCollected++;
                    }
                    catch { /* chunk mid-flight — next sweep */ }
                }

                if (debris.Chunks.Count == 0)
                    location.debris.Remove(debris);
            }
        }

                /// <summary>Abort when the job as a whole hasn't resolved a single tile recently.</summary>
        private void CheckJobWatchdog(WorkTaskState job)
        {
            if (job.StartedAtUtc != default
                && (DateTime.UtcNow - job.StartedAtUtc).TotalSeconds > WorkLimits.MaxJobDurationSeconds)
            {
                this.Cancel($"job exceeded {WorkLimits.MaxJobDurationSeconds / 60}-minute limit");
                return;
            }
            if ((DateTime.UtcNow - this.lastProgress).TotalSeconds <= 90) return;
            if (job.DoneCount + job.SkippedCount + job.FailedCount == 0) return; // still walking to first tile
            this.Cancel("job stalled — no tile resolved in 90s");
        }

        private void Finish()
        {
            var job = this.Current;
            if (job == null) return;

            job.FinishedAtUtc = DateTime.UtcNow;
            this.walker.Cancel();
            this.companion.Visual.controller = null;
            this.refillTarget = null;

            this.monitor.Log(
                $"{this.companion.Name}: {job.Id} finished — done {job.DoneCount}, skipped {job.SkippedCount}, failed {job.FailedCount}, swings {job.Progress.Swings}, refills {job.Progress.Refills}"
                + (job.AbortReason != null ? $" (aborted: {job.AbortReason})" : ""),
                LogLevel.Info);

            this.lastSnapshot = this.BuildSnapshot(job, active: false);
            this.Current = null;
            this.ResetTile();
        }

        /// <summary>New day: re-arm the bedtime gate (called via day-start job state).</summary>
        public void NewDay()
        {
            this.bedtimeHandled = false;
        }

        private GameLocation ResolveLocation(string name)
        {
            return string.IsNullOrEmpty(name)
                ? this.companion.Visual.currentLocation ?? Game1.player?.currentLocation
                : Game1.getLocationFromName(name);
        }

        /// <summary>
        /// Path to a tile. Work targets are usually occupied by the very thing we're
        /// clearing (a tree, a rock), and A* refuses impassable destinations — so when
        /// the target can't be stood on, aim for the closest reachable neighbour instead.
        /// </summary>
        private bool WalkTo(GameLocation location, Vector2 tile)
        {
            // NOTE: the map-only isTilePassable() can't see trees/rocks (they are
            // terrain features/objects), and vanilla findPath refuses destinations that
            // fail the FULL collision check — a tree tile as endpoint means NO PATH for
            // the whole job. Test standability the way A* does.
            var destination = IsTileStandable(location, tile)
                ? tile
                : FindWalkableAdjacent(location, tile) ?? tile;

            // Vanilla constructor plans the route (proven to work on this farm where the
            // static findPath call returns null for reasons the decompile doesn't show);
            // NpcWalker executes it frame by frame — assigning it to npc.controller froze
            // the NPC solid on multiplayer hosts, so we never do that.
            var controller = new StardewValley.Pathfinding.PathFindController(
                this.companion.Visual, location, new Point((int)destination.X, (int)destination.Y), 2);

            if (controller.pathToEndPoint == null || controller.pathToEndPoint.Count == 0)
            {
                // One-shot dump: why is A* returning nothing?
                var start = new Point((int)this.companion.Visual.Tile.X, (int)this.companion.Visual.Tile.Y);
                bool ownTileBlocked = location.isCollidingPosition(
                    new Rectangle(start.X * 64 + 1, start.Y * 64 + 1, 62, 62), Game1.viewport,
                    false, 0, glider: false, this.companion.Visual, pathfinding: true);
                var staticProbe = new StardewValley.Pathfinding.PathFindController(
                    new StardewValley.NPC(new AnimatedSprite("Characters", 0, 16, 32),
                        new Vector2(start.X * 64f, start.Y * 64f), 2, "Probe"),
                    location, new Point((int)destination.X, (int)destination.Y), 2);
                this.monitor.Log(
                    $"{this.companion.Name}: WalkTo({tile}) NO PATH — from={start} dest={destination}"
                    + $" ownTileBlocked={ownTileBlocked}"
                    + $" probePath={(staticProbe.pathToEndPoint?.Count ?? -1)}"
                    + $" chars={location.characters.Count} master={Game1.IsMasterGame}",
                    LogLevel.Trace);
                return false;
            }

            this.walker.Start(controller.pathToEndPoint);
            this.monitor.Log($"{this.companion.Name}: WalkTo({tile}) path={controller.pathToEndPoint.Count} from={this.companion.Visual.Tile}", LogLevel.Trace);
            return true;
        }

        // =====================================================================
        // PER-TILE DECISION + EXECUTION
        // =====================================================================

        /// <summary>
        /// Decide what this tile needs. This is the piece that used to live in the
        /// agent's head ("is that a rock or a bush?").
        /// </summary>
        private TileAction DecideAction(GameLocation location, Vector2 tile, WorkTaskState job)
        {
            TerrainFeature feature = location.terrainFeatures.TryGetValue(tile, out var tf) ? tf : null;
            SObject obj = location.objects.TryGetValue(tile, out var o) ? o : null;

            bool hasTree = feature is Tree;
            bool hasFruitTree = feature is FruitTree;
            bool hasClump = feature is ResourceClump;
            bool hasGrass = feature is Grass;
            bool hasBush = feature is Bush;
            bool hasDirt = feature is HoeDirt;

            switch (job.Task)
            {
                case WorkTask.ClearArea:
                    if (hasTree) return TileAction.SwingAxe;
                    if (hasClump) return TileAction.SwingPickaxe;
                    if (hasBush) return TileAction.ShakeBush;
                    if (hasGrass) return TileAction.SwingScythe;
                    if (obj != null && IsStone(obj)) return TileAction.SwingPickaxe;
                    if (obj != null && IsWeedObject(obj)) return TileAction.SwingScythe;
                    return TileAction.Nothing;

                case WorkTask.Chop:
                    return hasTree ? TileAction.SwingAxe : TileAction.Nothing;

                case WorkTask.Mine:
                    if (hasClump) return TileAction.SwingPickaxe;
                    return obj != null && IsStone(obj) ? TileAction.SwingPickaxe : TileAction.Nothing;

                case WorkTask.Water:
                    if (!hasDirt) return TileAction.Nothing;
                    return ((HoeDirt)feature).state.Value == HoeDirt.watered
                        ? TileAction.Nothing
                        : TileAction.Water;

                case WorkTask.Plant:
                    if (hasDirt)
                    {
                        var dirt = (HoeDirt)feature;
                        if (dirt.crop != null)
                            return dirt.state.Value == HoeDirt.watered ? TileAction.Nothing : TileAction.Water;
                        return TileAction.Plant;
                    }
                    return obj == null ? TileAction.SwingHoe : TileAction.Nothing;

                case WorkTask.Harvest:
                    if (hasDirt && ((HoeDirt)feature).readyForHarvest()) return TileAction.HarvestCrop;
                    if (hasBush && ((Bush)feature).readyForHarvest()) return TileAction.ShakeBush;
                    if (hasFruitTree && ((FruitTree)feature).fruit.Count > 0) return TileAction.HarvestFruit;
                    return TileAction.Nothing;
            }

            return TileAction.Nothing;
        }

        private TileOutcome RunTile(GameLocation location, Vector2 tile, WorkTaskState job)
        {
            switch (this.DecideAction(location, tile, job))
            {
                case TileAction.Nothing:
                    return Skip(job, "nothing to do");

                case TileAction.SwingAxe:
                    return this.SwingToolAt(location, tile, typeof(Axe), job, "tree");

                case TileAction.SwingPickaxe:
                    return this.SwingToolAt(location, tile, typeof(Pickaxe), job, "rock");

                case TileAction.SwingHoe:
                    return CompanionActions.HoeTile(location, tile, this.monitor)
                        ? Done(job, "tilled")
                        : Fail(job, "not diggable");

                case TileAction.SwingScythe:
                    return this.SwingScytheAt(location, tile, job);

                case TileAction.Water:
                    return this.WaterTile(location, tile, job);

                case TileAction.Plant:
                    return this.PlantSeed(location, tile, job);

                case TileAction.ShakeBush:
                    return CompanionActions.HarvestTile(location, tile, this.monitor)
                        ? Done(job, "shook bush")
                        : Fail(job, "bush not ready");

                case TileAction.HarvestCrop:
                    return CompanionActions.HarvestTile(location, tile, this.monitor)
                        ? Done(job, "harvested")
                        : Fail(job, "nothing ready");

                case TileAction.HarvestFruit:
                    return CompanionActions.HarvestTile(location, tile, this.monitor)
                        ? Done(job, "picked fruit")
                        : Fail(job, "fruit not ready");
            }

            return Fail(job, "unknown action");
        }

        // =====================================================================
        // ACTIONS
        // =====================================================================

        /// <summary>
        /// Swing a tool until the tile is clear. Trees take several hits; the call that
        /// leaves the tile empty is reported as Done.
        /// </summary>
        private TileOutcome SwingToolAt(GameLocation location, Vector2 tile, Type toolType,
            WorkTaskState job, string label)
        {
            var shadow = this.companion.Shadow;
            var tool = shadow.Items.FirstOrDefault(i => i != null && toolType.IsInstanceOfType(i)) as Tool;
            if (tool == null) return Fail(job, $"no {label} tool");

            return this.SwingOnce(location, tile, tool, job, label);
        }

        /// <summary>Scythe is a MeleeWeapon with id 47; weeds fall to it, not to a sword.</summary>
        private TileOutcome SwingScytheAt(GameLocation location, Vector2 tile, WorkTaskState job)
        {
            var shadow = this.companion.Shadow;
            var scythe = shadow.Items.OfType<MeleeWeapon>()
                .FirstOrDefault(w => w.QualifiedItemId == "(W)47" || w.Name?.Contains("Scythe") == true);
            if (scythe == null) return Fail(job, "no scythe");

            // Grass feature → normal swing; weed OBJECT → cutWeed directly (vanilla
            // refuses the starter scythe with QualifiedItemId == "(W)47", but the agent
            // doesn't care about scythe-tier gating).
            if (!location.terrainFeatures.TryGetValue(tile, out var feature) || feature is not Grass)
            {
                if (location.objects.TryGetValue(tile, out var obj))
                {
                    if (job.CurrentTileSwings >= WorkLimits.MaxTileSwings)
                        return Skip(job, $"tile swing limit reached ({WorkLimits.MaxTileSwings})");
                    if (shadow.Stamina <= WorkLimits.StaminaReserve)
                    {
                        job.AbortReason = "stamina reserve reached";
                        return Fail(job, job.AbortReason);
                    }
                    job.Progress.Swings++;
                    job.CurrentTileSwings++;
                    shadow.FaceToward(tile);
                    try
                    {
                        obj.cutWeed(shadow);
                        // cutWeed only plays debris/drops fiber — vanilla's caller removes
                        // the object afterwards.
                        location.objects.Remove(tile);
                        shadow.checkForExhaustion(shadow.Stamina);
                    }
                    catch (Exception ex) { return Fail(job, ex.Message); }

                    return Done(job, "cleared weeds");
                }
            }

            return this.SwingOnce(location, tile, scythe, job, "weeds");
        }

        private TileOutcome SwingOnce(GameLocation location, Vector2 tile, Tool tool, WorkTaskState job, string label)
        {
            var shadow = this.companion.Shadow;
            if (job.CurrentTileSwings >= WorkLimits.MaxTileSwings)
                return Skip(job, $"tile swing limit reached ({WorkLimits.MaxTileSwings})");
            if (shadow.Stamina <= WorkLimits.StaminaReserve)
            {
                job.AbortReason = "stamina reserve reached";
                return Fail(job, job.AbortReason);
            }
            job.Progress.Swings++;
            job.CurrentTileSwings++;

            this.companion.Visual.FacingDirection = this.FaceDirection(tile);
            shadow.FaceToward(tile);
            float oldStamina = shadow.Stamina;

            try
            {
                if (location.terrainFeatures.TryGetValue(tile, out var feature))
                {
                    switch (feature)
                    {
                        case Grass grass:
                            // Scythe path: performToolAction decrements the weed counter;
                            // vanilla's caller removes the feature once it's spent.
                            try { grass.performToolAction(tool, 1, tile); } catch { }
                            if (grass.numberOfWeeds.Value <= 0)
                                location.terrainFeatures.Remove(tile);
                            break;

                        case Tree tree when tree.growthStage.Value < 5:
                            // Saplings: Tree.performToolAction ignores them (return false
                            // for growthStage < 5), so a swing would loop forever. Pop.
                            location.terrainFeatures.Remove(tile);
                            break;

                        default:
                            // Trees/rocks/clumps: the tool's own DoFunction — it stamps
                            // lastUser (performToolAction needs it for debris/exp) and
                            // dispatches the feature handler with correct arguments.
                            tool.DoFunction(shadow.currentLocation, (int)(tile.X * 64f), (int)(tile.Y * 64f), 1, shadow);
                            break;
                    }
                }
                else
                {
                    tool.DoFunction(shadow.currentLocation, (int)(tile.X * 64f), (int)(tile.Y * 64f), 1, shadow);
                }
                shadow.checkForExhaustion(oldStamina);
            }
            catch (Exception ex)
            {
                return Fail(job, ex.Message);
            }

            // Gone after this swing → the swing finished it.
            if (!this.TileStillHasTarget(location, tile))
                return Done(job, $"cleared {label}");

            if (shadow.Stamina <= 0) return Fail(job, "out of energy");

            job.LastResult = $"swinging at {label}{this.HealthHint(location, tile)}";
            this.actionCooldown = 15;   // swing pace, not every frame
            return TileOutcome.Failed;
        }

        private bool TileStillHasTarget(GameLocation location, Vector2 tile)
        {
            if (location.objects.ContainsKey(tile)) return true;
            if (!location.terrainFeatures.TryGetValue(tile, out var feature)) return false;
            return feature is Tree || feature is FruitTree || feature is ResourceClump
                || feature is Grass || feature is Bush;
        }

        private string HealthHint(GameLocation location, Vector2 tile)
        {
            if (!location.terrainFeatures.TryGetValue(tile, out var tf)) return "";
            if (tf is Tree tree) return $" (tree hp {tree.health.Value:F0})";
            if (tf is ResourceClump clump) return $" (rock hp {clump.health.Value:F0})";
            return "";
        }

        /// <summary>Water one tile, deferring to a refill trip when the can runs dry.</summary>
        private TileOutcome WaterTile(GameLocation location, Vector2 tile, WorkTaskState job)
        {
            var can = this.companion.Shadow.Items.OfType<WateringCan>().FirstOrDefault();
            if (can == null) return Fail(job, "no watering can");

            if (can.WaterLeft <= 0)
            {
                this.SeekWaterSource(location);
                job.LastResult = "refilling watering can";
                return TileOutcome.Failed;   // keep this tile at the head of the queue
            }

            if (!CompanionActions.WaterTile(location, tile, this.monitor))
                return Fail(job, "nothing to water");

            can.WaterLeft--;
            return Done(job, "watered");
        }

        private TileOutcome PlantSeed(GameLocation location, Vector2 tile, WorkTaskState job)
        {
            var shadow = this.companion.Shadow;
            var seed = shadow.Items.FirstOrDefault(i => i is SObject o && string.Equals(o.Name, job.SeedId, StringComparison.OrdinalIgnoreCase));
            if (seed == null) return Fail(job, $"no '{job.SeedId}' in inventory");

            if (!location.terrainFeatures.TryGetValue(tile, out var tf) || tf is not HoeDirt dirt)
                return Fail(job, "not tilled");
            if (dirt.crop != null) return Skip(job, "already planted");

            try
            {
                dirt.crop = new Crop(seed.ItemId, (int)tile.X, (int)tile.Y, location);
                int index = shadow.Items.IndexOf(seed);
                if (seed.Stack > 1) seed.Stack--;
                else shadow.Items[index] = null;
            }
            catch (Exception ex)
            {
                return Fail(job, $"plant failed: {ex.Message}");
            }

            return Done(job, $"planted {job.SeedId}");
        }

        // =====================================================================
        // WATER REFILL
        // =====================================================================

        private void SeekWaterSource(GameLocation location)
        {
            this.refillTarget = FindNearestWater(location, this.companion.Visual.Tile, 24);
            this.refillTicks = 0;
            this.monitor.Log(this.refillTarget.HasValue
                ? $"{this.companion.Name}: can empty — walking to water at {this.refillTarget}"
                : $"{this.companion.Name}: can empty and no water within 24 tiles",
                this.refillTarget.HasValue ? LogLevel.Debug : LogLevel.Warn);
        }

        /// <summary>Walk to the water tile and fill. Returns true while still busy.</summary>
        private bool TryRefill(BotFarmer shadow)
        {
            var target = this.refillTarget.Value;
            var location = this.companion.Visual.currentLocation;
            if (location == null) { this.refillTarget = null; return false; }

            if (++this.refillTicks > RefillTickLimit)
            {
                this.monitor.Log($"{this.companion.Name}: couldn't reach water — aborting task", LogLevel.Warn);
                this.refillTarget = null;
                if (this.Current != null) this.Current.AbortReason = "could not reach a water source";
                return false;
            }

            var adjacent = FindWalkableAdjacent(location, target);
            if (!adjacent.HasValue) { this.refillTarget = null; return false; }

            if (Vector2.Distance(this.companion.Visual.Tile, adjacent.Value) > 1.2f)
            {
                if (this.repathCooldown <= 0)
                {
                    this.WalkTo(location, adjacent.Value);
                    this.repathCooldown = RepathCooldown;
                }
                this.repathCooldown--;
                return true;
            }

            this.companion.Visual.controller = null;
            var can = shadow.Items.OfType<WateringCan>().FirstOrDefault();
            if (can != null)
            {
                can.WaterLeft = can.waterCanMax;
                if (this.Current != null) this.Current.Progress.Refills++;
                this.monitor.Log($"{this.companion.Name}: refilled watering can to {can.WaterLeft}", LogLevel.Debug);
            }

            this.refillTarget = null;
            this.refillTicks = 0;
            this.repathCooldown = 0;
            return false;   // done refilling this tick; next tick resumes the tile
        }

        private static Vector2? FindNearestWater(GameLocation location, Vector2 from, int radius)
        {
            Vector2? best = null;
            float bestDist = float.MaxValue;

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = (int)from.X + dx;
                    int y = (int)from.Y + dy;
                    if (!location.isWaterTile(x, y)) continue;

                    float dist = dx * dx + dy * dy;
                    if (dist < bestDist) { bestDist = dist; best = new Vector2(x, y); }
                }
            }
            return best;
        }

        private Vector2? FindWalkableAdjacent(GameLocation location, Vector2 tile)
        {
            var offsets = new[]
            {
                new Vector2(0, -1), new Vector2(0, 1), new Vector2(-1, 0), new Vector2(1, 0),
                new Vector2(-1, -1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(1, 1),
            };
            foreach (var offset in offsets)
            {
                var candidate = tile + offset;
                // Full standability check — the map-only isTilePassable can't see
                // trees/weed objects, and a "standable" tile that isn't sends A*
                // into the same wall we're trying to route around.
                if (IsTileStandable(location, candidate))
                    return candidate;
            }
            return null;
        }

        // =====================================================================
        // SELL
        // =====================================================================

        /// <summary>Move matching inventory into the farm's shipping bin.</summary>
        private void SellInventory(WorkTaskState job)
        {
            var farm = Game1.getFarm();
            if (farm == null) { job.AbortReason = "no farm"; return; }

            var shadow = this.companion.Shadow;
            int sold = 0;

            for (int i = 0; i < shadow.Items.Count; i++)
            {
                if (shadow.Items[i] is not SObject obj || shadow.Items[i] is Tool) continue;
                if (!MatchesSellFilter(obj, job.SellFilter)) continue;

                try
                {
                    farm.shipItem(obj, shadow);
                    shadow.Items[i] = null;
                    sold++;
                }
                catch (Exception ex)
                {
                    this.monitor.Log($"{this.companion.Name}: shipping {obj.Name} failed: {ex.Message}", LogLevel.Warn);
                }
            }

            job.Progress.Done.Add(new { shipped = sold });
            this.monitor.Log($"{this.companion.Name}: shipped {sold} item stack(s) to the bin", LogLevel.Info);
        }

        private static bool MatchesSellFilter(SObject obj, string filter)
        {
            switch ((filter ?? "all").ToLowerInvariant())
            {
                // Category constants verified against SObject: vegetables -75,
                // fruits -79, flowers -80, greens -81; fish -4; gems -2 / minerals -12.
                case "crops": return obj.Category == SObject.VegetableCategory || obj.Category == SObject.FruitsCategory
                                  || obj.Category == SObject.flowersCategory || obj.Category == SObject.GreensCategory;
                case "forage": return obj.isForage();
                case "fish": return obj.Category == SObject.FishCategory;
                case "minerals": return obj.Category == SObject.GemCategory || obj.Category == SObject.mineralsCategory;
                case "all":
                default: return obj.canBeShipped();
            }
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        private static bool IsStone(SObject obj)
        {
            string name = obj.Name ?? "";
            return name.Contains("Stone") || name.Contains("Ore") || name.Contains("Geode") || name.Contains("Boulder");
        }

        private static bool IsWeedObject(SObject obj)
        {
            string name = obj.Name ?? "";
            return name.Contains("Weed") || name.Contains("Fiber") || name.Contains("Twig");
        }

        private int FaceDirection(Vector2 tile)
        {
            var diff = tile - this.companion.Visual.Tile;
            return Math.Abs(diff.X) > Math.Abs(diff.Y) ? (diff.X > 0 ? 1 : 3) : (diff.Y > 0 ? 2 : 0);
        }

        private static TileOutcome Done(WorkTaskState job, string what) { job.LastResult = what; return TileOutcome.Done; }
        private static TileOutcome Skip(WorkTaskState job, string why) { job.LastResult = why; return TileOutcome.Skipped; }
        private static TileOutcome Fail(WorkTaskState job, string why) { job.LastResult = why; return TileOutcome.Failed; }

        /// <summary>Serializable snapshot for bridge_data / get_job.</summary>
        public object Snapshot()
        {
            var job = this.Current;
            if (job == null) return this.lastSnapshot ?? (object)new { active = false };
            return this.BuildSnapshot(job, active: true);
        }

        private object BuildSnapshot(WorkTaskState job, bool active)
        {
            var remainingTiles = job.Remaining.Count > 0
                ? job.Remaining.ToList()
                : job.PendingAtStop.ToList();
            object requestedRegion = job.RequestedTiles.Count == 0 ? null : new
            {
                x1 = (int)job.RequestedTiles.Min(t => t.X),
                y1 = (int)job.RequestedTiles.Min(t => t.Y),
                x2 = (int)job.RequestedTiles.Max(t => t.X),
                y2 = (int)job.RequestedTiles.Max(t => t.Y),
            };
            return new
            {
                active,
                status = active ? "running" : job.AbortReason == null ? "completed" : "aborted",
                id = job.Id,
                task = job.Task.ToString(),
                location = job.LocationName,
                total = job.TotalTiles,
                done = job.DoneCount,
                skipped = job.SkippedCount,
                failed = job.FailedCount,
                requestedTiles = job.RequestedTiles.Count,
                requestedRegion,
                remaining = active ? job.Remaining.Count : remainingTiles.Count,
                remainingTiles = active ? null : remainingTiles,
                currentTile = job.CurrentTile,
                lastResult = job.LastResult,
                blockedBy = job.BlockedBy,
                abortReason = job.AbortReason,
                startedAt = job.StartedAtUtc,
                finishedAt = job.FinishedAtUtc,
                refilling = this.refillTarget.HasValue,
                swings = job.Progress.Swings,
                tilesWalked = Math.Round(this.tilesWalked, 1),
                itemsCollected = this.itemsCollected,
                refills = job.Progress.Refills,
                skippedDetails = job.Progress.Skipped.Take(20).ToList(),
                failedDetails = job.Progress.Failed.Take(20).ToList(),
            };
        }
    }
}
