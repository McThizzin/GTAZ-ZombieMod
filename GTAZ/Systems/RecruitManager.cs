using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Events;
using GTAZ.Extensions;
using GTAZ.UI;
using GTAZ.Utils;

namespace GTAZ.Systems
{
    // Phase 4 recruiting (DOCS/06-Roadmap.md §4.7), split out of FactionSystem. Owns the
    // _recruits list and everything that touches it: the soft-aim consumer (SoftAimController +
    // its validity predicate), the recruit/dismiss actions, and the per-tick recruit upkeep.
    //
    // Callback couplings into FactionSystem (per the §3.4 plan): _findSurvivorNpc searches
    // _activePeds for the survivor-faction FactionNPC wrapping a ped, _addActivePed/_removeActivePed
    // move an NPC between the recruit list and FactionSystem's active list on dismiss/join, and
    // _releaseSlot hands a dead/lost recruit's budget slot back. Soft-aim stays generic - the
    // controller takes the predicate here because survivor recruiting is its only current use.
    public class RecruitManager
    {
        private readonly List<FactionNPC> _recruits = [];
        private readonly SoftAimController _softAim;
        private readonly FactionDef _survivors;
        private readonly Func<Ped, FactionNPC> _findSurvivorNpc;
        private readonly Action<FactionNPC> _addActivePed;
        private readonly Action<FactionNPC> _removeActivePed;
        private readonly Action<FactionNPC> _releaseSlot;

        public int RecruitCount => _recruits.Count;

        // Ticks the soft-aim controller (H toggle, reticule, camera-raycast lock, highlight).
        // Called from FactionSystem.OnTick while factions are enabled.
        public void UpdateSoftAim() => _softAim.Update();

        public RecruitManager(FactionDef survivors, Func<Ped, FactionNPC> findSurvivorNpc,
            Action<FactionNPC> addActivePed, Action<FactionNPC> removeActivePed, Action<FactionNPC> releaseSlot)
        {
            _survivors = survivors;
            _findSurvivorNpc = findSurvivorNpc;
            _addActivePed = addActivePed;
            _removeActivePed = removeActivePed;
            _releaseSlot = releaseSlot;
            _softAim = new SoftAimController(IsRecruitableSoftTarget);
        }

        // --- Soft-aim recruiting ---
        //
        // The soft-aim targeting itself (H toggle, reticule, camera-raycast lock, highlight
        // marker) lives in SoftAimController; this predicate tells it what's lockable. Currently
        // the only lockable targets are survivors - either survivor-faction FactionNPCs (outpost
        // guards) or ambient/converted survivors (SurvivorSystem's SurvivorAI) - both adoptable
        // at RecruitKey press, so both lock green. Recruiting without a lock falls back to a
        // plain camera raycast (TryRecruitAtCrosshair).
        private bool IsRecruitableSoftTarget(Ped target)
        {
            if (target?.Exists() is not true || target.IsDead || target.IsPlayer || target.IsInGroup)
                return false;

            var npc = _findSurvivorNpc(target);
            return npc is not null || target.IsSurvivor();
        }

        // Camera-raycast recruit action, bound to RecruitKey (default X) in ModCore. A soft-aim
        // lock (toggled with SoftAimKey) takes priority over the raw raycast so recruiting never
        // requires aiming a weapon at a survivor.
        public bool TryRecruitAtCrosshair()
        {
            var player = Game.Player.Character;
            if (player?.Exists() is not true || player.IsDead || !Game.Player.CanControlCharacter)
                return false;

            if (!ModConfig.Current.EnableFactions)
            {
                GTA.UI.Notification.PostTicker("~r~Factions are disabled.", false);
                return false;
            }

            // Soft-aim lock wins: recruiting must never require pointing a weapon at a neutral
            // survivor (that's what flipped them hostile pre-recruit).
            var soft = _softAim.LockedTarget;
            if (soft?.Exists() is true && !soft.IsDead && !soft.IsPlayer && !soft.IsInGroup)
            {
                bool ok = TryToggleRecruit(soft);
                if (ok)
                    _softAim.ClearLock();
                return ok;
            }

            var hit = World.Raycast(GameplayCamera.Position, GameplayCamera.Direction, 250f,
                IntersectFlags.Peds | IntersectFlags.Map | IntersectFlags.Vehicles);
            if (!hit.DidHit || hit.HitEntity is not Ped)
            {
                GTA.UI.Notification.PostTicker("~y~Aim at a survivor to recruit.", false);
                return false;
            }

            var target = hit.HitEntity as Ped;
            if (target.IsPlayer || target.IsDead)
                return false;

            return TryToggleRecruit(target);
        }

        // Adds/removes a survivor faction ped to/from the player's group. Only peds owned by
        // FactionSystem (ambient survivors + outpost guards) are recruitable - their FactionNPC
        // owns the budget slot so it can be flagged essential and won't be evicted by the budget.
        private bool TryToggleRecruit(Ped ped)
        {
            var existing = _recruits.FirstOrDefault(n => n.Ped == ped);
            if (existing != null)
            {
                DismissRecruit(existing);
                GTA.UI.Notification.PostTicker("~y~Recruit left your group.", false);
                return true;
            }

            if (RecruitCount >= ModConfig.Current.MaxRecruits)
            {
                GTA.UI.Notification.PostTicker($"~r~Recruit cap reached ({ModConfig.Current.MaxRecruits}).", false);
                return false;
            }

            var npc = _findSurvivorNpc(ped);
            if (npc is null)
            {
                // Ambient/converted survivor (SurvivorSystem.SurvivorAI, not a FactionNPC):
                // adopt the ped on the spot so recruiting works for the survivors the player
                // actually meets. The ped must be a survivor (green blip) - bandits/guards that
                // aren't survivor-faction stay un-recruitable.
                if (!ped.IsSurvivor())
                {
                    GTA.UI.Notification.PostTicker("~r~Only survivors can be recruited.", false);
                    return false;
                }
                var ambient = SurvivorSystem.Instance.DetachAmbientSurvivor(ped);
                if (ambient is null)
                {
                    GTA.UI.Notification.PostTicker("~r~Only survivors can be recruited.", false);
                    return false;
                }
                npc = new FactionNPC(ped, _survivors, ambient.Origin, FactionRole.Recruit,
                    null, null, ambient.Weapon);
            }
            if (npc.Ped.IsInGroup)
                return false;

            int group = Function.Call<int>(Hash.GET_PLAYER_GROUP, Game.Player);
            npc.Ped.Task.ClearAllImmediately();
            Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, npc.Ped, group);

            // Move the recruit onto its own relationship group (DOCS/06-Roadmap.md §4.7) so the
            // player↔recruit stance is COMPANION instead of the shared survivor group's
            // Neutral. Companion is what stops the formation/combat AI from pitting two
            // recruits against each other - with 2+ recruits they were picking the other
            // recruit as an enemy because Neutral group members are valid combat targets.
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, npc.Ped, PedExtensions.RecruitRelationshipGroup);

            _removeActivePed(npc);
            npc.SetRole(FactionRole.Recruit);
            npc.MarkEssential();
            _recruits.Add(npc);

            GTA.UI.Notification.PostTicker("~g~Recruit joined your group.", false);
            FactionEvents.RaiseRecruitJoined(ped);
            return true;
        }

        // Shared dismiss path for one recruit (TryToggleRecruit) and all recruits (DismissAllRecruits):
        // leaves the player group, reverts to the shared survivor group, and returns the NPC to
        // FactionSystem's active ped tracking as a patrol.
        private void DismissRecruit(FactionNPC npc)
        {
            npc.Ped.LeaveGroup();
            npc.ClearEssential();
            npc.SetRole(FactionRole.Patrol);
            // Revert to the shared survivor group (Companion to the player) - the recruit
            // group's extra stances (Companion both ways with survivors + per-faction hate)
            // only apply while recruited.
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, npc.Ped, PedExtensions.SurvivorRelationshipGroup);
            _recruits.Remove(npc);
            _addActivePed(npc);
            FactionEvents.RaiseRecruitLeft(npc.Ped);
        }

        public void DismissAllRecruits()
        {
            if (_recruits.Count == 0)
            {
                GTA.UI.Notification.PostTicker("~y~No recruits to dismiss.", false);
                return;
            }

            int count = _recruits.Count;
            for (int i = _recruits.Count - 1; i >= 0; i--)
                DismissRecruit(_recruits[i]);

            GTA.UI.Notification.PostTicker($"~y~Dismissed all recruits ({count}).", false);
            Logger.Info("Dismissed all recruits");
        }

        // Per-tick recruit upkeep (FactionSystem.UpdatePeds). Recruits never distance-despawn -
        // they follow the player - but the engine can cull them if it clears the persistent
        // flag, so a missing ped is diagnostic-worthy, and a dead one drops its weapon like any
        // faction member. Runs even when factions are toggled off so recruits stay functional.
        public void Update(Vector3 playerPos)
        {
            for (int i = _recruits.Count - 1; i >= 0; i--)
            {
                var npc = _recruits[i];
                npc.Update();

                if (npc.Ped?.Exists() is not true)
                {
                    // Diagnostic: distinguish engine culling (ped deleted out from under us)
                    // from a script removal. Nothing deletes a recruit except this loop and
                    // ClearAll, so a missing ped here is the game culling it - likely the
                    // persistent flag being cleared while the engine manages the player group.
                    // Logging last-seen distance tells us if it happened at range (culling) or
                    // up close (combat).
                    float dist = (npc.LastPosition - playerPos).Length();
                    Logger.Info($"Recruit lost: {npc.Faction.DisplayName} no longer exists (last seen {dist:F0}m away)");
                    _releaseSlot(npc);
                    _recruits.RemoveAt(i);
                    continue;
                }

                // The engine can clear a ped's persistent flag while managing player-group
                // members, which makes it eligible for culling even though MarkEssential set
                // it at join time. Re-assert every tick for recruits.
                npc.Ped.IsPersistent = true;

                if (npc.Ped.IsDead)
                {
                    Logger.Info($"Recruit died: {npc.Faction.DisplayName} at {(npc.Ped.Position - playerPos).Length():F0}m");
                    PedLootUtils.DropLoot(npc.Ped, npc.Weapon);
                    FactionEvents.RaiseFactionNPCDeath(npc.Ped, npc.Faction);
                    npc.OnDeath();
                    _releaseSlot(npc);
                    _recruits.RemoveAt(i);
                }
            }
        }

        // Clears all recruits (FactionSystem.ClearAllFactions): leave the player group, clean
        // up each NPC, and empty the list.
        public void ClearAll()
        {
            foreach (var npc in _recruits)
            {
                if (npc.Ped?.Exists() is true && npc.Ped.IsInGroup)
                    npc.Ped.LeaveGroup();
                npc.Cleanup();
            }
            _recruits.Clear();
        }
    }
}
