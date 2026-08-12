using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTAZ.AI;
using GTAZ.Core;
using GTAZ.Extensions;
using GTAZ.Utils;

namespace GTAZ.Systems
{
    // Armed human NPCs that are NOT zombies. Survivors are transient encounters, not permanent
    // population: they appear near the player and at horde announcement positions, fight or flee
    // zombies, and despawn once the player leaves range so exploring the map keeps producing new
    // encounters. Recruiting (Phase 4) lets the player recruit any green-blip survivor. Engine-spawned
    // NPCs are also converted into survivors so ordinary civilians are armed and react to zombies.
    // Budgeted through PedBudgetManager (PedCategory.Survivor); WorldSystem skips them when clearing.
    public class SurvivorSystem
    {
        // Singleton so FactionSystem can adopt ambient survivors at recruit time (the recruit
        // flow must stop the SurvivorAI ticking on the ped or its wander/combat/flee tasks fight
        // the player-group formation AI and recruits never follow). ModuleManager uses this
        // instance - no second SurvivorSystem may be constructed.
        public static SurvivorSystem Instance { get; } = new SurvivorSystem();

        // Civilian models ONLY - no overlap with zombie walkers/runners or faction peds, so a
        // survivor never looks like a zombie (the old list shared Business01AMY/Methhead/Hillbilly/
        // Hippy/Strpreach/Tramp/Juggalo with the zombie pools) and a survivor can't be mistaken
        // for a Family/Lost/Military/Altruist member.
        private static readonly int[] SurvivorModels =
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
        ];

        private readonly List<SurvivorAI> _survivors = [];
        private readonly PedBudgetManager _budget = PedBudgetManager.Instance;
        private readonly SpawnRequestQueue _queue = SpawnRequestQueue.Instance;
        private readonly Random _rng = new();

        private int _spawnTimer;
        private int _convertTimer;

        public int ActiveCount => _survivors.Count;

        public void OnTick()
        {
            if (Game.Player.Character.IsDead || !Game.Player.CanControlCharacter)
                return;

            UpdateSurvivors();
            TryAmbientSpawn();

            if (++_convertTimer >= 120)
            {
                _convertTimer = 0;
                ConvertNearbyEngineNpcs();
            }
        }

        private void UpdateSurvivors()
        {
            var config = ModConfig.Current;
            var playerPos = Game.Player.Character.Position;

            for (int i = _survivors.Count - 1; i >= 0; i--)
            {
                var survivor = _survivors[i];
                survivor.Update();

                if (survivor.Ped?.Exists() is not true)
                {
                    survivor.Cleanup();
                    _survivors.RemoveAt(i);
                    continue;
                }

                if (survivor.Ped.IsDead)
                {
                    // Drop their weapon + ammo so the player is rewarded for attacking them.
                    DropLoot(survivor);
                    survivor.OnDeath();
                    _survivors.RemoveAt(i);
                    continue;
                }

                // Transient encounters: fade once the player leaves range so travelling the map
                // keeps finding new survivors instead of only the ones that spawned near spawn.
                if ((survivor.Ped.Position - playerPos).Length() > config.SurvivorDespawnDistance)
                {
                    survivor.Cleanup();
                    _survivors.RemoveAt(i);
                }
            }
        }

        private void TryAmbientSpawn()
        {
            var config = ModConfig.Current;
            if (!config.EnableSurvivors)
                return;

            if (_survivors.Count >= config.SurvivorCount)
                return;

            if (++_spawnTimer < 240)
                return;
            _spawnTimer = 0;

            if (_budget.Available <= 0)
                return;

            var playerPos = Game.Player.Character.Position;
            var pos = SpawnUtils.FindSafePosition(playerPos, 30f, 80f);
            if (pos != Vector3.Zero)
                SpawnSurvivorAt(pos);
        }

        private void SpawnSurvivorAt(Vector3 pos)
        {
            bool coward = _rng.NextDouble() < ModConfig.Current.SurvivorCowardChance;
            bool capturedCoward = coward;
            var capturedPos = pos;

            var request = new SpawnRequest
            {
                Category = PedCategory.Survivor,
                Priority = PedPriority.Active,
                Position = capturedPos,
                Origin = "Survivor",
                SpawnFactory = () =>
                {
                    var modelHash = SurvivorModels[_rng.Next(SurvivorModels.Length)];
                    var model = new Model(modelHash);
                    model.Request();
                    return World.CreatePed(model, capturedPos, _rng.Next(0, 360));
                },
                OnGranted = slot =>
                {
                    var ped = slot.Ped;
                    if (ped?.Exists() is not true)
                        return;

                    var weapon = ped.MakeSurvivorLike(capturedCoward);
                    _survivors.Add(new SurvivorAI(ped, capturedPos, capturedCoward, weapon));
                }
            };

            _queue.Enqueue(request);
        }

        // Arms engine-spawned civilian NPCs (the occasional peds the population system places)
        // so ordinary humans react to zombies instead of being oblivious. Runs on a slow cadence
        // and caps at SurvivorCount so we never convert half the city. Faction peds (bandits,
        // survivor guards) are skipped - converting a bandit into a SurvivorAI wrapper double-
        // wrapped it (FactionNPC + SurvivorAI on one ped) and stacked a green Friend blip on top
        // of its orange Enemy blip.
        private void ConvertNearbyEngineNpcs()
        {
            var config = ModConfig.Current;
            if (!config.EnableSurvivors)
                return;

            int cap = config.SurvivorCount;
            if (_survivors.Count >= cap)
                return;

            var playerPos = Game.Player.Character.Position;
            foreach (var ped in World.GetNearbyPeds(playerPos, 150f))
            {
                if (_survivors.Count >= cap)
                    break;
                ConvertEnginePed(ped);
            }
        }

        public bool ConvertEnginePed(Ped ped)
        {
            if (ped?.Exists() is not true || ped.IsPlayer || ped.IsDead || !ped.IsHumanoid())
                return false;
            if (ped.IsZombie() || ped.IsSurvivor() || ped.IsFactionPed())
                return false;

            bool coward = _rng.NextDouble() < ModConfig.Current.SurvivorCowardChance;
            var weapon = ped.MakeSurvivorLike(coward);
            _survivors.Add(new SurvivorAI(ped, ped.Position, coward, weapon));
            return true;
        }

        // Hands an ambient/converted survivor ped over to FactionSystem at recruit time: stops
        // the SurvivorAI from ticking it (its TASK_WANDER_IN_AREA/TASK_COMBAT_PED/TASK_SMART_FLEE
        // would fight the player-group formation AI and the despawn-distance cleanup would delete
        // a recruit mid-follow). Removes the survivor blip but leaves the ped alive - the recruit
        // path re-wraps it as an essential FactionNPC. Returns the detached SurvivorAI (caller
        // reads its Origin/Weapon) or null if the ped isn't a tracked ambient survivor.
        public SurvivorAI DetachAmbientSurvivor(Ped ped)
        {
            for (int i = _survivors.Count - 1; i >= 0; i--)
            {
                if (_survivors[i].Ped == ped)
                {
                    var survivor = _survivors[i];
                    survivor.Detach();
                    _survivors.RemoveAt(i);
                    return survivor;
                }
            }
            return null;
        }

        private void DropLoot(SurvivorAI survivor)
        {
            // Null model = 0 hash, so the game picks the weapon's default pickup model.
            PedLootUtils.DropLoot(survivor.Ped, survivor.Weapon);
        }

        public void ClearAllSurvivors()
        {
            foreach (var survivor in _survivors)
                survivor.Cleanup();
            _survivors.Clear();
        }
    }
}
