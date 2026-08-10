using GTA;
using GTA.Math;
using GTAZ.Core;

namespace GTAZ.AI.ZombieTypes
{
    public class WalkerAI : ZombieAI
    {
        public WalkerAI(Ped ped, Vector3 spawnOrigin) : base(ped, spawnOrigin)
        {
            Type = ZombieType.Walker;
            VisionRange = 40f;
            HearingRange = 25f;
            AttackRange = 1.8f;
            Damage = ModConfig.Current.ZombieDamage;
            MaxHealth = ModConfig.Current.ZombieHealth;
            MoveSpeed = 0.8f;
            WanderRadius = 25f;
            _updateInterval = 6;
        }
    }
}
