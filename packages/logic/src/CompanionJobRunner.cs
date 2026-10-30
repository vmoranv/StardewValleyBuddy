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

    /// <summary>Insert an item into a shadow farmer inventory without vanilla player lookups.</summary>
    internal static class CompanionInventory
    {
        public static bool TryAdd(BotFarmer farmer, Item item)
        {
            if (farmer == null || item == null || item.Stack <= 0)
                return false;

            var stackSlots = new List<(int Index, int Free)>();
            var emptySlots = new List<int>();
            int capacity = 0;
            int itemMaxStack = Math.Max(1, item.maximumStackSize());

            for (int i = 0; i < farmer.Items.Count; i++)
            {
                var existing = farmer.Items[i];
                if (existing == null)
                {
                    emptySlots.Add(i);
                    capacity += itemMaxStack;
                    continue;
                }

                if (!existing.canStackWith(item))
                    continue;

                int free = Math.Max(0, existing.maximumStackSize() - existing.Stack);
                if (free == 0) continue;

                stackSlots.Add((i, free));
                capacity += free;
            }

            if (capacity < item.Stack)
                return false;

            int remaining = item.Stack;
            foreach (var slot in stackSlots)
            {
                int amount = Math.Min(slot.Free, remaining);
                farmer.Items[slot.Index].Stack += amount;
                remaining -= amount;
                if (remaining == 0) return true;
            }

            foreach (int index in emptySlots)
            {
                if (remaining == 0) break;

                Item stored = item.getOne();
                int amount = Math.Min(itemMaxStack, remaining);
                stored.Stack = amount;
                farmer.Items[index] = stored;
                remaining -= amount;
            }

            return remaining == 0;
        }
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
        private const float DebrisPickupRadius = 160f;
        private const int DebrisChaseTickLimit = 600;   // ~10s per chunk before giving up
        // Sleep-recovery model: 20s of napping restores a full bar; each nap is a
        // half-bar slice, so the work/nap duty cycle is EEVDF-budgeted (work until the
        // reserve floor, sleep exactly as long as the slice needs, resume).
        private const double NapFullRestoreSeconds = 20.0;
        private const float NapSliceFraction = 0.5f;
        private float napWakeStamina;

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
        /// <summary>A* destinations that failed this job, with failure counts — after 2
        /// failures the zone is marked and further tiles routed there skip instantly.</summary>
        private readonly Dictionary<string, int> unreachableDestinations = new Dictionary<string, int>();
        /// <summary>Tiles written off as unreachable this job: tile → reason. Reported in the snapshot.</summary>
        private readonly Dictionary<string, string> markedUnreachable = new Dictionary<string, string>();
        /// <summary>Clump origin tiles the current tools cannot break — excluded from reroutes.</summary>
        private readonly HashSet<string> unbreakable = new HashSet<string>(System.StringComparer.Ordinal);
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
        /// <summary>When set, the bot is napping at home until this instant (stamina recovery).</summary>
        private DateTime? napUntilUtc;

        // Overnight resume: the remaining tiles of a job interrupted by bedtime (or the
        // player sleeping early) are stashed and the job auto-restarts next morning.
        private WorkTask? resumeTask;
        private string resumeLocation;
        private string resumeSeedId;
        private string resumeSellFilter;
        private List<Vector2> resumeTiles;
        private DateTime resumeAtUtc = DateTime.MinValue;
        /// <summary>Consecutive swings that didn't dent the target — tool-tier bail-out.</summary>
        private int zeroDamageStreak;
        /// <summary>Ticks spent walking toward one residual debris chunk.</summary>
        private int debrisChaseTicks;

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
            if (task == WorkTask.ClearArea)
            {
                this.lastClearAreaJob = state;
                this.debrisPickupUntilUtc = DateTime.UtcNow + DebrisPickupGracePeriod;
                this.lastDebrisSweep = DateTime.MinValue;
            }
            this.lastProgress = DateTime.UtcNow;
            this.napUntilUtc = null;
            this.deferCount.Clear();
            this.unreachableDestinations.Clear();
            this.markedUnreachable.Clear();
            this.unbreakable.Clear();
            this.InvalidateClumpCache();
            this.debrisChaseTicks = 0;
            this.zeroDamageStreak = 0;
            this.tilesWalked = 0;
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
            if (job == null)
            {
                var idleNow = DateTime.UtcNow;
                if (this.TryResumeInterruptedJob(idleNow)) return true;
                if (this.ChaseResidualDebris(idleNow)) return true;
                this.SweepNearbyDebrisIfDue(idleNow);
                return false;
            }

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

            // Late evening: keep the resume snapshot fresh so a manual early sleep
            // (before the 2550 curfew) still preserves the remaining tiles.
            if (Game1.timeOfDay >= 2200 && job.AbortReason == null)
                this.StashResume(job, DateTime.UtcNow);

            // Dynamic curfew (DP-style budgeting): every couple of ticks we A*-cost
            // the route home and stop working when [now + travel] reaches the 2550
            // hard curfew. The final leg is still a warp fallback, so nothing can
            // strand the bot past bedtime even if the path estimate was optimistic.
            int travel = this.TravelHomeMinutes(job);
            if (Game1.timeOfDay + travel >= 2550 && !this.bedtimeHandled)
            {
                this.bedtimeHandled = true;
                this.StashResume(job, DateTime.UtcNow);
                this.resumeAtUtc = DateTime.UtcNow.AddSeconds(20);
                this.Cancel("bedtime");
                this.companion.Shadow.SignalSleepReady();
                // Per-companion offset — both warping onto the same tile stacks their
                // bounding boxes and seals each other's A* start tile.
                var bedOffset = this.companion.Name == "Companion2" ? 2 : 1;
                this.companion.WarpTo(Game1.player.currentLocation?.Name ?? "FarmHouse",
                    (int)Game1.player.Tile.X + bedOffset, (int)Game1.player.Tile.Y);
                this.monitor.Log($"{this.companion.Name}: bedtime — travel home costs ~{travel} min, heading back", LogLevel.Info);
                return false;
            }

            this.companion.SyncFromNpc();
            var shadow = this.companion.Shadow;
            var now = DateTime.UtcNow;

            // Napping: instead of aborting on empty stamina, the bot goes home, sleeps
            // a few seconds, wakes up restored and resumes the SAME job.
            if (this.napUntilUtc != null)
            {
                if (now < this.napUntilUtc.Value) return true;
                shadow.Stamina = this.napWakeStamina;
                this.napUntilUtc = null;
                this.ResetTile();
                this.monitor.Log($"{this.companion.Name}: woke up — stamina {this.napWakeStamina:0}, resuming {job.Task}", LogLevel.Info);
            }

            if (job.Task == WorkTask.ClearArea)
                this.debrisPickupUntilUtc = now + DebrisPickupGracePeriod;
            this.SweepNearbyDebrisIfDue(now);

            if (this.walker.Active)
            {
                if (this.walker.Tick(this.companion.Visual.currentLocation, Game1.currentGameTime))
                    return true;

                // Walled in mid-route: whatever blocked us IS the work for a clearing
                // clear task only when it belongs to the requested region.
                var hit = this.walker.BlockedTile;
                this.walker.Cancel();
                this.companion.Visual.controller = null;
                this.monitor.Log(
                    $"{this.companion.Name}: walker finished at {this.companion.Visual.Tile}"
                    + $" pos={this.companion.Visual.Position} blocked={(hit.HasValue ? hit.ToString() : "null")}",
                    LogLevel.Trace);
                if (hit.HasValue)
                {
                    this.companion.Visual.currentLocation.terrainFeatures.TryGetValue(hit.Value, out var blockedFeature);
                    var nearbyChar = this.companion.Visual.currentLocation.characters
                        .Where(c => c != this.companion.Visual && Vector2.Distance(c.Tile, hit.Value) < 1.5f)
                        .Select(c => c.Name).ToList();
                    this.monitor.Log(
                        $"{this.companion.Name}: walker blocked at {hit} from {this.companion.Visual.Tile}"
                        + $" — object={this.companion.Visual.currentLocation.objects.ContainsKey(hit.Value)}"
                        + $" feature={(blockedFeature != null ? blockedFeature.GetType().Name : "none")}"
                        + $" charsNear=[{string.Join(",", nearbyChar)}]",
                        LogLevel.Trace);
                }
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
                    // Blocker outside the region: give the reroute logic one shot at
                    // clearing a path before writing the tile off as failed.
                    var blockedTile = job.Remaining.Count > 0 ? job.Remaining[0] : (Vector2?)null;
                    if (job.Task == WorkTask.ClearArea && blockedTile.HasValue
                        && job.Reroutes < WorkLimits.MaxReroutes
                        && this.TryClearPathTo(job, this.companion.Visual.currentLocation, blockedTile.Value))
                        return true;
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
                this.StartNap(job, "out of energy");
                return true;
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

            // The mod can see every tile from anywhere: if this tile has nothing to do,
            // resolve it WITHOUT walking there. Walking to empty tiles used to dominate
            // the job time (50+ pointless trips per region).
            if (this.DecideAction(location, tile, job) == TileAction.Nothing)
            {
                job.Progress.Skipped.Add(new { x = tile.X, y = tile.Y, reason = "nothing to do" });
                job.LastResult = "nothing to do";
                this.ResolveTile(job, tile);
                return job.IsFinished ? this.EndOrContinue() : true;
            }

            // Work target tiles are usually occupied by the very thing we're clearing,
            // so measure reach against the planned STANDING tile (the target itself or
            // its nearest standable neighbour). A neighbour two tiles out used to sit
            // forever outside the 1.45 reach and repath until the watchdog killed it.
            var workFrom = IsTileStandable(location, tile) ? tile : FindWalkableAdjacent(location, tile) ?? tile;

            // A marked unreachable zone: no point re-pathing or re-burning reroutes —
            // resolve in place instead of walking into the same wall again.
            if (this.unreachableDestinations.GetValueOrDefault($"{(int)workFrom.X},{(int)workFrom.Y}") >= 2)
            {
                job.LastResult = "unreachable (marked blocked zone)";
                job.Progress.Skipped.Add(new { x = tile.X, y = tile.Y, reason = job.LastResult });
                this.markedUnreachable[$"{(int)tile.X},{(int)tile.Y}"] = job.LastResult;
                this.ResolveTile(job, tile);
                this.ResetTile();
                return job.IsFinished ? this.EndOrContinue() : true;
            }

            if (Vector2.Distance(this.companion.Visual.Tile, workFrom) > ReachTiles)
            {
                // Standing inside an object (a stone/weed under our feet): A* refuses a
                // blocked start tile, so every route dies before its first step — clear
                // underfoot or hop aside before any pathing.
                if (this.ClearUnderfootOrStepAside(location, job)) return true;

                if (this.repathCooldown <= 0)
                {
                    if (!this.WalkTo(location, workFrom))
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
                        string unreachableReason = job.BlockedBy == null ? "unreachable" : $"blocked by {job.BlockedBy}";
                        job.Progress.Skipped.Add(new { x = tile.X, y = tile.Y, reason = unreachableReason });
                        this.markedUnreachable[$"{(int)tile.X},{(int)tile.Y}"] = unreachableReason;
                        this.lastProgress = DateTime.UtcNow;
                        this.ResetTile();
                        this.monitor.Log($"{this.companion.Name}: {job.CurrentTile} {unreachableReason} — marked after {attempts} deferrals", LogLevel.Debug);
                        return job.IsFinished ? this.EndOrContinue() : true;
                    }
                    this.repathCooldown = RepathCooldown;
                }
                // Distance is the progress signal: a path that covers 20 tiles takes
                // longer than one that covers 2, so a fixed tick budget would abandon
                // legitimate walks.
                if (this.ticksOnTile % 60 == 0)
                    this.monitor.Log($"{this.companion.Name}: walking to {workFrom} for {tile} — at {this.companion.Visual.Tile}, d={Vector2.Distance(this.companion.Visual.Tile, workFrom):F2}, controller={(this.companion.Visual.controller != null)}", LogLevel.Trace);
                this.NoteNoProgress(job, "walking", Vector2.Distance(this.companion.Visual.Tile, workFrom));
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
                // Pass a fixed state: LastResult strings like "swinging at tree…" never
                // matched the "walking"/"retrying" rescue in NoteNoProgress, so a swing
                // blocked by an obstacle used to idle out the full 600-tick budget.
                this.NoteNoProgress(job, "retrying");
                return true;
            }

            if (outcome == TileOutcome.Done)
                job.Progress.Done.Add(new { x = tile.X, y = tile.Y, what = job.LastResult });
            else
                job.Progress.Skipped.Add(new { x = tile.X, y = tile.Y, reason = job.LastResult });
            this.ResolveTile(job, tile);
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

            this.SweepNearbyDebrisIfDue(DateTime.UtcNow, force: true);

            if (!job.IsFinished) return true;
            this.Finish();
            return false;
        }

        /// <summary>
        /// Empty the bot inventory (tools excepted) into player chests, nearest chest
        /// first; when a chest is full move to the next in the queue, and when every
        /// chest is full place a new one next to the last. Chest coordinates are
        /// published in the job snapshot so the user/agent can rearrange them.
        /// </summary>
        private int DepositInventoryToChest()
        {
            var farm = Game1.getFarm();
            var shadow = this.companion.Shadow;
            if (farm == null || shadow == null) return 0;
            if (shadow.Items.All(i => i == null || i is StardewValley.Tool)) return 0;

            var queue = this.GetChestQueue();
            int deposited = 0;
            for (int i = 0; i < shadow.Items.Count; i++)
            {
                var item = shadow.Items[i];
                if (item == null || item is StardewValley.Tool) continue;
                if (queue.Count == 0)
                {
                    var created = this.PlaceNewChest(farm, new Vector2(68f, 15f));
                    if (created == null) break;
                    queue.Add(created);
                }

                bool stored = false;
                for (int q = 0; q < queue.Count; q++)
                {
                    try
                    {
                        var leftover = queue[q].addItem(item);
                        if (leftover == null)
                        {
                            shadow.Items[i] = null;
                            deposited++;
                            stored = true;
                            break;
                        }
                        if (!ReferenceEquals(leftover, item))
                        {
                            shadow.Items[i] = leftover;   // partially stacked; try the next chest for the rest
                        }
                    }
                    catch (Exception ex)
                    {
                        this.monitor.Log($"{this.companion.Name}: chest deposit of {item.Name} failed: {ex.Message}", LogLevel.Warn);
                    }
                }
                if (!stored && shadow.Items[i] != null)
                {
                    // every chest full: place one more and retry this item next pass
                    var created = this.PlaceNewChest(farm, queue.Count > 0 ? queue[queue.Count - 1].TileLocation : new Vector2(68f, 15f));
                    if (created != null) queue.Add(created);
                }
            }
            if (deposited > 0)
                this.monitor.Log($"{this.companion.Name}: deposited {deposited} stack(s) into {queue.Count} chest queue", LogLevel.Info);
            return deposited;
        }

        private StardewValley.Objects.Chest PlaceNewChest(GameLocation farm, Vector2 near)
        {
            foreach (var spot in new[]
                     {
                         near + new Vector2(1, 0), near + new Vector2(-1, 0), near + new Vector2(0, 1), near + new Vector2(0, -1),
                         near + new Vector2(2, 0), near + new Vector2(-2, 0), near + new Vector2(0, 2), near + new Vector2(0, -2),
                     })
            {
                if (!farm.objects.ContainsKey(spot) && this.IsTileStandable(farm, spot))
                {
                    var chest = new StardewValley.Objects.Chest(playerChest: true);
                    farm.objects[spot] = chest;
                    this.monitor.Log($"{this.companion.Name}: placed a deposit chest at ({(int)spot.X},{(int)spot.Y})", LogLevel.Info);
                    this.chestQueueScannedUtc = DateTime.MinValue;
                    return chest;
                }
            }
            return null;
        }

        /// <summary>Cache of player chests on the farm, nearest-first from last position.</summary>
        private List<StardewValley.Objects.Chest> chestQueue = new List<StardewValley.Objects.Chest>();
        private DateTime chestQueueScannedUtc = DateTime.MinValue;
        private DateTime travelCheckUtc = DateTime.MinValue;
        private Vector2 travelCheckTile = Vector2.Zero;
        private int travelHomeMinutes;

        /// <summary>In-game minutes (10 min per 7.17 real seconds) needed to reach the
        /// farmhouse from the current tile. A*-costed, re-estimated at most every 2s.</summary>
        private int TravelHomeMinutes(WorkTaskState job)
        {
            if (this.travelCheckUtc.AddSeconds(2) > DateTime.UtcNow
                && this.travelCheckTile == this.companion.Visual.Tile)
                return this.travelHomeMinutes;
            this.travelCheckUtc = DateTime.UtcNow;
            this.travelCheckTile = this.companion.Visual.Tile;

            var location = this.companion.Visual.currentLocation;
            if (location == null) { this.travelHomeMinutes = 30; return 30; }
            if (location.Name == "FarmHouse") { this.travelHomeMinutes = 0; return 0; }

            // Target: the warp on this map that leads back to the farmhouse.
            Point target = new Point(64, 15);   // vanilla Farm farmhouse-door fallback
            foreach (Warp warp in location.warps)
            {
                if (warp.TargetName == "FarmHouse") { target = new Point(warp.X, warp.Y); break; }
            }

            try
            {
                var controller = new StardewValley.Pathfinding.PathFindController(
                    this.companion.Visual, location, target, 2);
                int steps = controller.pathToEndPoint?.Count ?? 0;
                if (steps <= 0) { this.travelHomeMinutes = 60; return 60; }   // no route: assume far
                double seconds = steps / 3.0;                 // NpcWalker pace: 3 tiles/s
                this.travelHomeMinutes = System.Math.Max(10, (int)System.Math.Ceiling(seconds / 0.717));
            }
            catch
            {
                this.travelHomeMinutes = 60;
            }
            return this.travelHomeMinutes;
        }

        /// <summary>User-pinned deposit chest (Farm tile); the queue starts here when set.</summary>
        private Vector2? preferredChest;

        /// <inheritdoc/>
        public void SetDepositChest(int x, int y)
        {
            if (x < 0 || y < 0)
            {
                this.preferredChest = null;
                this.chestQueueScannedUtc = DateTime.MinValue;
                this.monitor.Log($"{this.companion.Name}: deposit chest pin cleared (nearest-first queue)", LogLevel.Info);
                return;
            }
            this.preferredChest = new Vector2(x, y);
            this.chestQueueScannedUtc = DateTime.MinValue;
            this.monitor.Log($"{this.companion.Name}: deposit chest pinned to ({x},{y})", LogLevel.Info);
        }

        /// <summary>Refresh the nearest-first chest queue (player chests on the farm).</summary>
        private List<StardewValley.Objects.Chest> GetChestQueue()
        {
            if (this.chestQueueScannedUtc.AddSeconds(30) > DateTime.UtcNow && this.chestQueue.Count > 0)
                return this.chestQueue;
            this.chestQueueScannedUtc = DateTime.UtcNow;
            var farm = Game1.getFarm();
            var origin = this.companion.Visual?.Tile ?? new Vector2(64f, 15f);
            this.chestQueue = farm == null
                ? new List<StardewValley.Objects.Chest>()
                : farm.objects.Values.OfType<StardewValley.Objects.Chest>()
                    .Where(c => c.playerChest.Value)
                    .OrderBy(c => Vector2.Distance(c.TileLocation, origin))
                    .ToList();
            // A pinned chest (stardew_set_deposit_chest) always leads the queue.
            if (this.preferredChest != null)
            {
                var pinned = this.chestQueue.FirstOrDefault(c => c.TileLocation == this.preferredChest.Value);
                if (pinned != null)
                {
                    this.chestQueue.Remove(pinned);
                    this.chestQueue.Insert(0, pinned);
                }
            }
            return this.chestQueue;
        }

        /// <summary>Chest coordinates for the bridge snapshot, so the user/agent can rearrange.</summary>
        private List<object> ChestQueueSnapshot()
        {
            return this.GetChestQueue()
                .Take(10)
                .Select(c => new { x = (int)c.TileLocation.X, y = (int)c.TileLocation.Y, stacks = c.Items.Count(i => i != null) })
                .ToList<object>();
        }

        /// <summary>Keep the remaining-tiles snapshot of the running job for next-morning resume.</summary>
        private void StashResume(WorkTaskState job, DateTime now)
        {
            if (job == null || job.AbortReason != null || job.Remaining.Count == 0) return;
            this.resumeTask = job.Task;
            this.resumeLocation = job.LocationName;
            this.resumeSeedId = job.SeedId;
            this.resumeSellFilter = job.SellFilter;
            this.resumeTiles = job.Remaining.ToList();
        }

        /// <summary>Restart yesterday's interrupted job once the new day has settled.</summary>
        private bool TryResumeInterruptedJob(DateTime now)
        {
            if (this.resumeTiles == null || this.resumeTiles.Count == 0) return false;
            if (now < this.resumeAtUtc) return false;
            var shadowStamina = this.companion.Shadow.Stamina;
            if (shadowStamina <= WorkLimits.StaminaReserve)
            {
                this.resumeAtUtc = now.AddSeconds(10);   // stamina not restored yet — retry
                return false;
            }

            var tiles = this.resumeTiles;
            var task = this.resumeTask.Value;
            var location = this.resumeLocation;
            this.resumeTiles = null;
            this.Start(task, location, tiles, this.resumeSeedId, this.resumeSellFilter);
            this.monitor.Log($"{this.companion.Name}: auto-resumed {task} in {location} — {tiles.Count} tile(s) left from yesterday", LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Stamina empty mid-job: cancel the current route, warp home and nap briefly;
        /// stamina is restored when the nap ends and the job resumes from the same tile.
        /// </summary>
        private void StartNap(WorkTaskState job, string why)
        {
            // Sleep happens at the bed. EEVDF-wise the round trip is amortised by the
            // slice size: one full-bar nap (20s) buys ~130 swings, so travel is a
            // small fraction of the work/nap duty cycle.
            float max = System.Math.Max(1f, this.companion.Shadow.maxStamina.Value);
            float current = this.companion.Shadow.Stamina;
            this.napWakeStamina = max;
            double seconds = NapFullRestoreSeconds * (this.napWakeStamina - current) / max;
            this.napUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            this.walker.Cancel();
            this.companion.Visual.controller = null;
            var home = Game1.getLocationFromName("FarmHouse") ?? this.companion.Visual.currentLocation;
            var playerTile = Game1.player?.Tile ?? this.companion.Visual.Tile;
            if (home != null)
            {
                var offset = this.companion.Name == "Companion2" ? 2 : 1;
                this.companion.WarpTo(home.Name, (int)playerTile.X + offset, (int)playerTile.Y);
                this.companion.SyncFromNpc();
            }
            this.monitor.Log(
                $"{this.companion.Name}: {why} — napping in bed {seconds:0.0}s to restore {this.napWakeStamina - current:0}/{max:0} stamina, then resuming {job.Task}",
                LogLevel.Info);
        }

        /// <summary>Mark a tile resolved, keep its position consistent, log one readable line.</summary>
        private void ResolveTile(WorkTaskState job, Vector2 tile)
        {
            this.InvalidateClumpCache();
            this.lastProgress = DateTime.UtcNow;
            int index = job.Remaining.IndexOf(tile);
            if (index >= 0) job.Remaining.RemoveAt(index);
            this.deferCount.Remove($"{tile.X},{tile.Y}");
            int resolved = job.DoneCount + job.SkippedCount + job.FailedCount;
            this.monitor.Log(
                $"{this.companion.Name}: [{resolved}/{job.TotalTiles}] ({(int)tile.X},{(int)tile.Y}) {job.LastResult}",
                LogLevel.Debug);
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

        /// <summary>Per-location ResourceClump cache — terrainFeatures holds a 2x2 clump
        /// only under its origin tile; these helpers reason about all four tiles.</summary>
        private readonly Dictionary<string, List<ResourceClump>> clumpCache = new Dictionary<string, List<ResourceClump>>();

        private List<ResourceClump> GetClumpCache(GameLocation location)
        {
            var key = location.NameOrUniqueName;
            if (this.clumpCache.TryGetValue(key, out var cached)) return cached;
            // SDV 1.6 stores ResourceClumps in their own location list, NOT in
            // terrainFeatures — only that list sees the stumps/logs/meteorites.
            var list = new List<ResourceClump>(location.resourceClumps);
            this.clumpCache[key] = list;
            return list;
        }

        private void InvalidateClumpCache() => this.clumpCache.Clear();

        /// <summary>The clump covering ANY of a 2x2 clump's tiles, or null.</summary>
        private ResourceClump ClumpCovering(GameLocation location, Vector2 tile)
        {
            var tileRect = new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64);
            foreach (var clump in this.GetClumpCache(location))
            {
                if (clump.getBoundingBox().Intersects(tileRect))
                    return clump;
            }
            return null;
        }

        /// <summary>Current health of the tree or clump at a tile (clump-aware), null when n/a.</summary>
        private float? TargetHealth(GameLocation location, Vector2 tile)
        {
            if (location.terrainFeatures.TryGetValue(tile, out var tf))
            {
                if (tf is Tree tree) return tree.health.Value;
                if (tf is ResourceClump clump) return clump.health.Value;
            }
            var covering = ClumpCovering(location, tile);
            return covering?.health.Value;
        }

        /// <summary>
        /// Can the companion physically stand on this tile? Checks objects, terrain
        /// features AND the map layers — isTilePassable() alone misses trees and rocks.
        /// Clump-covered tiles count as occupied even though only the origin tile is
        /// a terrain feature.
        /// </summary>
        private bool IsTileStandable(GameLocation location, Vector2 tile)
        {
            if (location.objects.ContainsKey(tile)) return false;
            if (ClumpCovering(location, tile) != null) return false;
            if (location.terrainFeatures.TryGetValue(tile, out var feature) && !feature.isPassable())
                return false;
            return location.isTilePassable(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport);
        }

        private bool IsClearableAt(GameLocation location, Vector2 tile)
        {
            var covering = ClumpCovering(location, tile);
            if (covering != null)
                return !this.unbreakable.Contains($"{(int)covering.Tile.X},{(int)covering.Tile.Y}");
            if (this.unbreakable.Contains($"{(int)tile.X},{(int)tile.Y}")) return false;
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
            this.zeroDamageStreak = 0;
        }

        /// <summary>
        /// A stone or weed can end up underfoot (spawned mid-walk, or we warped onto it).
        /// A blocked start tile kills every A* route, so clear it — or, when it isn't
        /// clearable here, hop to a standable neighbour. Returns true while it handled
        /// the tick.
        /// </summary>
        private bool ClearUnderfootOrStepAside(GameLocation location, WorkTaskState job)
        {
            var tile = this.companion.Visual.Tile;
            if (IsTileStandable(location, tile)) return false;

            if (job.Task == WorkTask.ClearArea && this.IsClearableAt(location, tile))
            {
                var outcome = this.RunTile(location, tile, job);
                if (outcome == TileOutcome.Done)
                {
                    job.Progress.Done.Add(new { x = tile.X, y = tile.Y, what = job.LastResult });
                    this.lastProgress = DateTime.UtcNow;
                    this.ResetTile();
                    return false;   // cleared — resume normal pathing next tick
                }
                if (outcome == TileOutcome.Failed)
                {
                    this.actionCooldown = 15;
                    return true;    // keep swinging underfoot
                }
                // Skipped (e.g. swing limit): fall through to the step-aside branch.
            }

            var stand = FindWalkableAdjacent(location, tile);
            if (stand.HasValue)
            {
                this.companion.WarpTo(location.Name, (int)stand.Value.X, (int)stand.Value.Y);
                this.ResetTile();
                this.monitor.Log($"{this.companion.Name}: stepped aside from blocked tile {tile}", LogLevel.Debug);
                return false;
            }
            return false;
        }

        private void FailCurrentTile(WorkTaskState job, string reason)
        {
            if (job.Remaining.Count == 0) return;
            var tile = job.Remaining[0];
            job.Progress.Failed.Add(new { x = (int)tile.X, y = (int)tile.Y, reason });
            job.LastResult = reason;
            this.markedUnreachable[$"{(int)tile.X},{(int)tile.Y}"] = reason;
            job.Remaining.RemoveAt(0);
            this.lastProgress = DateTime.UtcNow;
            this.ResetTile();
        }

        /// <summary>Debris sweep cadence and post-clear pickup window.</summary>
        private static readonly TimeSpan DebrisSweepInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan DebrisPickupGracePeriod = TimeSpan.FromSeconds(10);
        private DateTime lastDebrisSweep = DateTime.MinValue;
        private DateTime debrisPickupUntilUtc = DateTime.MinValue;
        private WorkTaskState lastClearAreaJob;
        private string lastSnapshotJobId;
        private readonly Dictionary<string, DateTime> lastDebrisIssueLog = new Dictionary<string, DateTime>();
        private string lastDebrisSweepDiagnosticJobId;

        /// <summary>
        /// Pick up item drops near the companion, per chunk. Debris has no public
        /// position — every chunk carries its own — and resource drops (wood/stone/
        /// fiber) are CHUNKS debris where `item` stays null and the id lives in
        /// `itemId`. The old check matched neither, so nothing was ever collected.
        /// </summary>
        private void SweepNearbyDebrisIfDue(DateTime now, bool force = false)
        {
            bool trackingClearArea = this.Current?.Task == WorkTask.ClearArea;
            if (!force && !trackingClearArea && now > this.debrisPickupUntilUtc)
                return;
            if (!force && now - this.lastDebrisSweep < DebrisSweepInterval)
                return;

            this.lastDebrisSweep = now;
            this.CollectNearbyDebris();
        }

        /// <summary>
        /// Post-clear salvage: walk to resource debris left inside the last clear-area
        /// region instead of hoping every chunk bounced within arm's reach. Runs only
        /// during the pickup grace window after a ClearArea job; per-chunk budget of
        /// 10s of walking before that chunk is written off.
        /// </summary>
        private bool ChaseResidualDebris(DateTime now)
        {
            var job = this.lastClearAreaJob;
            if (job == null || now > this.debrisPickupUntilUtc)
            {
                if (this.walker.Active && (now > this.debrisPickupUntilUtc || job == null))
                {
                    this.walker.Cancel();
                    this.companion.Visual.controller = null;
                    this.debrisChaseTicks = 0;
                }
                return false;
            }

            var location = this.companion.Visual.currentLocation;
            if (location == null) return false;

            // Collect whatever is already in reach first, then look for stragglers.
            this.CollectNearbyDebris();
            var chunk = this.FindNearestRegionDebris(location, job);
            if (!chunk.HasValue)
            {
                this.debrisChaseTicks = 0;
                return false;
            }

            var chunkTile = new Vector2((int)(chunk.Value.X / 64f), (int)(chunk.Value.Y / 64f));
            var stand = FindWalkableAdjacent(location, chunkTile) ?? chunkTile;
            if (Vector2.Distance(this.companion.Visual.Tile, stand) <= 1.2f)
            {
                // Standing next to it: the radius sweep picks it up on a later tick.
                this.debrisChaseTicks = 0;
                this.debrisPickupUntilUtc = now + DebrisPickupGracePeriod;
                this.CollectNearbyDebris();
                return false;
            }

            if (this.debrisChaseTicks >= DebrisChaseTickLimit)
            {
                this.LogDebrisIssue(location, "residual drop", chunk.Value, "could not walk to it within 10s");
                this.debrisChaseTicks = 0;
                return false;
            }

            if (!this.walker.Active && this.repathCooldown <= 0)
            {
                if (!this.WalkTo(location, stand))
                {
                    this.LogDebrisIssue(location, "residual drop", chunk.Value, "no path to it");
                    this.debrisChaseTicks = 0;
                    return false;
                }
                this.repathCooldown = RepathCooldown;
            }
            if (this.repathCooldown > 0) this.repathCooldown--;

            this.debrisChaseTicks++;
            // Making progress toward a drop — keep the pickup window open so the
            // 10s post-job grace doesn't expire halfway through the walk.
            this.debrisPickupUntilUtc = now + DebrisPickupGracePeriod;
            if (this.walker.Active)
                this.walker.Tick(location, Game1.currentGameTime);
            return true;
        }

        /// <summary>Pixel position of the nearest debris chunk sitting on one of the job's requested tiles.</summary>
        private Vector2? FindNearestRegionDebris(GameLocation location, WorkTaskState job)
        {
            var companionPosition = this.companion.Visual.Position;
            Vector2? best = null;
            float bestDist = float.MaxValue;
            foreach (var debris in location.debris)
            {
                if (debris == null || debris.Chunks.Count == 0) continue;
                if (debris.item == null)
                {
                    var debrisType = debris.debrisType.Value;
                    if (debrisType != StardewValley.Debris.DebrisType.CHUNKS
                        && debrisType != StardewValley.Debris.DebrisType.RESOURCE
                        && debrisType != StardewValley.Debris.DebrisType.OBJECT)
                        continue;
                }

                foreach (var chunk in debris.Chunks)
                {
                    Vector2 position;
                    try { position = chunk.position.Value; }
                    catch { continue; }

                    var tile = new Vector2((int)(position.X / 64f), (int)(position.Y / 64f));
                    if (!job.RequestedTiles.Contains(tile)) continue;
                    float dist = Vector2.Distance(position, companionPosition);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = position;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Pick up item drops near the companion, per chunk. Resource debris stores
        /// its item ID in itemId while item is null; direct item debris stores the item.
        /// </summary>
        private void CollectNearbyDebris()
        {
            var location = this.companion.Visual.currentLocation;
            if (location == null) return;

            var companionPosition = this.companion.Visual.Position;
            if (this.Current?.Task == WorkTask.ClearArea
                && this.lastDebrisSweepDiagnosticJobId != this.Current.Id)
            {
                int chunkCount = location.debris.Where(d => d != null).Sum(d => d.Chunks.Count);
                this.monitor.Log(
                    $"{this.companion.Name}: pickup sweep for {this.Current.Id} in {location.Name}; "
                    + $"visual={companionPosition}, requestedTiles={this.Current.RequestedTiles.Count}, "
                    + $"debrisGroups={location.debris.Count}, chunks={chunkCount}",
                    LogLevel.Debug);
                this.lastDebrisSweepDiagnosticJobId = this.Current.Id;
            }
            foreach (var debris in location.debris.ToList())
            {
                if (debris == null || debris.Chunks.Count == 0) continue;

                var directItem = debris.item;
                string itemId = debris.itemId.Value;
                if (string.IsNullOrWhiteSpace(itemId))
                    itemId = directItem?.QualifiedItemId;

                if (directItem != null)
                {
                    this.CollectDirectItemDebris(location, debris, directItem, itemId, companionPosition);
                    continue;
                }

                var debrisType = debris.debrisType.Value;
                if (debrisType != StardewValley.Debris.DebrisType.CHUNKS
                    && debrisType != StardewValley.Debris.DebrisType.RESOURCE
                    && debrisType != StardewValley.Debris.DebrisType.OBJECT)
                {
                    continue;
                }

                this.CollectChunkDebris(location, debris, itemId, companionPosition);
                if (debris.Chunks.Count == 0)
                    location.debris.Remove(debris);
            }
        }

        private void CollectDirectItemDebris(GameLocation location, StardewValley.Debris debris,
            Item directItem, string itemId, Vector2 companionPosition)
        {
            foreach (var chunk in debris.Chunks.ToList())
            {
                Vector2 chunkPosition;
                try
                {
                    chunkPosition = chunk.position.Value;
                }
                catch (Exception ex)
                {
                    this.LogDebrisIssue(location, itemId, companionPosition,
                        $"could not read chunk position: {ex.Message}");
                    continue;
                }

                if (!this.ShouldCollectDebrisAt(chunkPosition))
                {

                    continue;
                }

                try
                {
                    // Every chunk is worth one piece of the shared stack: grant one per
                    // chunk and shrink the stack, instead of handing out a single item
                    // and deleting the remaining chunks along with the debris group.
                    int granted = 0;
                    while (directItem.Stack > 0)
                    {
                        Item one = directItem.getOne();
                        one.Stack = 1;
                        if (!CompanionInventory.TryAdd(this.companion.Shadow, one)) break;
                        directItem.Stack--;
                        granted++;
                    }

                    if (granted == 0)
                    {
                        this.LogDebrisIssue(location, itemId, chunkPosition, "companion inventory rejected the item");
                        return;
                    }

                    this.RecordDebrisPickup(granted);
                    if (directItem.Stack > 0)
                    {
                        this.LogDebrisIssue(location, itemId, chunkPosition,
                            $"inventory accepted {granted}; {directItem.Stack} remain");
                        return;
                    }

                    location.debris.Remove(debris);
                    return;
                }
                catch (Exception ex)
                {
                    this.LogDebrisIssue(location, itemId, chunkPosition,
                        $"{ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }
        }

        private void CollectChunkDebris(GameLocation location, StardewValley.Debris debris,
            string itemId, Vector2 companionPosition)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                // Non-resource debris (cosmetic chunk bursts) has no item id and nothing
                // to grant — skip silently instead of warn-logging every 30 seconds.
                return;
            }

            string qualifiedItemId = itemId.StartsWith("(", StringComparison.Ordinal)
                ? itemId
                : $"(O){itemId}";

            foreach (var chunk in debris.Chunks.ToList())
            {
                Vector2 chunkPosition;
                try
                {
                    chunkPosition = chunk.position.Value;
                }
                catch (Exception ex)
                {
                    this.LogDebrisIssue(location, itemId, companionPosition,
                        $"could not read chunk position: {ex.Message}");
                    continue;
                }

                if (!this.ShouldCollectDebrisAt(chunkPosition))
                {

                    continue;
                }

                try
                {
                    Item toGive = ItemRegistry.Create(qualifiedItemId, 1);
                    if (toGive == null)
                    {
                        this.LogDebrisIssue(location, itemId, chunkPosition, "item could not be created");
                        continue;
                    }

                    int requestedStack = Math.Max(1, toGive.Stack);
                    bool acceptedAll = CompanionInventory.TryAdd(this.companion.Shadow, toGive);
                    int remainingStack = Math.Max(0, toGive.Stack);
                    int collected = acceptedAll ? requestedStack : Math.Max(0, requestedStack - remainingStack);
                    if (collected == 0)
                    {
                        this.LogDebrisIssue(location, itemId, chunkPosition,
                            "companion inventory rejected the item");
                        continue;
                    }

                    bool removed = debris.Chunks.Remove(chunk);
                    this.RecordDebrisPickup(collected);
                    if (!removed)
                        this.LogDebrisIssue(location, itemId, chunkPosition,
                            "item was added but its chunk was already gone");
                }
                catch (Exception ex)
                {
                    this.LogDebrisIssue(location, itemId, chunkPosition,
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private bool ShouldCollectDebrisAt(Vector2 chunkPosition)
        {
            var pickupJob = this.Current?.Task == WorkTask.ClearArea
                ? this.Current
                : DateTime.UtcNow <= this.debrisPickupUntilUtc ? this.lastClearAreaJob : null;
            if (pickupJob != null)
            {
                var tile = new Vector2((int)(chunkPosition.X / 64f), (int)(chunkPosition.Y / 64f));
                if (pickupJob.RequestedTiles.Contains(tile))
                    return true;
            }

            return Vector2.Distance(chunkPosition, this.companion.Visual.Position) <= DebrisPickupRadius;
        }

        private void RecordDebrisPickup(int count)
        {
            var job = this.Current?.Task == WorkTask.ClearArea
                ? this.Current
                : DateTime.UtcNow <= this.debrisPickupUntilUtc ? this.lastClearAreaJob : null;
            if (job == null) return;

            job.ItemsCollected += count;
            if (!ReferenceEquals(job, this.Current) && this.lastSnapshotJobId == job.Id)
                this.lastSnapshot = this.BuildSnapshot(job, active: false);
        }

        private void LogDebrisIssue(GameLocation location, string itemId, Vector2 position, string reason)
        {
            var now = DateTime.UtcNow;
            var locationName = location?.Name ?? "unknown";
            var resolvedItemId = string.IsNullOrWhiteSpace(itemId) ? "(unknown item)" : itemId;
            var tile = new Vector2((int)(position.X / 64f), (int)(position.Y / 64f));
            var key = $"{locationName}|{resolvedItemId}|{reason}";
            if (this.lastDebrisIssueLog.TryGetValue(key, out var lastLogged)
                && now - lastLogged < TimeSpan.FromSeconds(30))
                return;

            this.lastDebrisIssueLog[key] = now;
            foreach (var staleKey in this.lastDebrisIssueLog
                .Where(entry => now - entry.Value >= TimeSpan.FromMinutes(5))
                .Select(entry => entry.Key)
                .ToList())
            {
                this.lastDebrisIssueLog.Remove(staleKey);
            }

            this.monitor.Log(
                $"{this.companion.Name}: couldn't pick up debris '{resolvedItemId}' at {locationName} tile {tile}: {reason}",
                LogLevel.Warn);
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
            if (job.Task == WorkTask.ClearArea)
            {
                this.lastClearAreaJob = job;
                this.debrisPickupUntilUtc = DateTime.UtcNow + DebrisPickupGracePeriod;
                this.SweepNearbyDebrisIfDue(DateTime.UtcNow, force: true);
                this.DepositInventoryToChest();
            }
            this.walker.Cancel();
            this.companion.Visual.controller = null;
            this.refillTarget = null;

            this.monitor.Log(
                $"{this.companion.Name}: {job.Id} finished — done {job.DoneCount}, skipped {job.SkippedCount}, failed {job.FailedCount}, swings {job.Progress.Swings}, refills {job.Progress.Refills}"
                + (job.AbortReason != null ? $" (aborted: {job.AbortReason})" : ""),
                LogLevel.Info);

            this.lastSnapshot = this.BuildSnapshot(job, active: false);
            this.lastSnapshotJobId = job.Id;
            this.Current = null;
            this.ResetTile();
        }

        /// <summary>New day: re-arm the bedtime gate (called via day-start job state).</summary>
        public void NewDay()
        {
            this.bedtimeHandled = false;
            if (this.resumeTiles != null && this.resumeTiles.Count > 0)
            {
                this.resumeAtUtc = DateTime.UtcNow.AddSeconds(20);
                this.monitor.Log($"{this.companion.Name}: {this.resumeTiles.Count} tile(s) left from yesterday — will auto-resume shortly", LogLevel.Info);
            }

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
                // Record the failed destination so later tiles routed here skip fast.
                string destKey = $"{(int)destination.X},{(int)destination.Y}";
                this.unreachableDestinations[destKey] = this.unreachableDestinations.GetValueOrDefault(destKey) + 1;
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
                    if (hasClump) return ClumpAction((ResourceClump)feature);
                    var areaClump = ClumpCovering(location, tile);
                    if (areaClump != null) return ClumpAction(areaClump);
                    if (hasBush) return TileAction.ShakeBush;
                    if (hasGrass) return TileAction.SwingScythe;
                    if (obj != null && IsStone(obj)) return TileAction.SwingPickaxe;
                    if (obj != null && IsWeedObject(obj)) return TileAction.SwingScythe;
                    return TileAction.Nothing;

                case WorkTask.Chop:
                    return hasTree ? TileAction.SwingAxe : TileAction.Nothing;

                case WorkTask.Mine:
                    if (hasClump) return ClumpAction((ResourceClump)feature);
                    var mineClump = ClumpCovering(location, tile);
                    if (mineClump != null) return ClumpAction(mineClump);
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
                        this.StartNap(job, "stamina reserve reached");
                        return Fail(job, "napping — will resume");
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

        /// <summary>Stumps and hollow logs take the AXE; boulders and meteorites the PICKAXE.</summary>
        private static TileAction ClumpAction(ResourceClump clump)
        {
            int index = clump.parentSheetIndex.Value;
            return index == ResourceClump.stumpIndex || index == ResourceClump.hollowLogIndex
                ? TileAction.SwingAxe
                : TileAction.SwingPickaxe;
        }

        /// <summary>Vanilla tool-tier requirements per clump type; null when we CAN break it.</summary>
        private static string ClumpToolBlock(ResourceClump clump, Tool tool)
        {
            int index = clump.parentSheetIndex.Value;
            int level = tool.UpgradeLevel;
            if (tool is Axe)
            {
                if (index == ResourceClump.stumpIndex && level < 2) return "a copper axe";
                if (index == ResourceClump.hollowLogIndex && level < 3) return "a steel axe";
            }
            else if (tool is Pickaxe)
            {
                if (index == ResourceClump.boulderIndex && level < 2) return "a copper pickaxe";
                if (index == ResourceClump.meteoriteIndex && level < 3) return "a steel pickaxe";
            }
            return null;
        }

        private TileOutcome SwingOnce(GameLocation location, Vector2 tile, Tool tool, WorkTaskState job, string label)
        {
            var shadow = this.companion.Shadow;
            // A 2x2 clump only takes tool hits at its ORIGIN tile; standing on any of
            // the four tiles, aim the swing at the origin or it does nothing.
            var covering = this.ClumpCovering(location, tile);
            if (covering != null)
            {
                var origin = covering.Tile;
                if (origin != tile) tile = origin;
                // Tier gate BEFORE swinging: a too-weak tool pops the vanilla "not
                // strong enough" toast on every swing — skip silently instead.
                string blocked = ClumpToolBlock(covering, tool);
                if (blocked != null)
                {
                    this.unbreakable.Add($"{(int)covering.Tile.X},{(int)covering.Tile.Y}");
                    return Skip(job, $"needs {blocked} (tool too weak)");
                }
            }
            if (job.CurrentTileSwings >= WorkLimits.MaxTileSwings)
                return Skip(job, $"tile swing limit reached ({WorkLimits.MaxTileSwings})");
            if (shadow.Stamina <= WorkLimits.StaminaReserve)
            {
                this.StartNap(job, "stamina reserve reached");
                return Fail(job, "napping — will resume");
            }
            job.Progress.Swings++;
            job.CurrentTileSwings++;

            this.companion.Visual.FacingDirection = this.FaceDirection(tile);
            shadow.FaceToward(tile);
            float oldStamina = shadow.Stamina;
            // Health tracking: unchanged health across swings means the tool tier can't
            // dent this target (stumps/clumps need steel+) — bail instead of burning
            // the whole 32-swing budget on air.
            location.terrainFeatures.TryGetValue(tile, out var preFeature);
            float? healthBefore = TargetHealth(location, tile);

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

            // Health hit 0 but the feature lingers (fall animation) → done; stop
            // swinging so the animation doesn't eat the tile's swing budget.
            float? healthAfter = TargetHealth(location, tile);
            if (healthBefore.HasValue && healthAfter.HasValue)
            {
                if (healthAfter.Value <= 0)
                    return Done(job, $"cleared {label}");

                if (healthAfter.Value >= healthBefore.Value)
                {
                    if (++this.zeroDamageStreak >= 3)
                    {
                        this.zeroDamageStreak = 0;
                        this.unbreakable.Add($"{(int)tile.X},{(int)tile.Y}");
                        return Skip(job, $"{label} takes no damage from this tool tier");
                    }
                }
                else
                {
                    this.zeroDamageStreak = 0;
                }
            }
            else
            {
                this.zeroDamageStreak = 0;
            }

            if (shadow.Stamina <= 0) { this.StartNap(job, "out of energy"); return Fail(job, "napping — will resume"); }

            job.LastResult = $"swinging at {label}{this.HealthHint(location, tile)}";
            this.actionCooldown = 10;   // swing pace, not every frame
            return TileOutcome.Failed;
        }

        private bool TileStillHasTarget(GameLocation location, Vector2 tile)
        {
            if (location.objects.ContainsKey(tile)) return true;
            if (ClumpCovering(location, tile) != null) return true;
            if (!location.terrainFeatures.TryGetValue(tile, out var feature)) return false;
            return feature is Tree || feature is FruitTree || feature is ResourceClump
                || feature is Grass || feature is Bush;
        }

        private string HealthHint(GameLocation location, Vector2 tile)
        {
            float? hp = TargetHealth(location, tile);
            return hp.HasValue ? $" (hp {hp.Value:F0})" : "";
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
                napping = this.napUntilUtc != null,
                depositChests = this.ChestQueueSnapshot(),
                depositChestPreferred = this.preferredChest != null
                    ? $"{(int)this.preferredChest.Value.X},{(int)this.preferredChest.Value.Y}"
                    : null,
                swings = job.Progress.Swings,
                tilesWalked = Math.Round(this.tilesWalked, 1),
                itemsCollected = job.ItemsCollected,
                refills = job.Progress.Refills,
                skippedDetails = job.Progress.Skipped.Take(20).ToList(),
                failedDetails = job.Progress.Failed.Take(20).ToList(),
                unreachableTiles = this.markedUnreachable.OrderBy(e => e.Key).Select(e => e.Key).ToList(),
            };
        }
    }
}
