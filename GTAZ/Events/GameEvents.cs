using System;

namespace GTAZ.Events
{
    public static class GameEvents
    {
        public static event Action OnGameLoaded;
        public static event Action OnGameSaved;
        public static event Action OnModAborted;

        public static void RaiseGameLoaded() => OnGameLoaded?.Invoke();
        public static void RaiseGameSaved() => OnGameSaved?.Invoke();
        public static void RaiseModAborted() => OnModAborted?.Invoke();
    }
}
