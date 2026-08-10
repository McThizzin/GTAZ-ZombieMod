using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GTAZ.Utils
{
    public static class SpawnUtils
    {
        private static readonly Random _rng = new();

        // GSC_FLAG_NOT_ISOLATED | GSC_FLAG_NOT_INTERIOR | GSC_FLAG_NOT_WATER
        public const int SafeCoordFlags = 2 | 4 | 8;

        // Rooftops are valid navmesh polygons, so GET_SAFE_COORD_FOR_PED happily returns them -
        // a horde half-spawned across Los Santos rooftops is useless to the player, and a bandit
        // on a roof is unreachable (DOCS/08-Lessons-Learned.md #33). GET_GROUND_Z_FOR_3D_COORD can't tell
        // them apart: on a rooftop it returns the roof surface itself, so a raised candidate
        // "matches" its own roof. GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD returns the terrain
        // height below (objects/buildings excluded), so a rooftop or inside-roof candidate sits
        // well above it and is rejected, while a hilltop or street matches.
        private const float GroundHeightTolerance = 4.5f;

        // GET_SAFE_COORD_FOR_PED is the same native the game's own population system uses to
        // place peds: it walks the navmesh to find a real, walkable spot and rejects
        // interiors/water/isolated polygons. World.GetGroundHeight only samples collision
        // directly below the point - far from the player that collision is often not streamed
        // in, which silently falls back to the caller's Z and drops peds mid-air or embedded
        // in geometry (see DOCS/08-Lessons-Learned.md lesson 4).
        public static Vector3 FindSafePosition(Vector3 around, float minDist, float maxDist)
        {
            Vector3 fallback = Vector3.Zero;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                double angle = _rng.NextDouble() * Math.PI * 2;
                float dist = minDist + (float)(_rng.NextDouble() * (maxDist - minDist));
                float x = around.X + (float)(Math.Cos(angle) * dist);
                float y = around.Y + (float)(Math.Sin(angle) * dist);

                // GET_SAFE_COORD_FOR_PED searches the navmesh around the Z it is given. Anchors
                // can have a stale/placeholder Z (territory centers are hardcoded Z=40 while the
                // real ground is tens of meters higher on hillsides or lower at beaches), which
                // made the search fail entirely -> "Defender wave FAILED to spawn ... no safe
                // positions" and "0 queued" raids. Resolve the terrain height at the candidate
                // first and start the search at the actual ground; if the area isn't streamed,
                // start well above the anchor so the search can descend onto the navmesh.
                float startZ = around.Z;
                var heightArg = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD, x, y, 1000f, heightArg, false))
                {
                    float terrainZ = heightArg.GetResult<float>();
                    if (terrainZ > -100f && terrainZ < 1000f)
                        startZ = terrainZ + 1f;
                }
                else if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, x, y, 1000f, heightArg, false))
                {
                    float terrainZ = heightArg.GetResult<float>();
                    if (terrainZ > -100f && terrainZ < 1000f)
                        startZ = terrainZ + 1f;
                }
                else
                {
                    startZ = around.Z + 100f; // terrain unstreamed: search from above the anchor
                }

                var outPos = new OutputArgument();
                if (!Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED, x, y, startZ, false, outPos, SafeCoordFlags))
                    continue;

                var safe = outPos.GetResult<Vector3>();
                if (fallback == Vector3.Zero)
                    fallback = safe;

                // Terrain-only ground probe: on a roof or inside building geometry the surface
                // directly below is an object, so safe.Z sits well above the returned terrain and
                // the candidate is rejected. On a hilltop or street they match. Only resolves
                // inside render distance, same as the plain ground probe.
                var terrainArg = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD,
                    safe.X, safe.Y, safe.Z + 2f, terrainArg, false))
                {
                    if (Math.Abs(safe.Z - terrainArg.GetResult<float>()) <= GroundHeightTolerance)
                        return safe;
                    continue; // rooftop / inside a building - try another point
                }

                // Terrain probe unstreamed (far spawns, dense city blocks): fall back to the plain
                // collision-surface query, and if that fails too, to the navmesh point rather than
                // blocking the spawn entirely - that still beats no horde at all.
                var groundArg = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, safe.X, safe.Y, safe.Z + 2f, groundArg, false))
                {
                    float groundZ = groundArg.GetResult<float>();
                    if (Math.Abs(safe.Z - groundZ) <= GroundHeightTolerance)
                        return safe;
                }
            }

            return fallback;
        }

        // Distance in the X/Y plane only. Territory rings, raid radii and kill radii are circles
        // drawn on the map, so 3D length against a center with a stale/placeholder Z (territory
        // centers are hardcoded Z=40 while real ground is far higher/lower) would inflate every
        // range check and make players fall outside zones they are visibly standing in.
        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        public static Ped CreateRandomPed(Vector3 position)
        {
            return World.CreateRandomPed(position);
        }

        public static Ped CreatePed(Model model, Vector3 position, float heading = 0f)
        {
            if (!model.IsLoaded)
                model.Request();
            return World.CreatePed(model, position, heading);
        }

        public static Vehicle CreateVehicle(Model model, Vector3 position, float heading = 0f)
        {
            if (!model.IsLoaded)
                model.Request();
            return World.CreateVehicle(model, position, heading);
        }

        public static bool IsOnScreenOrTooClose(Vector3 position, float minDistance)
        {
            var playerPos = Game.Player.Character.Position;
            return (position - playerPos).Length() < minDistance;
        }
    }
}
