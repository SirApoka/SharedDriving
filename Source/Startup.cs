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
    [StaticConstructorOnStartup]
    public static class Startup
    {
        internal const string Tag = "[Shared Driving]";

        static Startup()
        {
            var harmony = new Harmony("SirApoka.SharedDriving");
            var active = new List<string>();

            TryPatch(harmony, active, "night rest override",
                AccessTools.Method(typeof(VehicleCaravanPathingHelper), nameof(VehicleCaravanPathingHelper.ShouldRestAt),
                    new[] { typeof(VehicleCaravan), typeof(PlanetTile).MakeByRefType() }),
                postfix: nameof(Patches.ShouldRestAt_Postfix));

            TryPatch(harmony, active, "non-stop arrival estimate",
                AccessTools.Method(typeof(VehicleCaravanPathingHelper), nameof(VehicleCaravanPathingHelper.EstimatedTicksToArrive),
                    new[] { typeof(VehicleCaravan), typeof(bool) }),
                prefix: nameof(Patches.EstimatedTicksToArrive_Prefix));

            TryPatch(harmony, active, "caravan toggle",
                AccessTools.Method(typeof(VehicleCaravan), nameof(VehicleCaravan.GetGizmos)),
                postfix: nameof(Patches.GetGizmos_Postfix));

            TryPatch(harmony, active, "inspect line",
                AccessTools.Method(typeof(VehicleCaravan), nameof(VehicleCaravan.GetInspectString)),
                postfix: nameof(Patches.GetInspectString_Postfix));

            var seatPatches = new List<string>();
            var dialog = typeof(Dialog_AssignSeats);
            var overlay = typeof(SeatDialogOverlay);
            TryPatch(harmony, seatPatches, "context",
                AccessTools.Method(dialog, "DrawVehicleMenu"), overlay,
                prefix: nameof(SeatDialogOverlay.DrawVehicleMenu_Prefix),
                finalizer: nameof(SeatDialogOverlay.DrawVehicleMenu_Finalizer));
            TryPatch(harmony, seatPatches, "pawn list",
                AccessTools.Method(dialog, "DrawPawns"), overlay,
                prefix: nameof(SeatDialogOverlay.DrawPawns_Prefix),
                finalizer: nameof(SeatDialogOverlay.Section_Finalizer));
            TryPatch(harmony, seatPatches, "assigned list",
                AccessTools.Method(dialog, "DrawAssignees"), overlay,
                prefix: nameof(SeatDialogOverlay.DrawAssignees_Prefix),
                postfix: nameof(SeatDialogOverlay.DrawAssignees_Postfix),
                finalizer: nameof(SeatDialogOverlay.Section_Finalizer));
            TryPatch(harmony, seatPatches, "pawn rows",
                AccessTools.Method(dialog, "DrawPawnRow"), overlay,
                postfix: nameof(SeatDialogOverlay.DrawPawnRow_Postfix));
            TryPatch(harmony, seatPatches, "seat headers",
                AccessTools.Method(typeof(SmashTools.UIElements), nameof(SmashTools.UIElements.LabelUnderlined),
                    new[] { typeof(Rect), typeof(string), typeof(string), typeof(Color), typeof(Color), typeof(Color) }), overlay,
                postfix: nameof(SeatDialogOverlay.LabelUnderlined_Postfix));
            if (seatPatches.Count > 0)
                active.Add($"seat dialog ({string.Join(", ", seatPatches)})");

            var tabPatches = new List<string>();
            var tab = typeof(VehicleTabHelper_Passenger);
            TryPatch(harmony, tabPatches, "pawn rows",
                AccessTools.Method(tab, nameof(VehicleTabHelper_Passenger.DoRow)), typeof(PawnsTabOverlay),
                postfix: nameof(PawnsTabOverlay.DoRow_Postfix));
            TryPatch(harmony, tabPatches, "seat headers",
                AccessTools.Method(tab, nameof(VehicleTabHelper_Passenger.ListPawns)), typeof(PawnsTabOverlay),
                prefix: nameof(PawnsTabOverlay.ListPawns_Prefix), postfix: nameof(PawnsTabOverlay.ListPawns_Postfix));
            TryPatch(harmony, tabPatches, "seat change",
                AccessTools.Method(tab, "TransferPawn"), typeof(PawnsTabOverlay),
                postfix: nameof(PawnsTabOverlay.TransferPawn_Postfix));
            if (tabPatches.Count > 0)
                active.Add($"pawns tab ({string.Join(", ", tabPatches)})");

            TryPatch(harmony, active, "empty cargo area",
                AccessTools.Method(typeof(VehicleTabHelper_Passenger), nameof(VehicleTabHelper_Passenger.DrawPassengersFor)),
                typeof(CargoDropArea), postfix: nameof(CargoDropArea.DrawPassengersFor_Postfix));

            TryPatch(harmony, active, "cargo list after load",
                AccessTools.Method(typeof(VehiclePawn), "PostLoad"),
                typeof(CargoCacheFix), postfix: nameof(CargoCacheFix.PostLoad_Postfix));

            if (CargoRest.Init())
            {
                TryPatch(harmony, active, "cargo rest while moving",
                    AccessTools.Method(typeof(Caravan_NeedsTracker), "TrySatisfyRestNeed", new[] { typeof(Pawn), typeof(RimWorld.Need_Rest), typeof(int) }),
                    typeof(CargoRest), postfix: nameof(CargoRest.TrySatisfyRestNeed_Postfix));
            }

            var peUtility = AccessTools.TypeByName("ProgressionEducation.ProficiencyUtility");
            if (peUtility != null)
            {
                TryPatch(harmony, active, "Progression: Education drop fix",
                    AccessTools.Method(peUtility, "CheckVehicleOperation", new[] { typeof(Pawn), typeof(object) }),
                    typeof(ProgressionEducationGuard), prefix: nameof(ProgressionEducationGuard.CheckVehicleOperation_Prefix));
            }

            Log.Message($"{Tag} Active: {string.Join(", ", active)}.");
        }

        private static void TryPatch(Harmony harmony, List<string> active, string label,
            System.Reflection.MethodInfo target, string prefix = null, string postfix = null) =>
            TryPatch(harmony, active, label, target, typeof(Patches), prefix, postfix);

        private static void TryPatch(Harmony harmony, List<string> active, string label,
            System.Reflection.MethodInfo target, Type host, string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                if (target == null)
                {
                    Log.Error($"{Tag} Target for '{label}' not found. That part is disabled.");
                    return;
                }
                // Run the patch class's static initializers now (they resolve VF fields by name), so a
                // renamed field disables this part here instead of throwing on every call of the patch.
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(host.TypeHandle);
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(host, prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(host, postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(host, finalizer) : null);
                active.Add(label);
            }
            catch (Exception e)
            {
                Log.Error($"{Tag} Failed to patch '{label}'. That part is disabled. {e}");
            }
        }
    }

    public static class Patches
    {
        private static bool restDisabled;
        private static bool etaDisabled;
        private static bool gizmoDisabled;
        private static bool inspectDisabled;

        // VF's Caravan.NightResting prefix and the pather both end up here.
        public static void ShouldRestAt_Postfix(VehicleCaravan __0, ref bool __result)
        {
            if (!__result || restDisabled)
                return;
            try
            {
                if (ShiftController.DrivesThroughNight(__0))
                    __result = false;
            }
            catch (Exception e)
            {
                restDisabled = true;
                Log.Error($"{Startup.Tag} Error in night rest override. Disabled for this session. {e}");
            }
        }

        public static bool EstimatedTicksToArrive_Prefix(VehicleCaravan __0, bool __1, ref int __result)
        {
            if (etaDisabled)
                return true;
            try
            {
                if (!ShiftController.WouldDriveThroughNight(__0))
                    return true;
                __result = DirectEta.Compute(__0, __1);
                return false;
            }
            catch (Exception e)
            {
                etaDisabled = true;
                Log.Error($"{Startup.Tag} Error computing the non-stop arrival estimate. Disabled for this session. {e}");
                return true;
            }
        }

        public static IEnumerable<Gizmo> GetGizmos_Postfix(IEnumerable<Gizmo> __result, VehicleCaravan __instance)
        {
            foreach (var g in __result)
                yield return g;

            if (gizmoDisabled || !__instance.IsPlayerControlled)
                yield break;

            Gizmo ours = null;
            try
            {
                ours = SharedDrivingGizmo.Make(__instance);
            }
            catch (Exception e)
            {
                gizmoDisabled = true;
                Log.Error($"{Startup.Tag} Error building the toggle. Disabled for this session. {e}");
            }
            if (ours != null)
                yield return ours;
        }

        public static void GetInspectString_Postfix(VehicleCaravan __instance, ref string __result)
        {
            if (inspectDisabled)
                return;
            try
            {
                var line = SharedDrivingGizmo.InspectLine(__instance);
                if (!line.NullOrEmpty())
                    __result = __result.NullOrEmpty() ? line : __result.TrimEnd() + "\n" + line;
            }
            catch (Exception e)
            {
                inspectDisabled = true;
                Log.Error($"{Startup.Tag} Error adding the inspect line. Disabled for this session. {e}");
            }
        }
    }

    // Travel time without night stops: the remaining tile costs summed with VF's own cost function.
    internal static class DirectEta
    {
        private const int CacheDuration = 100;
        private static VehicleCaravan cachedCaravan;
        private static PlanetTile cachedDest = PlanetTile.Invalid;
        private static int cachedTick = -1;
        private static int cachedResult;

        public static int Compute(VehicleCaravan caravan, bool allowCaching)
        {
            var pather = caravan.vehiclePather;
            if (!caravan.Spawned || !pather.Moving || pather.curPath == null)
                return 0;

            var dest = pather.Destination;
            int now = Find.TickManager.TicksGame;
            if (allowCaching && caravan == cachedCaravan && dest == cachedDest && now - cachedTick < CacheDuration)
                return cachedResult;

            var defs = caravan.VehiclesListForReading.Select(v => v.VehicleDef).ToList();
            var path = pather.curPath;
            int ticksPerMove = caravan.TicksPerMove;
            int ticksAbs = Find.TickManager.TicksAbs;

            int total = Mathf.CeilToInt(pather.nextTileCostLeft);
            int nodes = path.NodesLeftCount;
            if (nodes > 0)
            {
                PlanetTile prev = path.Peek(0);
                for (int i = 1; i < nodes && prev != dest; i++)
                {
                    PlanetTile next = path.Peek(i);
                    var data = new TicksPerMoveData
                    {
                        ticksPerMove = ticksPerMove,
                        tile = prev,
                        nextTile = next,
                        ticksAbs = ticksAbs + total,
                    };
                    total += VehicleCaravan_PathFollower.CostToMove(defs, in data);
                    prev = next;
                }
            }

            if (allowCaching)
            {
                cachedCaravan = caravan;
                cachedDest = dest;
                cachedTick = now;
                cachedResult = total;
            }
            return total;
        }
    }
}
