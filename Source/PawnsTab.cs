using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Vehicles;
using Vehicles.World;
using Verse;

namespace SharedDriving
{
    // The caravan "Pawns" tab (VF's VehicleTabHelper_Passenger) is where seats are rearranged after the
    // caravan has left, by dragging pawns. It shows no seat requirements. This adds the same hints as the
    // seat assignment dialog (decided 2026-11-24 instead of a separate dialog):
    //  - per pawn in a vehicle seat: can drive / can man the turrets of that vehicle (with the reasons
    //    when not), and a sun / moon icon for the Shared driving day / night driver;
    //  - per seat header: the seat's requirements and who in the caravan can take it.
    [StaticConstructorOnStartup]
    internal static class PawnsTabOverlay
    {
        private const float IconSize = 15f;
        private const float IconGap = 2f;
        private const float HeaderHeight = 25f;
        private const float RowHeight = 50f;

        private static readonly Texture2D DriveTex = Tex("UI/SharedDriving/Drive");
        private static readonly Texture2D TurretTex = Tex("UI/SharedDriving/Turret");
        private static readonly Texture2D CoDriverTex = Tex("UI/SharedDriving/CoDriver");
        private static readonly Texture2D DayDriverTex = Tex("UI/SharedDriving/DayDriver");
        private static readonly Color Capable = new Color(0.55f, 0.9f, 0.55f);
        private static readonly Color Incapable = new Color(0.95f, 0.4f, 0.4f);

        private static bool disabled;

        // Day and night driver per vehicle, recomputed once per frame (OnGUI runs several times per frame).
        private static int rolesFrame = -1;
        private static readonly Dictionary<VehiclePawn, (Pawn day, Pawn night)> roles = new Dictionary<VehiclePawn, (Pawn, Pawn)>();

        private static Texture2D Tex(string path) =>
            ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;

        // VehicleTabHelper_Passenger.DoRow(float curY, Rect viewRect, Vector2 scrollPos, Pawn pawn, ref Pawn moreDetailsForPawn, bool highlight)
        public static void DoRow_Postfix(float __0, Vector2 __2, Pawn __3)
        {
            float curY = __0;
            var pawn = __3;
            if (disabled || pawn == null || pawn.Dead || !pawn.RaceProps.ToolUser)
                return;
            // Same culling as VF; rows being dragged follow the mouse, skip those.
            if (curY <= __2.y - RowHeight || curY >= __2.y + 450f)
                return;
            if (!(pawn.ParentHolder is VehicleRoleHandler seat) || seat.vehicle == null)
                return;
            try
            {
                var vehicle = seat.vehicle;
                var movement = vehicle.handlers.Where(SeatRequirements.HasMovement).ToList();
                var turrets = vehicle.handlers.Where(h => SeatRequirements.HasTurret(h) && !SeatRequirements.HasMovement(h)).ToList();

                var icons = new List<(Texture2D tex, Color color, string tip)>();
                if (movement.Count > 0 && !ShiftController.DrivesItself(vehicle))
                    icons.Add(CapabilityIcon(DriveTex, pawn, vehicle, movement, "SD_TipCanDrive", "SD_TipCannotDrive"));
                if (turrets.Count > 0)
                    icons.Add(CapabilityIcon(TurretTex, pawn, vehicle, turrets, "SD_TipCanGun", "SD_TipCannotGun"));
                var (day, night) = RolesOf(vehicle);
                if (day == pawn)
                    icons.Add((DayDriverTex, Color.white, "SD_TipDayDriver".Translate(pawn.LabelShortCap, vehicle.LabelShortCap)));
                if (night == pawn)
                    icons.Add((CoDriverTex, Color.white, "SD_TipCoDriver".Translate(pawn.LabelShortCap, vehicle.LabelShortCap)));

                // Above the pawn's name (VF draws the portrait at x 4-31 and the name at y 16-34).
                float x = 35f;
                foreach (var (tex, color, tip) in icons)
                {
                    var r = new Rect(x, curY + 1f, IconSize, IconSize);
                    GUI.color = color;
                    GUI.DrawTexture(r, tex);
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(r, tip);
                    x += IconSize + IconGap;
                }
            }
            catch (Exception e)
            {
                Fail("pawn row", e);
            }
        }

        // VehicleTabHelper_Passenger.ListPawns(ref float curY, Rect viewRect, Vector2 scrollPos, IThingHolder holder, ...):
        // remember where the seat header starts, then add its tooltip.
        public static void ListPawns_Prefix(ref float __0, out float __state) => __state = __0;

        public static void ListPawns_Postfix(float __state, Rect __1, IThingHolder __3)
        {
            if (disabled || !(__3 is VehicleRoleHandler handler) || handler.vehicle == null)
                return;
            try
            {
                var rect = new Rect(0f, __state, __1.width, HeaderHeight);
                if (!Mouse.IsOver(rect))
                    return;
                var caravan = handler.vehicle.GetVehicleCaravan();
                var h = handler;
                TooltipHandler.TipRegion(rect, () =>
                {
                    var eligible = caravan == null
                        ? new List<Pawn>()
                        : caravan.AllPawnsAndVehiclePassengers.Where(p => p != null && !(p is VehiclePawn) && h.CanOperateRole(p)).ToList();
                    string who = eligible.Count > 0
                        ? "SD_SeatEligible".Translate(eligible.Select(p => p.LabelShortCap).ToCommaList())
                        : "SD_SeatNobodyEligible".Translate();
                    return h.role.label.CapitalizeFirst().Colorize(ColoredText.TipSectionTitleColor)
                        + "\n" + SeatRequirements.Describe(h) + "\n\n" + who;
                }, 0x5D100000 + handler.vehicle.handlers.IndexOf(handler));
            }
            catch (Exception e)
            {
                Fail("seat header", e);
            }
        }

        private static (Texture2D, Color, string) CapabilityIcon(Texture2D tex, Pawn pawn, VehiclePawn vehicle,
            List<VehicleRoleHandler> seats, string canKey, string cannotKey)
        {
            if (seats.Any(h => h.CanOperateRole(pawn)))
                return (tex, Capable, canKey.Translate(pawn.LabelShortCap, vehicle.LabelShortCap));
            var reasons = SeatRequirements.WhyNot(pawn, seats[0]);
            return (tex, Incapable, cannotKey.Translate(pawn.LabelShortCap, vehicle.LabelShortCap)
                + "\n" + string.Join("\n", reasons.Select(r => "  - " + r)));
        }

        // Shared driving roles, only when it is on for the caravan. Night shift: from the swap record (or, when
        // nobody can take over at 6:00, just the pawn at the wheel). Day: whoever is at the wheel, and the
        // night driver planned for 18:00.
        private static (Pawn day, Pawn night) RolesOf(VehiclePawn vehicle)
        {
            if (rolesFrame != Time.frameCount)
            {
                rolesFrame = Time.frameCount;
                roles.Clear();
            }
            if (roles.TryGetValue(vehicle, out var cached))
                return cached;

            (Pawn, Pawn) result = (null, null);
            var caravan = vehicle.GetVehicleCaravan();
            var state = SharedDrivingTracker.Instance?.Get(caravan);
            if (caravan != null && state != null && state.enabled && !ShiftController.DrivesItself(vehicle))
            {
                var atWheel = vehicle.handlers.FirstOrDefault(ShiftController.IsMovementSeat)?
                    .thingOwner.InnerListForReading.FirstOrDefault();
                if (state.nightActive)
                {
                    var r = state.swaps.FirstOrDefault(x => x.vehicle == vehicle);
                    result = r != null ? (r.driver, r.coDriver) : (null, atWheel);
                }
                else
                {
                    var r = ShiftController.Plan(caravan, state, includeOutside: false).swaps.FirstOrDefault(x => x.vehicle == vehicle);
                    result = (atWheel, r?.coDriver);
                }
            }
            roles[vehicle] = result;
            return result;
        }

        // VehicleTabHelper_Passenger.TransferPawn(Pawn pawn, Pawn hoveredPawn, IThingHolder targetHolder): a drag finished.
        public static void TransferPawn_Postfix(Pawn __0, IThingHolder __2)
        {
            if (disabled)
                return;
            try
            {
                var caravan = __0?.GetVehicleCaravan() ?? (__2 as VehicleRoleHandler)?.vehicle?.GetVehicleCaravan();
                if (caravan != null)
                    ShiftController.OnSeatsChanged(caravan);
            }
            catch (Exception e)
            {
                Fail("seat change", e);
            }
        }

        private static void Fail(string part, Exception e)
        {
            disabled = true;
            Log.Error($"{Startup.Tag} Error drawing the Pawns tab {part}. Pawns tab hints disabled for this session. {e}");
        }
    }
}
