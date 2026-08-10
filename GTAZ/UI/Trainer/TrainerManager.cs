using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Data;
using GTAZ.UI.Trainer.Features;
using LemonUI;
using LemonUI.Menus;

namespace GTAZ.UI.Trainer
{
    public class TrainerManager
    {
        private const int MaxPendingVehicleSpawns = 3;
        private const int VehicleLoadTimeoutTicks = 600;

        public NativeMenu RootMenu { get; }

        private readonly NativeMenu _playerMenu;
        private readonly NativeMenu _vehicleMenu;
        private readonly NativeMenu _weaponMenu;
        private readonly NativeMenu _worldMenu;
        private readonly NativeMenu _vehicleSpawnMenu;

        private readonly List<PendingVehicleSpawn> _pendingSpawns = [];

        public TrainerManager()
        {
            // Same right-alignment as the rest of the UI (Alignment.Center throws; Left/Right only).
            RootMenu = new NativeMenu("GTAZ", "TRAINER") { Alignment = GTA.UI.Alignment.Right };
            _playerMenu = new NativeMenu("GTAZ", "PLAYER") { Alignment = GTA.UI.Alignment.Right };
            _vehicleMenu = new NativeMenu("GTAZ", "VEHICLE") { Alignment = GTA.UI.Alignment.Right };
            _weaponMenu = new NativeMenu("GTAZ", "WEAPON") { Alignment = GTA.UI.Alignment.Right };
            _worldMenu = new NativeMenu("GTAZ", "WORLD") { Alignment = GTA.UI.Alignment.Right };
            _vehicleSpawnMenu = new NativeMenu("GTAZ", "SPAWN VEHICLE") { Alignment = GTA.UI.Alignment.Right };

            RootMenu.AddSubMenu(_playerMenu);
            RootMenu.AddSubMenu(_vehicleMenu);
            RootMenu.AddSubMenu(_weaponMenu);
            RootMenu.AddSubMenu(_worldMenu);
            _vehicleMenu.AddSubMenu(_vehicleSpawnMenu);

            BuildPlayerMenu();
            BuildVehicleMenu();
            BuildWeaponMenu();
            BuildWorldMenu();
        }

        public void Initialize(MenuManager menus)
        {
            menus.Pool.Add(RootMenu);
            menus.Pool.Add(_playerMenu);
            menus.Pool.Add(_vehicleMenu);
            menus.Pool.Add(_weaponMenu);
            menus.Pool.Add(_worldMenu);
            menus.Pool.Add(_vehicleSpawnMenu);

            menus.MainMenu.AddSubMenu(RootMenu);

            VehicleCatalog.Load();
            BuildVehicleSpawnMenu(menus.Pool);
        }

        private void BuildPlayerMenu()
        {
            var godMode = new NativeCheckboxItem("God Mode", "Makes the player invincible", false);
            godMode.CheckboxChanged += (s, a) => PlayerTrainer.ToggleGodMode(godMode.Checked);

            var infiniteStamina = new NativeCheckboxItem("Infinite Stamina", "Sprint without tiring", false);
            infiniteStamina.CheckboxChanged += (s, a) => PlayerTrainer.InfiniteStamina(infiniteStamina.Checked);

            var superJump = new NativeCheckboxItem("Super Jump", "Jump high", false);
            superJump.CheckboxChanged += (s, a) => PlayerTrainer.SuperJump(superJump.Checked);

            var fastRun = new NativeCheckboxItem("Fast Run", "Run and sprint faster", false);
            fastRun.CheckboxChanged += (s, a) => PlayerTrainer.FastRun(fastRun.Checked);

            var heal = new NativeItem("Heal Player", "Restores health and armor");
            heal.Activated += (s, a) => PlayerTrainer.Heal();

            var clean = new NativeItem("Clean Player", "Removes blood and damage decals");
            clean.Activated += (s, a) => PlayerTrainer.Clean();

            var invisible = new NativeCheckboxItem("Invisible", "Hides the player model", false);
            invisible.CheckboxChanged += (s, a) => PlayerTrainer.SetVisibility(invisible.Checked);

            var money = new NativeSliderItem("Set Money ($10k)", "Sets cash in $10,000 steps", 1000, 1)
            {
                Multiplier = 10000
            };
            money.ValueChanged += (s, a) => PlayerTrainer.SetCash(money.Value * 10000);

            var wanted = new NativeSliderItem("Set Wanted Level", "Sets the wanted level (0-5)", 5, 0);
            wanted.ValueChanged += (s, a) => PlayerTrainer.SetWantedLevel(wanted.Value);

            var neverWanted = new NativeCheckboxItem("Never Wanted", "Locks the wanted level to zero", false);
            neverWanted.CheckboxChanged += (s, a) => PlayerTrainer.NeverWanted(neverWanted.Checked);

            var teleport = new NativeItem("Teleport to Waypoint", "Teleports to the map waypoint");
            teleport.Activated += (s, a) => PlayerTrainer.TeleportToWaypoint();

            _playerMenu.Add(godMode);
            _playerMenu.Add(infiniteStamina);
            _playerMenu.Add(superJump);
            _playerMenu.Add(fastRun);
            _playerMenu.Add(heal);
            _playerMenu.Add(clean);
            _playerMenu.Add(invisible);
            _playerMenu.Add(money);
            _playerMenu.Add(wanted);
            _playerMenu.Add(neverWanted);
            _playerMenu.Add(teleport);
        }

        private void BuildVehicleMenu()
        {
            var godMode = new NativeCheckboxItem("Vehicle God Mode", "Makes the current vehicle invincible", false);
            godMode.CheckboxChanged += (s, a) => VehicleTrainer.VehicleGodMode(godMode.Checked);

            var fix = new NativeItem("Fix Vehicle", "Repairs and washes the current vehicle");
            fix.Activated += (s, a) => VehicleTrainer.Fix();

            var clean = new NativeItem("Clean Vehicle", "Washes the current vehicle");
            clean.Activated += (s, a) => VehicleTrainer.Clean();

            var delete = new NativeItem("Delete Vehicle", "Deletes the current vehicle");
            delete.Activated += (s, a) => VehicleTrainer.Delete();

            var seatbelt = new NativeCheckboxItem("Seatbelt", "Prevents the player being thrown from the vehicle", false);
            seatbelt.CheckboxChanged += (s, a) => VehicleTrainer.Seatbelt(seatbelt.Checked);

            _vehicleMenu.Add(godMode);
            _vehicleMenu.Add(fix);
            _vehicleMenu.Add(clean);
            _vehicleMenu.Add(delete);
            _vehicleMenu.Add(seatbelt);
        }

        private void BuildVehicleSpawnMenu(ObjectPool pool)
        {
            foreach (var category in VehicleCatalog.Categories)
            {
                var subMenu = new NativeMenu("GTAZ", category.ToUpperInvariant()) { Alignment = GTA.UI.Alignment.Right };
                pool.Add(subMenu);

                foreach (var model in VehicleCatalog.GetModels(category))
                {
                    var item = new NativeItem(model);
                    item.Activated += (s, a) => RequestVehicleSpawn(model);
                    subMenu.Add(item);
                }

                _vehicleSpawnMenu.AddSubMenu(subMenu);
            }
        }

        private void BuildWeaponMenu()
        {
            var giveAll = new NativeItem("Give All Weapons", "Gives every weapon with max ammo");
            giveAll.Activated += (s, a) => WeaponTrainer.GiveAllWeapons();

            var removeAll = new NativeItem("Remove All Weapons", "Removes every weapon");
            removeAll.Activated += (s, a) => WeaponTrainer.RemoveAllWeapons();

            WeaponHash[] weapons = [.. Enum.GetValues(typeof(WeaponHash)).Cast<WeaponHash>()];
            var select = new NativeListItem<WeaponHash>("Select Weapon", "Gives the selected weapon", weapons);
            select.Activated += (s, a) =>
            {
                int index = select.SelectedIndex;
                if (index >= 0 && index < weapons.Length)
                    WeaponTrainer.SelectWeapon(weapons[index]);
                else
                    GTA.UI.Notification.PostTicker("~r~Invalid weapon selection.", false);
            };

            var infiniteAmmo = new NativeCheckboxItem("Infinite Ammo", "Current weapon never runs out", false);
            infiniteAmmo.CheckboxChanged += (s, a) =>
            {
                WeaponTrainer.SetInfiniteAmmo(infiniteAmmo.Checked);
                PlayerTrainer.ShowStatus("Infinite Ammo: ", infiniteAmmo.Checked);
            };

            var setAmmo = new NativeSliderItem("Set Ammo", "Sets the current weapon's ammo", 9999, 100);
            setAmmo.ValueChanged += (s, a) => WeaponTrainer.SetAmmo(setAmmo.Value);

            var removeCurrent = new NativeItem("Remove Current Weapon", "Removes the held weapon");
            removeCurrent.Activated += (s, a) => WeaponTrainer.RemoveCurrentWeapon();

            _weaponMenu.Add(giveAll);
            _weaponMenu.Add(removeAll);
            _weaponMenu.Add(select);
            _weaponMenu.Add(infiniteAmmo);
            _weaponMenu.Add(setAmmo);
            _weaponMenu.Add(removeCurrent);
        }

        private void BuildWorldMenu()
        {
            foreach (var weather in WorldTrainer.WeatherOptions)
            {
                var item = new NativeItem(weather);
                item.Activated += (s, a) => WorldTrainer.SetWeather(weather);
                _worldMenu.Add(item);
            }

            var freezeTime = new NativeCheckboxItem("Freeze Time", "Pauses the in-game clock", false);
            freezeTime.CheckboxChanged += (s, a) => WorldTrainer.FreezeTime(freezeTime.Checked);

            var hour = new NativeSliderItem("Set Hour", "Sets the hour (0-23)", 23, 0)
            {
                Value = GTA.Chrono.GameClock.Hour
            };
            hour.ValueChanged += (s, a) => WorldTrainer.SetHour(hour.Value);

            var minute = new NativeSliderItem("Set Minute", "Sets the minute (0-59)", 59, 0)
            {
                Value = GTA.Chrono.GameClock.Minute
            };
            minute.ValueChanged += (s, a) => WorldTrainer.SetMinute(minute.Value);

            var second = new NativeSliderItem("Set Second", "Sets the second (0-59)", 59, 0)
            {
                Value = GTA.Chrono.GameClock.Second
            };
            second.ValueChanged += (s, a) => WorldTrainer.SetSecond(second.Value);

            _worldMenu.Add(freezeTime);
            _worldMenu.Add(hour);
            _worldMenu.Add(minute);
            _worldMenu.Add(second);
        }

        private void RequestVehicleSpawn(string modelName)
        {
            var model = new Model(modelName);
            if (!model.IsValid || !model.IsInCdImage)
            {
                GTA.UI.Notification.PostTicker($"~r~Invalid vehicle: ~s~{modelName}", false);
                return;
            }

            if (_pendingSpawns.Count >= MaxPendingVehicleSpawns)
            {
                GTA.UI.Notification.PostTicker("~r~Vehicle spawn queue is full.", false);
                return;
            }

            var player = Game.Player.Character;
            _pendingSpawns.Add(new PendingVehicleSpawn
            {
                Model = model,
                ModelName = modelName,
                Position = player.Position + (player.ForwardVector * 5f),
                Heading = player.Heading
            });

            GTA.UI.Notification.PostTicker($"~b~Loading vehicle: ~s~{modelName}", false);
        }

        private void ProcessPendingVehicleSpawns()
        {
            for (int i = _pendingSpawns.Count - 1; i >= 0; i--)
            {
                var pending = _pendingSpawns[i];

                // Request every tick until loaded (same rule as clipset/animation loading).
                pending.Model.Request();
                if (pending.Model.IsLoaded)
                {
                    var vehicle = World.CreateVehicle(pending.Model, pending.Position, pending.Heading);
                    if (vehicle != null)
                    {
                        vehicle.PlaceOnGround();
                        vehicle.MarkAsNoLongerNeeded();
                        Game.Player.Character.SetIntoVehicle(vehicle, VehicleSeat.Driver);
                        GTA.UI.Notification.PostTicker($"~b~Spawned: ~g~{pending.ModelName}", false);
                    }
                    _pendingSpawns.RemoveAt(i);
                }
                else if (++pending.Ticks >= VehicleLoadTimeoutTicks)
                {
                    GTA.UI.Notification.PostTicker($"~r~Failed to load vehicle: ~s~{pending.ModelName}", false);
                    _pendingSpawns.RemoveAt(i);
                }
            }
        }

        public void Tick()
        {
            PlayerTrainer.Tick();
            VehicleTrainer.ApplyVehicleGodMode();

            if (PlayerTrainer.InfiniteStaminaOn)
                Function.Call(Hash.RESET_PLAYER_STAMINA, Game.Player.Handle);
            if (PlayerTrainer.SuperJumpOn)
                Function.Call(Hash.SET_SUPER_JUMP_THIS_FRAME, Game.Player.Handle);
            if (WeaponTrainer.InfiniteAmmoEnabled)
                WeaponTrainer.SetInfiniteAmmo(true);

            ProcessPendingVehicleSpawns();
        }

        public void Toggle()
        {
            bool wasVisible = RootMenu.Visible;
            MenuManager.Instance.Pool.HideAll();
            RootMenu.Visible = !wasVisible;
        }

        private sealed class PendingVehicleSpawn
        {
            public Model Model;
            public string ModelName;
            public Vector3 Position;
            public float Heading;
            public int Ticks;
        }
    }
}
