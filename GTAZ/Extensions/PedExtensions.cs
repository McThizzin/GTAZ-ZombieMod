using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Systems;


namespace GTAZ.Extensions
{
    public static class PedExtensions
    {
        public static int InfectedRelationshipGroup { get; private set; }
        public static int SurvivorRelationshipGroup { get; private set; }
        public static int RecruitRelationshipGroup { get; private set; }
        private static bool _relationshipSetup;
        // Confirmed loading successfully in-game (HAS_CLIP_SET_LOADED true, verified in log).
        // The T-pose was never this clipset - it was PED_FLAG_FREEZE (see MakeZombieLike below).
        //
        // Matches the user's original reference template exactly: strafeClipset is the SAME
        // @core string as movementClipset (not @strafe) - @strafe is only ever passed to
        // SetPedUsingActionMode as the action-mode clipset, alongside a weapon animation
        // override. Earlier rounds got this wrong by passing @strafe directly to
        // SET_PED_STRAFE_CLIPSET and never calling action mode at all.
        private const string ZombieMovementClipset = "clipset@anim@ingame@move_m@zombie@core";
        private const string ZombieActionModeClipset = "clipset@anim@ingame@move_m@zombie@strafe";
        private static bool _clipsetsLoaded;
        private static bool _clipsetLoadFailed;

        public static bool AreZombieClipsetsReady => _clipsetsLoaded;

        // TASK_PLAY_ANIM needs its anim dictionary requested and confirmed loaded first, same
        // as clip sets need REQUEST_CLIP_SET. This was never being done for the melee attack
        // anim in ZombieAI.OnAttack(), so that TASK_PLAY_ANIM call was silently failing -
        // damage still applied, but no attack animation ever actually played.
        public const string AttackAnimDict = "anim@ingame@melee@unarmed@streamed_variations_zombie";
        private static bool _attackAnimLoaded;
        private static bool _attackAnimLoadFailed;

        public static bool IsAttackAnimReady => _attackAnimLoaded;

        // SET_FACIAL_IDLE_ANIM_OVERRIDE / PLAY_FACIAL_ANIM with a dict that isn't streamed in
        // silently fails, same class of bug as unloaded clipsets/animation dicts. The facial
        // overrides are gated on these loading (see ApplyZombieFace).
        private const string FacialDictMale = "facials@gen_male@base";
        private const string FacialDictFemale = "facials@gen_female@base";
        private static bool _facialDictsLoaded;
        private static bool _facialDictsLoadFailed;

        // Mood expressions are the lesson-12-verified anim names (mood_pain_1 is NOT real;
        // these are). Zombies get one at random at spawn so the horde doesn't all wear the
        // same face. The earlier "burning_1" burned-face attempt never rendered and is dropped.
        public static readonly string[] FacialMoodAnims =
        [
            "mood_angry_1",
            "mood_injured_1",
            "mood_drunk_1",
            "mood_scared_1"
        ];

        public static void EnsureFacialDictsLoaded()
        {
            if (_facialDictsLoaded || _facialDictsLoadFailed)
                return;

            int timeout = 0;
            while (timeout < 300)
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, FacialDictMale);
                Function.Call(Hash.REQUEST_ANIM_DICT, FacialDictFemale);
                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, FacialDictMale)
                    && Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, FacialDictFemale))
                {
                    _facialDictsLoaded = true;
                    Logger.Info($"Facial dicts loaded OK: {FacialDictMale} / {FacialDictFemale}");
                    return;
                }
                Script.Wait(1);
                timeout++;
            }

            _facialDictsLoadFailed = true;
            Logger.Warn($"Facial dicts FAILED to load after {timeout} attempts ({FacialDictMale} / {FacialDictFemale}) - " +
                        "zombies will spawn without the burned-face expression.");
        }

        public static void EnsureAttackAnimLoaded()
        {
            if (_attackAnimLoaded || _attackAnimLoadFailed)
                return;

            int timeout = 0;
            while (timeout < 300)
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, AttackAnimDict);
                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, AttackAnimDict))
                {
                    _attackAnimLoaded = true;
                    Logger.Info($"Attack anim dict loaded OK: {AttackAnimDict}");
                    return;
                }
                Script.Wait(1);
                timeout++;
            }

            _attackAnimLoadFailed = true;
            Logger.Warn($"Attack anim dict '{AttackAnimDict}' FAILED to load after {timeout} attempts - " +
                        "zombies will apply attack damage with no visible swing animation.");
        }

        public static void EnsureClipsetsLoaded()
        {
            if (_clipsetsLoaded || _clipsetLoadFailed)
                return;

            int timeout = 0;
            while (timeout < 300)
            {
                Function.Call(Hash.REQUEST_CLIP_SET, ZombieMovementClipset);
                Function.Call(Hash.REQUEST_CLIP_SET, ZombieActionModeClipset);
                if (Function.Call<bool>(Hash.HAS_CLIP_SET_LOADED, ZombieMovementClipset)
                    && Function.Call<bool>(Hash.HAS_CLIP_SET_LOADED, ZombieActionModeClipset))
                {
                    _clipsetsLoaded = true;
                    Logger.Info($"Zombie clipsets loaded OK: {ZombieMovementClipset} / {ZombieActionModeClipset}");
                    return;
                }
                Script.Wait(1);
                timeout++;
            }

            _clipsetLoadFailed = true;
            Logger.Warn($"Zombie clipsets FAILED to load after {timeout} attempts " +
                        $"({ZombieMovementClipset} / {ZombieActionModeClipset}). " +
                        "Zombies will spawn with no clipset override (default model moveset) instead of T-posing.");
        }

        public static void SetupZombieRelationships()
        {
            if (_relationshipSetup)
                return;
            _relationshipSetup = true;

            InfectedRelationshipGroup = World.AddRelationshipGroup("Infected").Hash;

            int playerGroup = Game.Player.Character.RelationshipGroup.Hash;

            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, InfectedRelationshipGroup, playerGroup, 1);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, playerGroup, InfectedRelationshipGroup, 1);

            const int civGroup = unchecked((int)0x6F0783F5);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, InfectedRelationshipGroup, civGroup, 1);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, civGroup, InfectedRelationshipGroup, 1);

            const int copGroup = unchecked((int)0xCE68C05D);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, InfectedRelationshipGroup, copGroup, 1);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, copGroup, InfectedRelationshipGroup, 1);
        }

        // Survivor group must be set up AFTER SetupZombieRelationships (it references
        // InfectedRelationshipGroup). Relationship codes: 0 companion, 1 respect, 2 like,
        // 3 neutral, 4 dislike, 5 hate. Survivors and zombies hate each other so both the
        // native AI and our scripted TASK_COMBAT_PED fights behave consistently. Survivor <-> player
        // is COMPANION (0), the highest level - Respect (1) was NOT enough: a Respect ped never
        // *initiates* combat with the player but still retaliates the moment it takes damage from
        // the player (direct shot or stray bullet), so survivors still turned hostile mid-fight
        // and even a recruited follower flipped once it got clipped. Companion stops the native AI
        // from ever treating the player as a combat target, including on retaliation - the
        // "hostile until they join" bug (joining put the ped on the Companion Recruit group and
        // hostility vanished, proving the pre-join stance was the problem). Survivors stay fully
        // damageable by the player (relationship never gates damage) so killing one still drops
        // loot; they just never fight back, ever.
        public static void SetupSurvivorRelationships()
        {
            SurvivorRelationshipGroup = World.AddRelationshipGroup("Survivors").Hash;

            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, SurvivorRelationshipGroup, InfectedRelationshipGroup, 1);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, InfectedRelationshipGroup, SurvivorRelationshipGroup, 1);

            int playerGroup = Game.Player.Character.RelationshipGroup.Hash;
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 0, SurvivorRelationshipGroup, playerGroup, 1);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 0, playerGroup, SurvivorRelationshipGroup, 1);

            // Startup diagnostic (DOCS/08-Lessons-Learned.md #33): record the constructor-time player
            // group hash so the first-tick re-assert can prove whether the ctor bind stuck.
            // Playtest logs showed the ctor hash MATCHING the live group while the stances
            // still failed to stick - the relationship natives were silently dropped (game not
            // fully ready), NOT bound to a phantom group. 0 = Companion, 3 = Neutral, 5 = Hate.
            ConstructorPlayerGroup = playerGroup;
            Logger.Info($"Survivor relationships (ctor): group={SurvivorRelationshipGroup} playerGroup={playerGroup} "
                + $"survivor->player={Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_GROUPS, SurvivorRelationshipGroup, playerGroup)} "
                + $"player->survivor={Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_GROUPS, playerGroup, SurvivorRelationshipGroup)} "
                + $"survivor->infected={Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_GROUPS, SurvivorRelationshipGroup, InfectedRelationshipGroup)}");
        }

        // Relationship group the player ped reported when SetupSurvivorRelationships ran (ModCore
        // constructor). Compared against the live group in EnsurePlayerFacingRelationships so the
        // dropped-ctor-bind hypothesis is proven or refuted in GTAZ.log rather than guessed at.
        public static int ConstructorPlayerGroup { get; private set; }

        public static bool IsHumanoid(this Ped ped)
        {
            return Function.Call<bool>(Hash.IS_PED_HUMAN, ped);
        }

        public static bool IsZombie(this Ped ped)
        {
            return ped.RelationshipGroup == InfectedRelationshipGroup;
        }

        public static bool IsSurvivor(this Ped ped)
        {
            return ped.RelationshipGroup == SurvivorRelationshipGroup;
        }

        // Every relationship group that Phase 4 faction peds belong to (Survivors group +
        // each registered FactionDef group). Used by IsFactionPed so WorldSystem and the
        // ambient-ped clear skip them like they do survivors.
        private static readonly HashSet<int> FactionRelationshipGroups = [];

        // Phase 4 §4.5 relationship matrix. Called from ModuleManager.Initialize AFTER
        // SetupZombieRelationships (needs InfectedRelationshipGroup) and SetupSurvivorRelationships
        // (Survivor faction reuses the existing SurvivorRelationshipGroup so ambient and faction
        // survivors share one group).
        public static void SetupFactionRelationships(this System.Collections.Generic.IEnumerable<FactionDef> factions)
        {
            foreach (var faction in factions)
            {
                int group = faction.Id == FactionCatalog.SurvivorFactionId
                    ? SurvivorRelationshipGroup
                    : World.AddRelationshipGroup(faction.DisplayName).Hash;
                faction.RelationshipGroup = group;
                FactionRelationshipGroups.Add(group);
            }

            int playerGroup = Game.Player.Character.RelationshipGroup.Hash;
            const int civGroup = unchecked((int)0x6F0783F5);
            const int copGroup = unchecked((int)0xCE68C05D);

            foreach (var faction in factions)
            {
                int g = faction.RelationshipGroup;

                // Player relation. Survivors are COMPANION (Ally -> 0), matching the shared
                // Survivor group set in SetupSurvivorRelationships. Respect (1) was tried first
                // but a Respect ped still retaliates when damaged by the player, so survivors
                // kept flipping hostile ("hostile until they join"); Companion means the native
                // AI never fights the player at all, even on retaliation. Hostile factions
                // (The Families, Military, Altruists, The Lost) are Hate (5).
                int playerRel = faction.RelationToPlayer switch
                {
                    PlayerRelation.Hostile => 5,
                    PlayerRelation.Ally => 0,
                    _ => 3
                };
                SetRelationship(g, playerGroup, playerRel);
                SetRelationship(playerGroup, g, playerRel);

                // Every faction hates zombies EXCEPT factions that SparesZombies (the Altruists
                // worship them). The truce is one-way: the faction ignores zombies (Neutral) but
                // zombies still hunt the faction (Hate). SetRelationship mirrors both directions,
                // so the truce needs the directional bind (a mirrored Neutral would let zombies
                // ignore Altruists too, emptying the camp of prey).
                if (faction.SparesZombies)
                {
                    SetRelationshipDirectional(g, InfectedRelationshipGroup, 3);
                    SetRelationshipDirectional(InfectedRelationshipGroup, g, 5);
                }
                else
                {
                    SetRelationship(g, InfectedRelationshipGroup, 5);
                    SetRelationship(InfectedRelationshipGroup, g, 5);
                }

                // Factions don't pick fights with civilians or cops in this phase (bandits shoot
                // survivors/zombies/player, not random bystanders).
                SetRelationship(g, civGroup, 3);
                SetRelationship(civGroup, g, 3);
                SetRelationship(g, copGroup, 3);
                SetRelationship(copGroup, g, 3);
            }

            foreach (var faction in factions)
            {
                foreach (var other in factions)
                {
                    if (faction == other)
                        continue;

                    bool aHatesB = HatedFactionIds(faction.Id).Contains(other.Id);
                    bool bHatesA = HatedFactionIds(other.Id).Contains(faction.Id);

                    SetRelationship(faction.RelationshipGroup, other.RelationshipGroup, aHatesB ? 5 : 3);
                    SetRelationship(other.RelationshipGroup, faction.RelationshipGroup, bHatesA ? 5 : 3);
                }
            }

            // Recruits get their own relationship group (DOCS/06-Roadmap.md §4.7) so they leave the
            // shared Survivor group. That lets the player↔recruit stance be COMPANION without
            // making every ambient survivor unshootable. Companion to the player stops the
            // game's formation/combat AI from treating the player or fellow recruits as targets
            // (the 2+ recruits fighting bug); recruits keep hating zombies/bandits/Lost. The
            // recruit↔survivor stance is COMPANION BOTH WAYS so stray bullets fired during a
            // zombie fight can never trigger retaliation - Like was tried and a Neutral survivor
            // still aggroed a recruit mid-fight ("non-joinable survivors attack joined survivors
            // when they shoot a zombie", DOCS/08-Lessons-Learned.md #33).
            RecruitRelationshipGroup = World.AddRelationshipGroup("GTAZRecruits").Hash;
            FactionRelationshipGroups.Add(RecruitRelationshipGroup);

            SetRelationship(RecruitRelationshipGroup, playerGroup, 0); // Companion to the player
            SetRelationship(RecruitRelationshipGroup, InfectedRelationshipGroup, 5); // hate zombies
            SetRelationship(RecruitRelationshipGroup, SurvivorRelationshipGroup, 0); // Companion both ways
            SetRelationship(SurvivorRelationshipGroup, RecruitRelationshipGroup, 0); // so survivors never retaliate

            foreach (var faction in factions)
            {
                if (faction.Id == FactionCatalog.SurvivorFactionId)
                    continue; // already Like above
                bool recruitHates = HatedFactionIds(FactionCatalog.SurvivorFactionId).Contains(faction.Id);
                SetRelationship(RecruitRelationshipGroup, faction.RelationshipGroup, recruitHates ? 5 : 3);
            }
        }

        // Re-asserts every player-facing stance against the LIVE player relationship group. Runs
        // as a one-shot on the first tick: the Setup*Relationships methods execute in the ModCore
        // constructor, which in SHVDN3 can run before the game is fully ready - relationship
        // natives issued that early are SILENTLY DROPPED (the SET_RELATIONSHIP_BETWEEN_GROUPS
        // calls don't stick; playtest logs showed the ctor player-group hash matching the live
        // group while the stances still failed, so it's an early-native-drop, not a phantom ped).
        // That left survivors Neutral (armed peds engage the player mid-fight, the "green-blip
        // survivors shoot me" report) and bandits Neutral (they only ever fought the player on
        // retaliation, not on sight). Idempotent: no-op binding when the startup bind already
        // stuck. See DOCS/08-Lessons-Learned.md #33.
        public static void EnsurePlayerFacingRelationships()
        {
            var playerPed = Game.Player.Character;
            if (playerPed?.Exists() is not true)
                return;
            int playerGroup = playerPed.RelationshipGroup.Hash;

            SetRelationship(SurvivorRelationshipGroup, playerGroup, 0);
            SetRelationship(playerGroup, SurvivorRelationshipGroup, 0);

            if (RecruitRelationshipGroup != 0)
            {
                SetRelationship(RecruitRelationshipGroup, playerGroup, 0);
                SetRelationship(playerGroup, RecruitRelationshipGroup, 0);
            }

            foreach (var faction in FactionSystem.Instance.Definitions)
            {
                if (faction.RelationshipGroup <= 0)
                    continue;
                int rel = faction.RelationToPlayer switch
                {
                    PlayerRelation.Hostile => 5,
                    PlayerRelation.Ally => 0,
                    _ => 3
                };
                SetRelationship(faction.RelationshipGroup, playerGroup, rel);
                SetRelationship(playerGroup, faction.RelationshipGroup, rel);
            }

            // The ctor can silently drop ALL relationship natives, not just the player-facing
            // ones (lesson 33) - re-assert the faction↔zombie binds (including the one-way
            // Altruist truce) and the cross-faction matrix on the first tick too, so hostile
            // zone defenders always fight the player and the truce holds.
            foreach (var faction in FactionSystem.Instance.Definitions)
            {
                if (faction.RelationshipGroup <= 0)
                    continue;
                if (faction.SparesZombies)
                {
                    SetRelationshipDirectional(faction.RelationshipGroup, InfectedRelationshipGroup, 3);
                    SetRelationshipDirectional(InfectedRelationshipGroup, faction.RelationshipGroup, 5);
                }
                else
                {
                    SetRelationship(faction.RelationshipGroup, InfectedRelationshipGroup, 5);
                    SetRelationship(InfectedRelationshipGroup, faction.RelationshipGroup, 5);
                }
            }
            foreach (var faction in FactionSystem.Instance.Definitions)
            {
                if (faction.RelationshipGroup <= 0)
                    continue;
                foreach (var other in FactionSystem.Instance.Definitions)
                {
                    if (faction == other || other.RelationshipGroup <= 0)
                        continue;
                    bool aHatesB = HatedFactionIds(faction.Id).Contains(other.Id);
                    bool bHatesA = HatedFactionIds(other.Id).Contains(faction.Id);
                    SetRelationship(faction.RelationshipGroup, other.RelationshipGroup, aHatesB ? 5 : 3);
                    SetRelationship(other.RelationshipGroup, faction.RelationshipGroup, bHatesA ? 5 : 3);
                }
            }

            Logger.Info($"Player relationships re-asserted on first tick: playerGroup={playerGroup} "
                + (ConstructorPlayerGroup == playerGroup
                    ? "(same as ctor bind - ctor group was already live)"
                    : $"(ctor bind used phantom group {ConstructorPlayerGroup} - this re-assert corrected it)")
                + $" survivor->player={Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_GROUPS, SurvivorRelationshipGroup, playerGroup)} "
                + $"families->player={Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_GROUPS, FactionSystem.Instance.GetFaction(FactionCatalog.BanditFactionId).RelationshipGroup, playerGroup)}");
        }

        // Cross-faction hostility: The Families (bandits) raid survivor outposts, The Lost are a
        // rival gang to both. Military/Altruists are isolationist - they defend their own turf and
        // fight the player/zombies, but don't pick fights with the other factions.
        private static string[] HatedFactionIds(string id)
        {
            return id switch
            {
                FactionCatalog.SurvivorFactionId => [FactionCatalog.BanditFactionId, FactionCatalog.LostFactionId],
                FactionCatalog.BanditFactionId => [FactionCatalog.SurvivorFactionId, FactionCatalog.LostFactionId],
                FactionCatalog.LostFactionId => [FactionCatalog.SurvivorFactionId, FactionCatalog.BanditFactionId],
                _ => [],
            };
        }

        // SET_RELATIONSHIP_BETWEEN_GROUPS is directional - mirror every entry so both groups
        // see the same stance. Codes: 0 companion, 1 respect, 2 like, 3 neutral, 4 dislike, 5 hate.
        private static void SetRelationship(int groupA, int groupB, int rel)
        {
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, rel, groupA, groupB, 1);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, rel, groupB, groupA, 1);
        }

        // One-way relationship bind (only groupA -> groupB). The mirrored SetRelationship is the
        // normal case; SparesZombies needs faction->zombie Neutral while zombie->faction stays
        // Hate so zombies keep hunting the sparing faction.
        private static void SetRelationshipDirectional(int groupA, int groupB, int rel)
        {
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, rel, groupA, groupB, 1);
        }

        public static bool IsFactionPed(this Ped ped)
        {
            return FactionRelationshipGroups.Contains(ped.RelationshipGroup.Hash);
        }

        public static bool HasClearLineOfSightTo(this Ped ped, Entity target)
        {
            return Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, ped, target, 17);
        }

        public static void MakeZombieLike(this Ped ped)
        {
            ped.CanPlayGestures = false;
            ped.KeepTaskWhenMarkedAsNoLongerNeeded = true;
            ped.BlockPermanentEvents = true;

            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 223, false);
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 166, false);
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 224, false);
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 225, false);
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 145, true);
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 281, true);
            // Flag 292 is PED_FLAG_FREEZE - it was being set to true here, which freezes the
            // ped's skeleton from the instant it spawns. A frozen ped never updates its pose
            // again, so it's locked into the raw bind pose (T-pose) forever no matter what
            // clip set is assigned. THIS was the actual T-pose bug, not the clipset - removed.

            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 0, false);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 5, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 46, true);

            // Zombies never flee or dodge. The actual controls for explosion/gunfire panic are
            // ped config flags, not SET_PED_FLEE_ATTRIBUTES (which with (0, 0) is a no-op):
            // flag 118 = RunFromFiresAndExplosions, flag 208 = DisableExplosionReactions,
            // flag 39 = DisableEvasiveDives (the same behavior SET_PED_CAN_EVASIVE_DIVE toggles).
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 118, false);
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 208, true);
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 39, true);
            Function.Call(Hash.SET_PED_CAN_EVASIVE_DIVE, ped, false);
            // An ignited zombie STILL panics/flees even with 118/208 false - the running-around-
            // screaming CTaskFirePanic is a separate reaction, not a "run from fire" threat
            // response. Flag 430 = CPED_CONFIG_FLAG_IgnoreBeingOnFire suppresses that panic, so
            // a molotov'd zombie keeps pressing its normal Wandering/Chasing/attack behavior
            // while burning. Trade-off: the flag also suppresses the engine fire's damage over
            // time, so ZombieAI.Update re-applies scripted burn damage on a cadence (see the
            // burn check) - burning zombies keep attacking AND burn to death.
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 430, true);

            // Pathfinding hints to stay out of water. The pathfinder does not honor these for
            // every task type, so ZombieAI.Update also force-pulls swimming zombies back onto
            // dry ground (see the IsInWater guard in ZombieAI).
            Function.Call(Hash.SET_PED_PATH_MAY_ENTER_WATER, ped, false);
            Function.Call(Hash.SET_PED_PATH_PREFER_TO_AVOID_WATER, ped, true);

            // Zombies never climb ladders - flag 146 = CPED_CONFIG_FLAG_DisableLadderClimbing
            // stops the climb interaction itself, and SET_PED_PATH_CAN_USE_LADDERS stops the
            // pathfinder from routing them up ladders in the first place.
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 146, true);
            Function.Call(Hash.SET_PED_PATH_CAN_USE_LADDERS, ped, false);

            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, ped, InfectedRelationshipGroup);

            if (_clipsetsLoaded)
            {
                Function.Call(Hash.SET_PED_MOVEMENT_CLIPSET, ped, ZombieMovementClipset, 0.25f);
                Function.Call(Hash.SET_PED_STRAFE_CLIPSET, ped, ZombieMovementClipset);
                Function.Call(Hash.SET_PED_USING_ACTION_MODE, ped, true, -1, ZombieActionModeClipset);
                Function.Call(Hash.SET_WEAPON_ANIMATION_OVERRIDE, ped, StringHash.AtStringHash("ZOMBIE", 0));
            }
            // else: clipset failed to load - leave the ped's default model moveset alone.
            // A normal-looking default walk is far better than forcing SET_PED_MOVEMENT_CLIPSET
            // on a clip set we haven't confirmed is actually resident.

            Function.Call(Hash.STOP_PED_SPEAKING, ped, true);
            // Suppress the engine's automatic pain/death groans (the vanilla human scream on a
            // gunshot kill and the death moan) - scripted zombie gurgles/moans are played by
            // ZombieAI instead, which re-enables pain audio for the single PLAY_PAIN call
            // (pain sounds are muted while pain audio is disabled - reference-mod verified).
            Function.Call(Hash.DISABLE_PED_PAIN_AUDIO, ped, true);
            // Facial expression + gore are applied by the ZombieAI ctor right after this
            // (ApplyZombieFace / ApplyZombieFaceGore) - the random mood choice is stored
            // per-zombie there so the per-task re-apply in Update re-uses it.
        }

        private static readonly WeaponHash[] SurvivorWeapons =
        [
            WeaponHash.Pistol,
            WeaponHash.CombatPistol,
            WeaponHash.MicroSMG,
            WeaponHash.AssaultRifle,
        ];

        // Armed human NPCs that engage or flee zombies. Weapons are given via GIVE_WEAPON_TO_PED
        // (exposed as Weapons.Give in SHVDN3) so zombies actually take damage from their shots,
        // not just play panic animations. Fight vs flee is decided per-survivor at spawn; the
        // scripted TASK_COMBAT_PED / TASK_SMART_FLEE_PED in SurvivorAI drives the actual behavior.
        // Returns the weapon that was equipped so the caller can drop it as loot on death.
        // NOTE: BlockPermanentEvents is deliberately NOT set - survivors must still react to
        // damage (they take hits and can die for loot), they just don't retaliate because the
        // Survivor group is RESPECT toward the player.
        public static WeaponHash MakeSurvivorLike(this Ped ped, bool isCoward)
        {
            ped.CanPlayGestures = false;
            ped.KeepTaskWhenMarkedAsNoLongerNeeded = true;

            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, ped, SurvivorRelationshipGroup);

            var weapon = SurvivorWeapons[new Random().Next(SurvivorWeapons.Length)];
            ped.Weapons.Give(weapon, 120, true, true);
            ped.Weapons.Select(weapon);
            Function.Call(Hash.SET_PED_ACCURACY, ped, 60);

            // Combat AI guard-rails (NATIVES/PED/SetPedCombatAttributes.md): CA_DISABLE_SECONDARY_TARGET
            // (25) stops a ped mid-combat from switching to a secondary target - without it a
            // survivor fighting a zombie retargets onto the (friendly) player the moment the player
            // shoots that zombie, which is the "survivors go hostile when I shoot zombies" bug.
            // CA_DISABLE_REACT_TO_BUDDY_SHOT (68) stops retaliation from accidental stray-bullet
            // hits during a shared fight. Together with the Survivor group's RESPECT stance toward
            // the player, survivors never flip hostile; direct damage just kills them for loot.
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 25, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 68, true);

            // Fighters hold their ground and keep shooting (no fleeing from danger/attacks).
            // Cowards keep the default fear behavior and will flee via scripted tasks.
            if (!isCoward)
            {
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped, 0, false);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped, 1, false);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped, 2, false);
                Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 118, false); // RunFromFiresAndExplosions
            }
            else
            {
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped, 2, true);
            }

            Function.Call(Hash.STOP_PED_SPEAKING, ped, true);
            return weapon;
        }

        // Phase 4 faction members (DOCS/06-Roadmap.md §4.3). Assigns the faction relationship group,
        // arms them from the faction's weapon list, and - for defensive factions - disables
        // fleeing so outpost guards hold their ground. Aggressive factions keep the default fear
        // behavior so bandits scatter when outgunned. Returns the equipped weapon so the caller
        // can drop it as loot on death (same pattern as MakeSurvivorLike).
        public static WeaponHash MakeFactionLike(this Ped ped, FactionDef faction)
        {
            ped.CanPlayGestures = false;
            ped.KeepTaskWhenMarkedAsNoLongerNeeded = true;

            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, ped, faction.RelationshipGroup);

            var weapon = faction.Weapons[new Random().Next(faction.Weapons.Length)];
            ped.Weapons.Give(weapon, 120, true, true);
            ped.Weapons.Select(weapon);
            Function.Call(Hash.SET_PED_ACCURACY, ped, faction.Accuracy);

            // Same guard-rails as MakeSurvivorLike: no mid-combat retargeting onto the player,
            // and no retaliation for stray-bullet buddy hits. Hostile factions still attack
            // the player as their primary target via the hate relationship - this only stops the
            // accidental flip for friendly ones (survivor guards at owned outposts, which are
            // also RESPECT to the player via the shared Survivor group).
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 25, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 68, true);

            if (faction.Archetype == FactionArchetype.Defensive)
            {
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped, 0, false);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped, 1, false);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped, 2, false);
                Function.Call(Hash.SET_PED_CONFIG_FLAG, ped, 118, false); // RunFromFiresAndExplosions
            }

            Function.Call(Hash.STOP_PED_SPEAKING, ped, true);
            return weapon;
        }

        private static string GetFacialAnimDict(Ped ped)
        {
            return ped.Gender == Gender.Female ? "facials@gen_female@base" : "facials@gen_male@base";
        }

        // Persistent facial expression. SET_FACIAL_CLIPSET (0x5687C7F05B39E401) is the same
        // native as the undocumented _SET_FACIAL_CLIPSET_OVERRIDE and is required to actually
        // switch the ped onto the per-gender facial clipset - SET_FACIAL_IDLE_ANIM_OVERRIDE
        // against the default clipset silently fails. The clipset persists once set; only the
        // idle override is cleared when the ped starts a new task, so re-applies pass
        // reapply:true to skip re-setting the clipset.
        public static void ApplyZombieFace(this Ped ped, string anim, bool reapply)
        {
            if (!_facialDictsLoaded)
                return;
            string dict = GetFacialAnimDict(ped);
            if (!reapply)
                Function.Call(Hash.SET_FACIAL_CLIPSET, ped, dict);
            Function.Call(Hash.SET_FACIAL_IDLE_ANIM_OVERRIDE, ped, anim, dict);
        }

        // One-shot facial anim. PLAY_FACIAL_ANIM is visible even mid-task, unlike the idle
        // override which the engine clears whenever the ped starts a new task (zombies are
        // almost never idle) - ZombieAI fires this on a slow cadence so the expression shows.
        public static void PlayZombieFaceAnim(this Ped ped, string anim)
        {
            if (!_facialDictsLoaded)
                return;
            Function.Call(Hash.PLAY_FACIAL_ANIM, ped, anim, GetFacialAnimDict(ped));
        }

        // Guaranteed-visible undead face: permanent blood/gore damage packs. Reference values
        // from GTAV-SimpleZombies ZombieCreator (BigHitByVehicle + ExplosionMed) with damage 0
        // so it's cosmetic only - reads as "zombie" even if the facial rig never renders an anim.
        public static void ApplyZombieFaceGore(this Ped ped)
        {
            Function.Call(Hash.APPLY_PED_DAMAGE_PACK, ped, "BigHitByVehicle", 0f, 0.6f);
            Function.Call(Hash.APPLY_PED_DAMAGE_PACK, ped, "ExplosionMed", 0f, 0.6f);
        }

        public static void ResetClipsets(this Ped ped)
        {
            Function.Call(Hash.SET_PED_MOVEMENT_CLIPSET, ped, "move_m@generic", 0f);
            Function.Call(Hash.SET_PED_STRAFE_CLIPSET, ped, "move_m@generic");
            Function.Call(Hash.SET_PED_USING_ACTION_MODE, ped, false, -1, "DEFAULT_ACTION_MODE");
            // No CLEAR_WEAPON_ANIMATION_OVERRIDE native in this SHVDN version's Hash enum -
            // re-calling SET_WEAPON_ANIMATION_OVERRIDE with hash 0 is the standard way to clear it.
            Function.Call(Hash.SET_WEAPON_ANIMATION_OVERRIDE, ped, 0);
        }

        public static void DisableRagdoll(this Ped ped)
        {
            Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped, false);
        }
    }
}
