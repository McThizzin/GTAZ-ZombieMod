using System;
using GTA;

namespace GTAZ.Events
{
    public static class ZombieEvents
    {
        public static event Action<Ped> OnZombieSpawned;
        public static event Action<Ped> OnZombieDied;

        public static void RaiseZombieSpawned(Ped ped) => OnZombieSpawned?.Invoke(ped);
        public static void RaiseZombieDied(Ped ped) => OnZombieDied?.Invoke(ped);
    }
}
