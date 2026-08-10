using System;
using System.Collections.Generic;
using GTAZ.Core;
using GTAZ.Events;
using GTAZ.Extensions;
using GTAZ.Systems;
using GTAZ.UI;
using GTAZ.UI.Trainer;

namespace GTAZ
{
    public class ModuleManager
    {
        private readonly List<Action> _tickActions = [];
        private readonly List<Action> _abortActions = [];

        public WorldSystem World { get; private set; }
        public ZombieSystem Zombies { get; private set; }
        public SurvivorSystem Survivors { get; private set; }
        public HordeSystem Horde { get; private set; }
        public FactionSystem Factions { get; private set; }
        public PersistenceManager Persistence { get; private set; }
        public MenuManager Menu { get; private set; }
        public TrainerManager Trainer { get; private set; }

        public void Initialize()
        {
            Logger.Info("Initializing GTAZ modules...");

            World = new();
            Zombies = new();
            Survivors = SurvivorSystem.Instance;
            Horde = new HordeSystem(Survivors, Zombies);
            Factions = FactionSystem.Instance;
            Persistence = PersistenceManager.Instance;
            Menu = MenuManager.Instance;
            Menu.Initialize(World, Zombies, Horde, Survivors, Factions, Persistence);

            Trainer = new TrainerManager();
            Trainer.Initialize(Menu);

            PedExtensions.SetupZombieRelationships();
            PedExtensions.SetupSurvivorRelationships();
            Factions.Definitions.SetupFactionRelationships();

            // One-shot on the first tick (player is guaranteed spawned by then): the
            // Setup*Relationships calls above run in the ModCore constructor, which can execute
            // before the game is fully ready, silently DROPPING the relationship natives - the
            // player-facing stances (Companion/Hate) never stick. Re-asserting against the live
            // PLAYER group fixes the "green-blip survivors shoot me" bug (DOCS/08-Lessons-Learned.md #33).
            // Idempotent when the ctor bind already stuck.
            bool playerRelationshipsDone = false;
            _tickActions.Add(() =>
            {
                if (playerRelationshipsDone)
                    return;
                playerRelationshipsDone = true;
                PedExtensions.EnsurePlayerFacingRelationships();
            });

            _tickActions.Add(() => PedExtensions.EnsureClipsetsLoaded());
            _tickActions.Add(() => PedExtensions.EnsureAttackAnimLoaded());
            _tickActions.Add(() => PedExtensions.EnsureFacialDictsLoaded());
            _tickActions.Add(() => GameplaySettings.Tick());
            _tickActions.Add(() => World.OnTick());
            _tickActions.Add(() => Zombies.OnTick());
            _tickActions.Add(() => Horde.OnTick());
            _tickActions.Add(() => Survivors.OnTick());
            _tickActions.Add(() => Factions.OnTick());

            // Resume the last session: on the first tick (player spawned, zones built), load
            // the freshest save - the autosave first, falling back to the manual save. No-op
            // on a first-ever run. Runs after Factions.OnTick so restored zone owners are safe.
            bool startupLoadDone = false;
            _tickActions.Add(() =>
            {
                if (startupLoadDone)
                    return;
                startupLoadDone = true;
                if (Persistence.LoadMostRecentAndApply())
                    Logger.Info("GTAZ resumed from the last save");
            });

            _tickActions.Add(() => Menu.Process());
            _tickActions.Add(() => Menu.Tick());
            _tickActions.Add(() => Trainer.Tick());
            _tickActions.Add(() => Persistence.TickAutoSave());

            _abortActions.Add(() =>
            {
                World.OnAborted();
                Zombies.OnAborted();
                Horde.OnAborted();
                Survivors.ClearAllSurvivors();
                Factions.ClearAllFactions();
                Persistence.Save("abort_save.ini");
                GameEvents.RaiseModAborted();
                Logger.Info("GTAZ aborted - emergency save written");
            });

            GameEvents.RaiseGameLoaded();
            Logger.Info("GTAZ modules initialized");
        }

        public void Tick()
        {
            foreach (var action in _tickActions)
                action();
        }

        public void Abort()
        {
            foreach (var action in _abortActions)
                action();
        }
    }
}
