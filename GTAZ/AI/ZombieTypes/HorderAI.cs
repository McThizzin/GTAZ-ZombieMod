using GTA;
using GTA.Math;
using GTAZ.Core;

namespace GTAZ.AI.ZombieTypes
{
    // The horde's center point: the big armored g_m_m_zombie_02 that the horde forms around.
    // Faster than a walker but below a runner (MoveSpeed 1.3) so the horde groups to it without
    // dragging runners down. Spawned only by HordeSystem (one per horde) - the model is removed
    // from the ambient runner pool so it never appears as an ordinary zombie. The Horder is
    // undamageable while its horde lives (HordeAlive set by HordeSystem); the immunity is
    // re-asserted every tick because the engine clears entity proof flags on re-creation
    // (DOCS/08-Lessons-Learned.md lesson 35).
    public class HorderAI : ZombieAI
    {
        // True while any member of this Horder's horde is alive (set by HordeSystem each tick).
        // While true the Horder is immune; when the last member dies HordeSystem flips this and
        // Update() clears the proofs, making it killable.
        public bool HordeAlive { get; set; } = true;

        public HorderAI(Ped ped, Vector3 spawnOrigin) : base(ped, spawnOrigin)
        {
            Type = ZombieType.Horder;
            VisionRange = 55f;
            HearingRange = 35f;
            AttackRange = 2.8f;
            Damage = ModConfig.Current.ZombieDamage;
            MaxHealth = ModConfig.Current.ZombieHealth;
            MoveSpeed = 1.3f;
            WanderRadius = 20f;
            _updateInterval = 5;

            // The first proof application can't wait for the first tick - between
            // materialization and the first Update() the Horder would sit completely
            // un-armed (lesson 35). Apply now when the horde is alive; Update() re-asserts
            // or clears from HordeAlive every tick.
            if (HordeAlive && ModConfig.Current.HorderInvulnerableWithHorde)
                ApplyImmunity(true);
        }

        private void ApplyImmunity(bool immune)
        {
            Ped.IsInvincible = immune;
            Ped.IsBulletProof = immune;
            Ped.IsExplosionProof = immune;
            Ped.IsFireProof = immune;
            Ped.IsMeleeProof = immune;
        }

        public override void Update()
        {
            base.Update();

            if (Ped?.Exists() is not true || Ped.IsDead)
                return;

            bool immune = HordeAlive && ModConfig.Current.HorderInvulnerableWithHorde;

            // Re-assert every tick while immune AND clear when the horde dies - a once-set flag
            // silently stops working when the engine re-creates the entity, and equally a flag
            // left set would keep the Horder invincible after its horde is gone (lesson 35).
            ApplyImmunity(immune);
        }
    }
}
