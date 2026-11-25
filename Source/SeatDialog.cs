using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Vehicles;
using Vehicles.World;
using Verse;

namespace SharedDriving
{
    // Adds capability icons, seat requirements and the night co-driver choice to Vehicle Framework's
    // seat assignment dialog. VF draws every pawn row with DrawPawnRow and every seat header with
    // UIElements.LabelUnderlined, inside its scroll views; we draw right after them, using a context
    // set by prefixes on the dialog's own methods (always cleared by finalizers).
    [StaticConstructorOnStartup]
    internal static class SeatDialogOverlay
    {
        private const float IconSize = 20f;
        private const float IconGap = 2f;

        private static readonly Texture2D DriveTex = Tex("UI/SharedDriving/Drive");
        private static readonly Texture2D TurretTex = Tex("UI/SharedDriving/Turret");
        private static readonly Texture2D CoDriverTex = Tex("UI/SharedDriving/CoDriver");

        private static readonly Color Capable = new Color(0.55f, 0.9f, 0.55f);
        private static readonly Color Incapable = new Color(0.95f, 0.4f, 0.4f);

        private static readonly AccessTools.FieldRef<Dialog_AssignSeats, VehiclePawn> VehicleRef =
            AccessTools.FieldRefAccess<Dialog_AssignSeats, VehiclePawn>("vehicle");
        private static readonly AccessTools.FieldRef<Dialog_AssignSeats, VehicleAssignment> AssignerRef =
            AccessTools.FieldRefAccess<Dialog_AssignSeats, VehicleAssignment>("assigner");
        private static readonly AccessTools.FieldRef<Dialog_AssignSeats, List<Pawn>> PawnsRef =
            AccessTools.FieldRefAccess<Dialog_AssignSeats, List<Pawn>>("pawns");

        private enum Section { None, Pawns, Assigned }

        private static bool disabled;
        private static Dialog_AssignSeats dialog;
        private static VehiclePawn vehicle;
        private static Section section;
        private static int headerIndex;
        private static Pawn plannedCoDriver;
        private static List<Pawn> coDriverCandidates = new List<Pawn>();

        private static Texture2D Tex(string path) =>
            ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.WhiteTex;

        private static void Fail(string where, Exception e)
        {
            disabled = true;
            Clear();
            Log.Error($"{Startup.Tag} Error in the seat dialog overlay ({where}). Overlay disabled for this session. {e}");
        }

        private static void Clear()
        {
            dialog = null;
            vehicle = null;
            section = Section.None;
            plannedCoDriver = null;
            coDriverCandidates.Clear();
        }

        // --- context -------------------------------------------------------------------------------

        public static void DrawVehicleMenu_Prefix(Dialog_AssignSeats __instance)
        {
            Clear();
            if (disabled)
                return;
            try
            {
                dialog = __instance;
                vehicle = VehicleRef(__instance);
                PlanCoDriver();
            }
            catch (Exception e)
            {
                Fail("setup", e);
            }
        }

        public static Exception DrawVehicleMenu_Finalizer(Exception __exception)
        {
            Clear();
            return __exception;
        }

        public static void DrawPawns_Prefix()
        {
            if (vehicle != null)
                section = Section.Pawns;
        }

        public static void DrawAssignees_Prefix()
        {
            if (vehicle == null)
                return;
            section = Section.Assigned;
            headerIndex = -1;
        }

        public static Exception Section_Finalizer(Exception __exception)
        {
            section = Section.None;
            return __exception;
        }

        private static void PlanCoDriver()
        {
            plannedCoDriver = null;
            coDriverCandidates.Clear();
            if (vehicle == null || ShiftController.DrivesItself(vehicle))
                return;
            var seat = vehicle.handlers.FirstOrDefault(ShiftController.IsMovementSeat);
            if (seat == null)
                return;

            var assignments = AssignerRef(dialog)?.GetAssignments(vehicle);
            if (assignments == null)
                return;
            foreach (var a in assignments)
            {
                if (a?.pawn != null && ShiftController.IsPassengerSeat(a.handler) && ShiftController.CanTakeWheel(seat, a.pawn))
                    coDriverCandidates.Add(a.pawn);
            }
            ShiftController.ToDayArrangement(vehicle, seat, coDriverCandidates,
                assignments.Where(a => a?.pawn != null && a.handler == seat).Select(a => a.pawn).ToList());
            if (coDriverCandidates.Count == 0)
                return;

            var chosen = SharedDrivingTracker.Instance?.GetChosen(vehicle);
            plannedCoDriver = chosen != null && coDriverCandidates.Contains(chosen)
                ? chosen
                : coDriverCandidates.MaxBy(p => p.needs?.rest?.CurLevel ?? 1f);
        }

        // --- seat headers --------------------------------------------------------------------------

        public static void LabelUnderlined_Postfix(Rect __0)
        {
            var rect = __0;
            if (disabled || section != Section.Assigned || vehicle == null)
                return;
            try
            {
                headerIndex++;
                if (headerIndex < 0 || headerIndex >= vehicle.handlers.Count)
                    return;
                var handler = vehicle.handlers[headerIndex];

                var eligible = (PawnsRef(dialog) ?? new List<Pawn>()).Where(p => p != null && handler.CanOperateRole(p)).ToList();

                Texture2D icon = SeatRequirements.HasMovement(handler) ? DriveTex
                    : SeatRequirements.HasTurret(handler) ? TurretTex : null;
                if (icon != null)
                {
                    var iconRect = new Rect(rect.xMax - 16f - IconSize - 4f, rect.y + 2f, IconSize, IconSize);
                    GUI.color = eligible.Count > 0 ? Color.white : Incapable;
                    GUI.DrawTexture(iconRect, icon);
                    GUI.color = Color.white;
                }

                var h = handler;
                TooltipHandler.TipRegion(rect, () =>
                {
                    string who = eligible.Count > 0
                        ? "SD_SeatEligible".Translate(eligible.Select(p => p.LabelShortCap).ToCommaList())
                        : "SD_SeatNobodyEligible".Translate();
                    return h.role.label.CapitalizeFirst().Colorize(ColoredText.TipSectionTitleColor)
                        + "\n" + SeatRequirements.Describe(h) + "\n\n" + who;
                }, 0x5D000000 + headerIndex);
            }
            catch (Exception e)
            {
                Fail("seat header", e);
            }
        }

        // --- pawn rows -----------------------------------------------------------------------------

        public static void DrawPawnRow_Postfix(Rect __0, Pawn __1)
        {
            var rect = __0;
            var pawn = __1;
            if (disabled || section == Section.None || vehicle == null || pawn == null || !pawn.RaceProps.ToolUser)
                return;
            try
            {
                var movement = vehicle.handlers.Where(SeatRequirements.HasMovement).ToList();
                var turrets = vehicle.handlers.Where(h => SeatRequirements.HasTurret(h) && !SeatRequirements.HasMovement(h)).ToList();

                var icons = new List<(Texture2D tex, Color color, string tip)>();
                if (movement.Count > 0 && !ShiftController.DrivesItself(vehicle))
                    icons.Add(CapabilityIcon(DriveTex, pawn, movement, "SD_TipCanDrive", "SD_TipCannotDrive"));
                if (turrets.Count > 0)
                    icons.Add(CapabilityIcon(TurretTex, pawn, turrets, "SD_TipCanGun", "SD_TipCannotGun"));
                if (pawn == plannedCoDriver)
                    icons.Add((CoDriverTex, Color.white, "SD_TipCoDriver".Translate(pawn.LabelShortCap, vehicle.LabelShortCap)));

                // VF puts the Add/Remove button at rect.x + rect.width - 101; icons go just left of it.
                float x = rect.x + rect.width - 101f - 4f - icons.Count * (IconSize + IconGap);
                foreach (var (tex, color, tip) in icons)
                {
                    var r = new Rect(x, rect.y + 5f, IconSize, IconSize);
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

        private static (Texture2D, Color, string) CapabilityIcon(Texture2D tex, Pawn pawn, List<VehicleRoleHandler> seats,
            string canKey, string cannotKey)
        {
            if (seats.Any(h => h.CanOperateRole(pawn)))
                return (tex, Capable, canKey.Translate(pawn.LabelShortCap, vehicle.LabelShortCap));
            var reasons = SeatRequirements.WhyNot(pawn, seats[0]);
            return (tex, Incapable, cannotKey.Translate(pawn.LabelShortCap, vehicle.LabelShortCap)
                + "\n" + string.Join("\n", reasons.Select(r => "  - " + r)));
        }

        // --- night co-driver choice ----------------------------------------------------------------

        public static void DrawAssignees_Postfix(Rect __0)
        {
            if (disabled || vehicle == null || ShiftController.DrivesItself(vehicle))
                return;
            try
            {
                if (!vehicle.handlers.Any(ShiftController.IsMovementSeat))
                    return;

                var rect = new Rect(__0.x + 110f, __0.y, __0.width - 110f - 17f, 24f);
                var chosen = SharedDrivingTracker.Instance?.GetChosen(vehicle);

                string text;
                Color color;
                if (plannedCoDriver == null)
                {
                    text = "SD_SeatCoDriver".Translate("SD_CoDriverNone".Translate());
                    color = Incapable;
                }
                else
                {
                    text = "SD_SeatCoDriver".Translate(plannedCoDriver == chosen
                        ? "SD_CoDriverChosen".Translate(plannedCoDriver.LabelShort)
                        : "SD_CoDriverAuto".Translate(plannedCoDriver.LabelShort));
                    color = Capable;
                }

                var anchor = Text.Anchor;
                Text.Anchor = TextAnchor.UpperRight;
                GUI.color = color;
                Widgets.Label(rect, text);
                GUI.color = Color.white;
                Text.Anchor = anchor;

                if (Mouse.IsOver(rect))
                    Widgets.DrawHighlight(rect);
                TooltipHandler.TipRegion(rect, plannedCoDriver == null ? "SD_SeatCoDriverNoneTip".Translate() : "SD_SeatCoDriverTip".Translate());

                if (Widgets.ButtonInvisible(rect))
                    OpenCoDriverMenu(vehicle, coDriverCandidates.ToList(), chosen);
            }
            catch (Exception e)
            {
                Fail("co-driver line", e);
            }
        }

        private static void OpenCoDriverMenu(VehiclePawn v, List<Pawn> candidates, Pawn chosen)
        {
            var tracker = SharedDrivingTracker.Instance;
            if (tracker == null)
                return;
            var options = new List<FloatMenuOption>();
            string auto = "SD_SeatMenuAutomatic".Translate();
            if (chosen == null)
                auto += " " + "SD_MenuCurrent".Translate();
            options.Add(new FloatMenuOption(auto, () => tracker.SetChosen(v, null)));

            if (candidates.Count == 0)
                options.Add(new FloatMenuOption("SD_SeatMenuNoCandidates".Translate(), null));
            foreach (var p in candidates)
            {
                var pawn = p;
                string label = pawn.LabelShortCap;
                if (pawn == chosen)
                    label += " " + "SD_MenuCurrent".Translate();
                options.Add(new FloatMenuOption(label, () => tracker.SetChosen(v, pawn)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
