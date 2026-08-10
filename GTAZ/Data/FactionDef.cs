using GTA;

namespace GTAZ.Data
{
    // Phase 4 - Faction & Territory (DOCS/06-Roadmap.md §4). FactionDef is the static per-faction
    // data: identity, spawn weights, weapons, relations, blip color. The RelationshipGroup is
    // filled in at runtime by PedExtensions.SetupFactionRelationships (a faction group is just
    // a World.AddRelationshipGroup hash, except Survivors which reuse the existing
    // SurvivorRelationshipGroup so ambient + faction survivors share one group).
    public class FactionDef
    {
        public string Id { get; init; }
        public string DisplayName { get; init; }

        // Defensive factions hold ground and guard territory; aggressive factions raid and
        // push (drives FactionAI task choice).
        public FactionArchetype Archetype { get; init; }
        public PlayerRelation RelationToPlayer { get; init; }

        // Assigned in SetupFactionRelationships. -1 until then.
        public int RelationshipGroup { get; set; } = -1;

        // Minimap color for faction blips and zone ownership.
        public BlipColor Color { get; init; }

        // Models to draw this faction's members from.
        public int[] PedModels { get; init; }

        // Weapons faction members are armed with.
        public WeaponHash[] Weapons { get; init; }

        // Relative chance this faction is picked for ambient spawns (0 = data-only, spawns
        // disabled). All five factions are live in the roll-out: The Families, Military,
        // Altruists and The Lost all spawn (weight 1); set a weight to 0 to turn one off.
        public float AmbientSpawnWeight { get; init; } = 1f;

        // Shooting accuracy for this faction's members (SET_PED_ACCURACY). Deliberately below
        // the survivor 60 (survivors are trained-ish shots; factions are road trash and conscripts)
        // so hostile factions are dangerous up close but miss a lot at range - the player isn't
        // melted the instant a faction ped sees them (see MakeFactionLike).
        public int Accuracy { get; init; } = 35;

        // True when this faction spares zombies (cults that worship them): the faction never
        // attacks Infected, but zombies still attack the faction (the stance is one-way - the
        // relationship bind skips the mirrored hate that normally makes both sides fight).
        public bool SparesZombies { get; init; }

        public override string ToString() => DisplayName;
    }

    public enum FactionArchetype
    {
        Defensive,
        Aggressive
    }

    public enum PlayerRelation
    {
        Ally,
        Hostile,
        Neutral
    }
}
