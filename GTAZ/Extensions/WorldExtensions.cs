using GTA;
using GTA.Native;


#pragma warning disable RCS1224 // Make method an extension method — methods use static Function.Call internally

namespace GTAZ.Extensions
{
    public static class WorldExtensions
    {
        public static void SetPedDensityThisFrame(float multiplier)
        {
            Function.Call(Hash.SET_PED_DENSITY_MULTIPLIER_THIS_FRAME, multiplier);
        }

        public static void SetScenarioPedDensityThisFrame(float multiplier)
        {
            Function.Call(Hash.SET_SCENARIO_PED_DENSITY_MULTIPLIER_THIS_FRAME, multiplier);
        }

        public static void SetVehicleDensityThisFrame(float multiplier)
        {
            Function.Call(Hash.SET_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, multiplier);
        }

        public static void SetRandomVehicleDensityThisFrame(float multiplier)
        {
            Function.Call(Hash.SET_RANDOM_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, multiplier);
        }

        public static void SetParkedVehicleDensityThisFrame(float multiplier)
        {
            Function.Call(Hash.SET_PARKED_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, multiplier);
        }

        public static void SetAllDensitiesZero()
        {
            SetPedDensityThisFrame(0f);
            SetScenarioPedDensityThisFrame(0f);
            SetVehicleDensityThisFrame(0f);
            SetRandomVehicleDensityThisFrame(0f);
            SetParkedVehicleDensityThisFrame(0f);
        }
    }
}
