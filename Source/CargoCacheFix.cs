using System;
using HarmonyLib;
using Vehicles;
using Verse;

namespace SharedDriving
{
    // VF caches the pawns in a vehicle's cargo (AllInventoryPawns) and only rebuilds it when the inventory
    // changes. The cache is not saved and VehiclePawn.PostLoad does not rebuild it, so after loading a save
    // the caravan's Pawns tab hides the Cargo section (and other VF code sees no cargo pawns).
    internal static class CargoCacheFix
    {
        // Resolved on first use inside the try, so a renamed VF method disables this fix instead of
        // throwing a TypeInitializationException out of VehiclePawn.PostLoad.
        private static Action<VehiclePawn> recache;

        private static bool disabled;

        public static void PostLoad_Postfix(VehiclePawn __instance)
        {
            if (disabled)
                return;
            try
            {
                recache ??= AccessTools.MethodDelegate<Action<VehiclePawn>>(
                    AccessTools.Method(typeof(VehiclePawn), "RecacheInventoryPawns"));
                recache(__instance);
            }
            catch (Exception e)
            {
                disabled = true;
                Log.Error($"{Startup.Tag} Error rebuilding the cargo pawn list after loading. Disabled for this session. {e}");
            }
        }
    }
}
