using System;
using System.Linq;
using GTA;
using GTA.Native;
using GTAZ.Data;
using GTAZ.Data.Serialization;
using GTAZ.Core;

namespace GTAZ.Systems
{
    public class PersistenceManager
    {
        private static PersistenceManager _instance;
        public static PersistenceManager Instance => _instance ??= new PersistenceManager();

        private DateTime _lastAutoSave;
        public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromMinutes(5);

        public void Save(string slot = "save.ini")
        {
            try
            {
                var player = Game.Player.Character;
                if (player?.Exists() is not true)
                    return;

                var data = new PlayerData
                {
                    Position = player.Position,
                    Heading = player.Heading,
                    Health = player.Health,
                    Armor = player.Armor,
                    OwnedTerritoryHashes = [.. FactionSystem.Instance.Zones
                        .Where(z => z.IsPlayerOwned)
                        .Select(z => z.ZoneHash)]
                };

                IniSerializer.Serialize("PlayerData_" + slot, data);
                Logger.Info($"Game saved to {slot}");

                // The mod save only carries the mod's state (health/armor/position/owned
                // territories). Fire the game's own autosave alongside it so the full GTA state
                // (money, weapons, ammo, purchases, game time, stats) is saved too - weapons
                // don't persist in the mod save, only in the game save.
                try
                {
                    Function.Call(Hash.DO_AUTO_SAVE);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Game autosave failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Save failed: {ex.Message}");
            }
        }

        public PlayerData Load(string slot = "save.ini")
        {
            try
            {
                var data = IniSerializer.Deserialize<PlayerData>("PlayerData_" + slot);
                Logger.Info($"Game loaded from {slot}");
                return data;
            }
            catch (Exception ex)
            {
                Logger.Error($"Load failed: {ex.Message}");
                return null;
            }
        }

        public bool LoadAndApply(string slot = "save.ini")
        {
            var data = Load(slot);
            if (data is null)
                return false;

            var player = Game.Player.Character;
            if (player?.Exists() is not true)
                return false;

            player.Position = data.Position;
            player.Heading = data.Heading;
            player.Health = (int)data.Health;
            player.Armor = (int)data.Armor;

            // Restore owned territories (zone data is initialized by the time this runs - the
            // startup load fires on the first tick, after FactionSystem has built its zones).
            FactionSystem.Instance.RestoreOwnedTerritories(data.OwnedTerritoryHashes);

            GTA.UI.Notification.PostTicker("~g~Game loaded.", false);
            return true;
        }

        // Loads the freshest save at game start so a session resumes where it left off: the
        // autosave first (it's the most recent), falling back to the manual save when no
        // autosave exists yet. No-op (returns false) on a first-ever run with no saves.
        public bool LoadMostRecentAndApply()
        {
            string slot = "autosave.ini";
            if (!IniSerializer.Exists("PlayerData_" + slot))
                slot = "manual_save.ini";
            if (!IniSerializer.Exists("PlayerData_" + slot))
                return false;

            return LoadAndApply(slot);
        }

        public void TickAutoSave()
        {
            if (DateTime.UtcNow - _lastAutoSave >= AutoSaveInterval)
            {
                Save("autosave.ini");
                _lastAutoSave = DateTime.UtcNow;
            }
        }
    }
}
