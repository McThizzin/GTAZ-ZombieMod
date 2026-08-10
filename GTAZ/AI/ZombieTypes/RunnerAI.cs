using GTA;
using GTA.Math;
using GTAZ.Core;

namespace GTAZ.AI.ZombieTypes
{
    public class RunnerAI : ZombieAI
    {
        public RunnerAI(Ped ped, Vector3 spawnOrigin) : base(ped, spawnOrigin)
        {
            Type = ZombieType.Runner;
            VisionRange = 60f;
            HearingRange = 40f;
            AttackRange = 2.5f;
            Damage = ModConfig.Current.ZombieDamage;
            MaxHealth = ModConfig.Current.ZombieHealth;
            MoveSpeed = 1.8f;
            WanderRadius = 40f;
            _updateInterval = 3;
        }
    }
}
