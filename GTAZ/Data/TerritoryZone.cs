using GTA;
using GTA.Math;
using GTA.Native;

namespace GTAZ.Data
{
    // A single capturable game zone (DOCS/06-Roadmap.md §4.8). The zone is identified by its game
    // zone hash (GET_ZONE_AT_COORDS at the configured center), NOT by name string - that makes
    // the membership check robust to any zone naming table. Display name is resolved via the
    // game's own localized name lookup so menus/notifications show "Vespucci Beach" etc.
    public class TerritoryZone
    {
        public string ZoneName { get; set; } = "UNKNOWN";
        public Vector3 Center { get; set; }
        public int ZoneHash { get; set; }
        public FactionDef Owner { get; set; }
        // The faction this territory belongs to in Territories.txt (Military, The Lost,
        // Altruists, The Families). Filled at zone-build time; a captured/surrendered zone
        // reverts to this faction, and hostile-zone defenders/defense waves/raids spawn as it.
        public string HomeFactionId { get; set; }
        public int Value { get; set; }

        // Capture-gating flags (see FactionSystem.UpdateZones): DefendersPending is set while
        // bandit defenders are queued to spawn so the zone doesn't start capturing on the same
        // tick their spawns are enqueued; ResistTicks counts consecutive defender-free ticks
        // before capture is allowed to begin; PendingDefenderSpawns tracks queued spawn requests
        // so DefendersPending clears only when every queued defender resolved. LeaveGraceTicks
        // counts down the on-screen "return to the area" grace while the player is outside the
        // ring but a takeover attempt is still in flight - the attempt resets only when it hits
        // zero (or the player re-enters, which cancels it).
        //
        // RespawnCooldownTicks counts down while no defenders are alive; when it hits 0 a fresh
        // defender wave spawns, so a hostile zone always fights back while the player stays in it
        // (previously WaveSpawnedThisVisit limited it to one wave per visit, which let zombies
        // clear a zone and hand it to the player with no fight). FightKills counts enemy bandit
        // deaths near an owned zone toward the counter-attack quota; FightCooldownTicks spaces
        // counter-attacks out so a fight can't re-trigger the moment it ends.
        public bool DefendersPending { get; set; }
        public int ResistTicks { get; set; }
        public int PendingDefenderSpawns { get; set; }
        public int LeaveGraceTicks { get; set; }
        public int RespawnCooldownTicks { get; set; }
        public int FightKills { get; set; }
        public int FightCooldownTicks { get; set; }
        // Kill-triggered defense-reinforcement waves sent this visit to a HOSTILE zone (see
        // FactionSystem.UpdateZones escalation). Capped by TerritoryFightReinforcementWaves so a
        // takeover attempt is a real fight but always winnable; resets when the player leaves.
        public int ReinforcementWavesSent { get; set; }
        // Ticks since the counter-attack started. The fight end-check ignores the "all raiders
        // dead" condition for the first ~5s so queued raider spawns (3 grants/pass) have time
        // to materialize - without this a fight could start and instantly end as 'repelled'.
        public int FightTicks { get; set; }
        // Queued raider spawns still waiting on the SpawnRequestQueue (3 grants/pass, 8s TTL).
        // The counter-attack end-check ALSO stays open while any raider is still queued, so a
        // raid can't be declared "repelled" before its own wave is on the ground (the round-5
        // "attack/repel loop": fight started then ended 3s later, zero raiders ever appeared).
        public int PendingRaiderSpawns { get; set; }
        // True once the player has proven a full clear (zone held defender-free past the 1s
        // capture grace). While set, defender respawns and kill-triggered reinforcement waves
        // stop so a fresh wave can't re-materialize mid-capture. Capture itself is instant on
        // the clear - there is no hold-timer. Resets when the player leaves and on capture.
        public bool CaptureInProgress { get; set; }
        // Set once per visit when a proven clear is blocked by the MaxPlayerZones cap, so the
        // player gets one explicit notification instead of silently grinding an un-capturable
        // zone. Reset when the player leaves.
        public bool CapBlockedNotified { get; set; }

        // True once the player has armed a surrender of this owned zone (SurrenderCurrentZone).
        // The zone only actually reverts when the player LEAVES it - the deferral stops the new
        // hostile defenders from materializing on top of the player who's standing in the zone.
        public bool SurrenderPending { get; set; }

        // True once the player has captured this zone (set by FactionSystem.CaptureZone).
        public bool IsPlayerOwned { get; set; }

        // Player-mapped safe wave spawn points (Territories.txt mapping pass). Defender, defense
        // and raid waves pick a random point from this list instead of searching for a safe
        // position around the center, so waves reliably materialize at the verified spots. Empty
        // for territories that haven't been mapped yet -> FactionSystem falls back to a random
        // safe position.
        public Vector3[] WaveSpawnPoints { get; set; } = [];

        private Blip _blip;
        private Blip _radiusBlip;

        public bool IsInZone(Vector3 position)
        {
            return Function.Call<int>(Hash.GET_ZONE_AT_COORDS, position.X, position.Y, position.Z) == ZoneHash;
        }

        public string DisplayName => World.GetZoneLocalizedName(Center);

        // Owner-colored Safehouse blip (same sprite GangMod uses for turf). Player-owned zones
        // get a green secondary, bandit-held get red.
        public void CreateBlip()
        {
            if (_blip?.Exists() is true)
                return;

            _blip = World.CreateBlip(Center);
            _blip.Sprite = BlipSprite.GTAOPlayerSafehouse;
            _blip.Scale = 0.8f;
            _blip.IsShortRange = true;
            UpdateBlip();
        }

        public void UpdateBlip()
        {
            if (_blip?.Exists() is not true)
                return;

            _blip.Color = Owner?.Color ?? BlipColor.White;
            _blip.SecondaryColor = IsPlayerOwned ? System.Drawing.Color.Green : System.Drawing.Color.Red;
            _blip.Name = IsPlayerOwned
                ? $"{DisplayName} (Yours)"
                : $"{DisplayName} ({Owner?.DisplayName ?? "Neutral"} turf)";
        }

        public void DeleteBlip()
        {
            _blip?.Delete();
            _blip = null;
        }

        // Debug radius ring (ADD_BLIP_FOR_RADIUS via World.CreateBlip(pos, radius)) so zone
        // boundaries are visible on the map while playtesting. A radius blip has no sprite -
        // only color/secondary color/alpha/scale, so Sprite is deliberately never set here.
        // NOTE: never call Blip.Scale on a radius blip - SET_BLIP_SCALE sets the RING RADIUS to
        // that value in game units, so Scale = 1.2f made the ring 1.2m wide (invisible). The
        // radius comes from the CreateBlip call alone.
        public void CreateRadiusBlip(float radius)
        {
            if (_radiusBlip?.Exists() is true)
                return;

            _radiusBlip = World.CreateBlip(Center, radius);
            _radiusBlip.Alpha = 160;
            // Short-range blips don't render on the pause map - leave this off so the ring
            // shows on both the minimap and the big map.
            _radiusBlip.IsHiddenOnLegend = true;
            _radiusBlip.Name = $"{DisplayName} territory";
            UpdateRadiusBlip();
        }

        public void UpdateRadiusBlip()
        {
            if (_radiusBlip?.Exists() is not true)
                return;

            _radiusBlip.Color = Owner?.Color ?? BlipColor.White;
            _radiusBlip.SecondaryColor = IsPlayerOwned ? System.Drawing.Color.Green : System.Drawing.Color.Red;
        }

        public void DeleteRadiusBlip()
        {
            _radiusBlip?.Delete();
            _radiusBlip = null;
        }
    }
}
