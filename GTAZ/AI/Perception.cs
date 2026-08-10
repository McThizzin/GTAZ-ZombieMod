using GTA;
using GTA.Math;
using GTA.Native;


namespace GTAZ.AI
{
    public static class Perception
    {
        public static bool CanDetectTarget(Ped source, Entity target, float range)
        {
            var dist = (source.Position - target.Position).Length();
            if (dist > range)
                return false;
            return Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, source, target, 17);
        }

        public static bool CanSeeTarget(Ped source, Entity target, float visionRange, float fieldOfViewDeg = 120f)
        {
            var sourcePos = source.Position;
            var targetPos = target.Position;
            var offset = targetPos - sourcePos;

            if (offset.Length() > visionRange)
                return false;

            var forward = source.ForwardVector;
            var angle = Vector3.Dot(forward, offset.Normalized);
            var cosFov = (float)System.Math.Cos(fieldOfViewDeg * 0.5f * System.Math.PI / 180f);

            if (angle < cosFov)
                return false;

            return Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, source, target, 17);
        }

        public static bool CanHearTarget(Ped source, Entity target, float hearingRange)
        {
            var dist = (source.Position - target.Position).Length();
            return dist <= hearingRange;
        }

        public static bool CanHearNoise(Ped source, Vector3 noiseOrigin, float noiseRange)
        {
            var dist = (source.Position - noiseOrigin).Length();
            return dist <= noiseRange;
        }
    }
}
