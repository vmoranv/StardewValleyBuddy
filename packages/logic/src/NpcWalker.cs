// Frame-driven NPC walking along an A* path. GPL-3.0-or-later. See LICENSE.txt.
//
// We don't hand the path to PathFindController for execution: on a multiplayer host
// the vanilla controller left the NPC standing still even with a valid path and every
// gate (IsMasterGame, freezeMotion, menu, eventUp) reporting healthy. The mod already
// writes NPC.Position directly when spawning, so we own movement the same way — vanilla
// A* computes the waypoints, we interpolate the position per frame.
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewMCPBridge
{
    /// <summary>Walks a companion NPC along precomputed waypoints, frame by frame.</summary>
    public class NpcWalker : INpcWalker
    {
        /// <summary>Tiles covered per second. Matches the companion's vanilla walking pace.</summary>
        private const float PixelsPerSecond = 64f * 3f;

        /// <summary>Route in walking order: index 0 is the next step.</summary>
        private readonly List<Point> route = new List<Point>();

        private readonly NPC npc;

        public NpcWalker(NPC npc)
        {
            this.npc = npc;
        }

        public bool Active => this.route.Count > 0;

        /// <summary>The tile that blocked the route on the last Tick, if any.</summary>
        public Vector2? BlockedTile { get; private set; }

        /// <summary>Waypoint the NPC is currently heading to, for diagnostics.</summary>
        public Point CurrentWaypoint => this.route.Count > 0 ? this.route[0] : default;

        /// <summary>
        /// Install a route. Stack&lt;T&gt; enumerates top-first, and the A* stack's top is
        /// the FIRST step toward the goal — so plain enumeration gives walking order and
        /// route[0] is the next step. Reversing here walks the mirrored route away.
        /// </summary>
        public void Start(IEnumerable<Point> waypoints)
        {
            this.route.Clear();
            this.route.AddRange(waypoints);
        }

        public void Cancel()
        {
            this.route.Clear();
            this.BlockedTile = null;
        }

        /// <summary>
        /// Advance one frame toward the next waypoint. Returns true while still walking;
        /// false when the route finished or was blocked (path is cleared either way, so
        /// the caller's stall/reroute logic can take over).
        /// </summary>
        public bool Tick(GameLocation location, GameTime time)
        {
            if (this.route.Count == 0) return false;

            var waypoint = this.route[0];
            var target = new Vector2(waypoint.X * 64f + 32f, waypoint.Y * 64f + 32f);
            var position = this.npc.Position;
            var toTarget = target - position;
            float distance = toTarget.Length();

            if (distance < 4f)
            {
                this.route.RemoveAt(0);
                this.npc.Position = target;
                return this.route.Count > 0;
            }

            // Time-normalised so SMAPI's variable timestep doesn't change pace.
            float step = PixelsPerSecond * (float)time.ElapsedGameTime.TotalSeconds;
            if (step > distance) step = distance;

            var next = position + toTarget / distance * step;
            // Respect walls: A* routes through passable tiles, but a newly spawned
            // object (or another character) can appear mid-route. Drop the route rather
            // than phase through; the caller's stall detection handles the rest.
            var center = new Point((int)(next.X / 64f), (int)(next.Y / 64f));
            if (!location.isTilePassable(new xTile.Dimensions.Location(center.X, center.Y), Game1.viewport)
                && center != waypoint)
            {
                this.BlockedTile = new Vector2(center.X, center.Y);
                this.route.Clear();
                return false;
            }

            this.npc.Position = next;
            return true;
        }
    }
}
