using System.Collections.Generic;
using GTA;

namespace GTAZ.Data
{
    // Static per-faction data (identity, models, weapons, relations, blip colors) for the five
    // Phase 4 factions. Pure data - the FactionSystem ctor calls Build() and stores the list.
    // The per-faction RelationshipGroup is still filled at runtime by
    // PedExtensions.SetupFactionRelationships (faction Ids cross-reference FactionCatalog).
    public static class FactionCatalog
    {
        public const string SurvivorFactionId = "Survivors";
        public const string BanditFactionId = "Bandits";
        public const string MilitaryFactionId = "Military";
        public const string CultistFactionId = "Cultists";
        public const string LostFactionId = "TheLost";

        public static List<FactionDef> Build()
        {
            // Survivors are also the outpost guards, so they share the survivor look (same
            // civilian pool as SurvivorSystem.SurvivorModels) - no zombie/faction overlap.
            var survivors = new FactionDef
            {
                Id = SurvivorFactionId,
                DisplayName = "Survivors",
                Archetype = FactionArchetype.Defensive,
                RelationToPlayer = PlayerRelation.Ally,
                Color = BlipColor.Green,
                PedModels =
                [
                    unchecked((int)PedHash.Eastsa01AMY),
                    unchecked((int)PedHash.Eastsa01AFY),
                    unchecked((int)PedHash.Business02AMY),
                    unchecked((int)PedHash.Business03AMY),
                    unchecked((int)PedHash.Business04AFY),
                    unchecked((int)PedHash.Hiker01AMY),
                    unchecked((int)PedHash.Hiker01AFY),
                    unchecked((int)PedHash.Malibu01AMM),
                    unchecked((int)PedHash.MexLabor01AMM),
                    unchecked((int)PedHash.Tourist01AMM),
                    unchecked((int)PedHash.Tourist01AFY),
                    unchecked((int)PedHash.Trucker01SMM),
                    unchecked((int)PedHash.JanitorSMM),
                    unchecked((int)PedHash.Farmer01AMM),
                    unchecked((int)PedHash.Beach01AMM),
                ],
                Weapons =
                [
                    WeaponHash.Pistol,
                    WeaponHash.CombatPistol,
                    WeaponHash.MicroSMG,
                    WeaponHash.AssaultRifle,
                ],
                AmbientSpawnWeight = 1f
            };

            // The Families (formerly "Bandits"): a street gang that holds turf and raids player
            // outposts. Internal Id stays "Bandits" for the BanditPatrolIntervalMinutes config
            // key and save compatibility; every player-facing string says "The Families".
            var families = new FactionDef
            {
                Id = BanditFactionId,
                DisplayName = "The Families",
                Archetype = FactionArchetype.Aggressive,
                RelationToPlayer = PlayerRelation.Hostile,
                Color = BlipColor.Orange,
                PedModels =
                [
                    unchecked((int)PedHash.BallaEast01GMY),
                    (int)PedHash.BallaOrig01GMY,
                    unchecked((int)PedHash.BallaSout01GMY),
                    unchecked((int)PedHash.Ballas01GFY),
                    unchecked((int)PedHash.Families01GFY),
                    unchecked((int)PedHash.Famca01GMY),
                    unchecked((int)PedHash.Famdnf01GMY),
                ],
                Weapons =
                [
                    WeaponHash.MicroSMG,
                    WeaponHash.AssaultRifle,
                    WeaponHash.PumpShotgun,
                    WeaponHash.SawnOffShotgun,
                ],
                AmbientSpawnWeight = 1f
            };

            // Fully-defined hostile factions: the Military (turf defenders at the Fort and
            // Bolingbroke) and the Altruists (cannibal cult holding the camp and scrapyard -
            // they spare zombies, SparesZombies drives a one-way relationship bind in
            // PedExtensions.SetupFactionRelationships). Both shoot the player on sight, so their
            // zones have to be fought for before capture.
            var military = new FactionDef
            {
                Id = MilitaryFactionId,
                DisplayName = "Military",
                Archetype = FactionArchetype.Defensive,
                RelationToPlayer = PlayerRelation.Hostile,
                Color = BlipColor.Blue,
                PedModels =
                [
                    (int)PedHash.ExArmy01,
                    unchecked((int)PedHash.Swat01SMY),
                    unchecked((int)PedHash.Marine01SMM),
                    unchecked((int)PedHash.Blackops02SMY),
                    unchecked((int)PedHash.Blackops03SMY),
                    unchecked((int)PedHash.Marine03SMY),
                ],
                Weapons =
                [
                    WeaponHash.AssaultRifle,
                    WeaponHash.CarbineRifle,
                    WeaponHash.HeavyPistol,
                ],
                AmbientSpawnWeight = 1f
            };

            var cultists = new FactionDef
            {
                Id = CultistFactionId,
                DisplayName = "Altruists",
                Archetype = FactionArchetype.Defensive,
                RelationToPlayer = PlayerRelation.Hostile,
                Color = BlipColor.Purple,
                SparesZombies = true,
                PedModels =
                [
                    unchecked((int)PedHash.Acult02AMO),
                    unchecked((int)PedHash.FatCult01AFM),
                    unchecked((int)PedHash.Acult01AMO),
                    unchecked((int)PedHash.Acult01AMY),
                    unchecked((int)PedHash.Fooliganz01GFM),
                    unchecked((int)PedHash.Fooliganz2),
                ],
                Weapons =
                [
                    WeaponHash.Pistol,
                    WeaponHash.CombatPistol,
                ],
                AmbientSpawnWeight = 1f
            };

            var theLost = new FactionDef
            {
                Id = LostFactionId,
                DisplayName = "The Lost",
                Archetype = FactionArchetype.Aggressive,
                RelationToPlayer = PlayerRelation.Hostile,
                Color = BlipColor.Yellow,
                PedModels =
                [
                    (int)PedHash.Lost01GMY,
                    (int)PedHash.Lost02GMY,
                    (int)PedHash.Lost03GMY,
                    unchecked((int)PedHash.Lost01GFY),
                ],
                Weapons =
                [
                    WeaponHash.MicroSMG,
                    WeaponHash.PumpShotgun,
                    WeaponHash.SawnOffShotgun,
                ],
                AmbientSpawnWeight = 1f
            };

            return [survivors, families, military, cultists, theLost];
        }
    }
}
