using System;
using System.Windows.Forms;
using GTA;
using GTAZ.Core;
using GTAZ.Events;

namespace GTAZ
{
    public class ModCore : Script
    {
        private readonly ModuleManager _modules;
        private readonly Keys _menuKey;
        private readonly Keys _trainerKey;
        private readonly Keys _recruitKey;

        public ModCore()
        {
            Logger.Initialize();
            Logger.Info("GTAZ starting...");

            ModConfig.Load();

            _menuKey = ModConfig.Current.OpenMenuKey;
            _trainerKey = ModConfig.Current.OpenTrainerKey;
            _recruitKey = ModConfig.Current.RecruitKey;

            _modules = new();
            _modules.Initialize();

            Tick += OnTick;
            KeyUp += OnKeyUp;
            Aborted += OnAborted;

            RegisterScriptEventHandlers();

            Core.Logger.Info("GTAZ started successfully");
            GTA.UI.Notification.PostTicker($"~b~GTAZ loaded. Menu: ~g~{_menuKey}", false);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                _modules.Tick();
            }
            catch (Exception ex)
            {
                Logger.Error($"Unhandled exception in Tick: {ex}");
            }
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == _menuKey && e.Modifiers == Keys.None)
            {
                _modules.Menu.ToggleMainMenu();
            }
            else if (e.KeyCode == _trainerKey && e.Modifiers == Keys.None)
            {
                _modules.Trainer.Toggle();
            }
            else if (e.KeyCode == _recruitKey && e.Modifiers == Keys.None)
            {
                _modules.Factions.TryRecruitAtCrosshair();
            }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            ModConfig.Current.Save();
            _modules.Abort();
        }

        private void RegisterScriptEventHandlers()
        {
            GameEvents.OnGameSaved += () => GTA.UI.Notification.PostTicker("~g~Game saved.", false);
        }
    }
}
