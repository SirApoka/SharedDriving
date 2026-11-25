using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Vehicles;
using Vehicles.World;
using Verse;

namespace SharedDriving
{
    // In a vehicle caravan's Pawns tab, VF only draws a vehicle's "Cargo" drop area when that cargo
    // already holds a pawn, so an animal walking with the caravan cannot be dragged into an empty cargo.
    // This draws the same area (with VF's own ListPawns) when the cargo is empty and an animal is outside.
    internal static class CargoDropArea
    {
        private static readonly AccessTools.FieldRef<bool> OverDropSpot =
            AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(typeof(VehicleTabHelper_Passenger), "overDropSpot"));

        private static readonly List<Pawn> Empty = new List<Pawn>();
        private static bool disabled;

        public static void DrawPassengersFor_Postfix(ref float __0, Rect __1, Vector2 __2, VehiclePawn __3, ref Pawn __4)
        {
            if (disabled || __3 == null)
                return;
            try
            {
                if (__3.AllInventoryPawns.Count > 0)
                    return;
                if (!(__3.GetCaravan() is VehicleCaravan caravan))
                    return;
                if (!caravan.DismountedPawnsListForReading.Any(p => p != null && !p.Dead && p.ShouldAlwaysTransferToVehiclesCargo()))
                    return;

                Empty.Clear();
                if (VehicleTabHelper_Passenger.ListPawns(ref __0, __1, __2, __3.inventory, "VF_Caravan_Cargo".Translate(), Empty, ref __4))
                    OverDropSpot() = true;
            }
            catch (Exception e)
            {
                disabled = true;
                Log.Error($"{Startup.Tag} Error drawing the empty cargo area. Disabled for this session. {e}");
            }
        }
    }
}
