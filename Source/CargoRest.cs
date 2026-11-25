using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Vehicles;
using Vehicles.World;
using Verse;

namespace SharedDriving
{
    // You Drive, I Sleep only rests pawns in vehicle seats while the caravan moves. Pawns in a vehicle's
    // cargo (animals, colony mechs) never rest on the move, and with Shared driving the caravan no longer
    // stops at night for them to catch up. Give them the same rest a passenger gets: the vehicle's
    // BedRestEffectiveness. While stopped, vanilla / the YDIS + CV Compatibility Patch handle them.
    internal static class CargoRest
    {
        private static AccessTools.FieldRef<Need_Rest, int> lastRestTick;
        private static bool disabled;

        internal static bool Init()
        {
            try
            {
                lastRestTick = AccessTools.FieldRefAccess<Need_Rest, int>("lastRestTick");
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"{Startup.Tag} Need_Rest.lastRestTick not accessible. Cargo rest disabled. {e}");
                return false;
            }
        }

        // __0 = pawn, __1 = rest.
        public static void TrySatisfyRestNeed_Postfix(Caravan ___caravan, Pawn __0, Need_Rest __1)
        {
            if (disabled || lastRestTick == null)
                return;
            try
            {
                if (!(___caravan is VehicleCaravan caravan) || __0 == null || __1 == null || __0.Dead)
                    return;
                if (!caravan.vehiclePather.MovingNow)
                    return;
                if (!(__0.ParentHolder is Pawn_InventoryTracker inventory) || !(inventory.pawn is VehiclePawn vehicle))
                    return;
                if (lastRestTick(__1) == Find.TickManager.TicksGame)
                    return;

                __1.TickResting(vehicle.GetStatValue(StatDefOf.BedRestEffectiveness));
            }
            catch (Exception e)
            {
                disabled = true;
                Log.Error($"{Startup.Tag} Error giving rest to pawns in cargo. Disabled for this session. {e}");
            }
        }
    }
}
