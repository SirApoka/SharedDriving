using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Vehicles;
using Vehicles.World;
using Verse;

namespace SharedDriving
{
    public class Command_SharedDriving : Command_Toggle
    {
        public VehicleCaravan caravan;

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                var tracker = SharedDrivingTracker.Instance;
                if (tracker == null || caravan == null)
                    yield break;

                bool any = false;
                foreach (var vehicle in caravan.VehiclesListForReading)
                {
                    if (ShiftController.DrivesItself(vehicle))
                        continue;
                    var seat = vehicle.handlers.FirstOrDefault(ShiftController.IsMovementSeat);
                    if (seat == null)
                        continue;
                    any = true;

                    var v = vehicle;
                    var current = tracker.GetChosen(v);
                    string auto = "SD_MenuAutomatic".Translate(v.LabelShortCap);
                    if (current == null)
                        auto += " " + "SD_MenuCurrent".Translate();
                    yield return new FloatMenuOption(auto, () => tracker.SetChosen(v, null));

                    var candidates = ShiftController.PassengerCandidates(v, seat).ToList();
                    ShiftController.ToDayArrangement(v, seat, candidates, seat.thingOwner.InnerListForReading.OfType<Pawn>().ToList());
                    foreach (var p in candidates)
                    {
                        var pawn = p;
                        string label = "SD_MenuPick".Translate(v.LabelShortCap, pawn.LabelShort);
                        if (current == pawn)
                            label += " " + "SD_MenuCurrent".Translate();
                        yield return new FloatMenuOption(label, () =>
                        {
                            tracker.SetChosen(v, pawn);
                            // At night the choice takes the wheel right away, like dragging in the Pawns tab.
                            ShiftController.SeatNightDriverNow(caravan, v, pawn);
                        });
                    }
                }

                if (!any)
                    yield return new FloatMenuOption("SD_MenuNoVehicles".Translate(), null);
            }
        }
    }

    [StaticConstructorOnStartup]
    internal static class SharedDrivingGizmo
    {
        private static readonly Texture2D Icon =
            ContentFinder<Texture2D>.Get("UI/SharedDriving/Toggle", reportFailure: false) ?? RimWorld.TexCommand.PauseCaravan;

        public static Gizmo Make(VehicleCaravan caravan)
        {
            var tracker = SharedDrivingTracker.Instance;
            var state = tracker?.Get(caravan);
            bool on = state?.enabled ?? false;

            var cmd = new Command_SharedDriving
            {
                caravan = caravan,
                defaultLabel = "SD_CommandLabel".Translate(),
                defaultDesc = "SD_CommandDesc".Translate() + "\n\n" + StatusText(caravan, state),
                icon = Icon,
                isActive = () => SharedDrivingTracker.Instance?.Get(caravan)?.enabled ?? false,
                toggleAction = () => ShiftController.SetEnabled(caravan,
                    !(SharedDrivingTracker.Instance?.Get(caravan)?.enabled ?? false)),
            };

            // Only block turning it ON; it must always be possible to turn it off.
            if (!on)
            {
                var plan = ShiftController.Plan(caravan, state, includeOutside: false);
                if (!plan.Feasible)
                    cmd.Disable("SD_CommandDisabledNoCoDriver".Translate(Bullets(plan.blockers)));
            }
            return cmd;
        }

        public static string StatusText(VehicleCaravan caravan, CaravanState state)
        {
            var sb = new StringBuilder();
            sb.Append("SD_StatusHeader".Translate().Colorize(ColoredText.TipSectionTitleColor));
            sb.AppendLine();

            if (state == null || !state.enabled)
            {
                sb.Append("SD_StatusOff".Translate());
                return sb.ToString();
            }

            float hour = ShiftClock.LocalHour(caravan);

            if (state.nightActive)
            {
                sb.Append("SD_StatusNightShift".Translate());
                foreach (var vehicle in caravan.VehiclesListForReading)
                {
                    sb.AppendLine();
                    var r = state.swaps.FirstOrDefault(x => x.vehicle == vehicle);
                    if (r != null)
                    {
                        sb.Append("  - " + "SD_VehicleLineNight".Translate(vehicle.LabelShortCap,
                            NightDriverLabel(vehicle, r.coDriver), r.driver.LabelShort));
                    }
                    else if (ShiftController.DrivesItself(vehicle))
                    {
                        sb.Append("  - " + "SD_VehicleAutonomous".Translate(vehicle.LabelShortCap));
                    }
                    else
                    {
                        // The day driver took the wheel back at night and nobody else can drive by day.
                        var driver = vehicle.handlers.FirstOrDefault(ShiftController.IsMovementSeat)?
                            .thingOwner.InnerListForReading.FirstOrDefault();
                        sb.Append("  - " + "SD_VehicleLineNightOnly".Translate(vehicle.LabelShortCap,
                            driver != null ? driver.LabelShort : (string)"SD_NoDriver".Translate()));
                    }
                }
                var outside = ShiftController.OutsideReasons(caravan);
                if (outside.Count > 0)
                {
                    sb.AppendLine();
                    sb.Append("SD_StatusWillRest".Translate().Colorize(ColorLibrary.RedReadable));
                    sb.AppendLine();
                    sb.Append(Bullets(outside));
                }
            }
            else
            {
                var plan = ShiftController.Plan(caravan, state, includeOutside: true);
                sb.Append(ShiftClock.IsNight(hour) && !plan.Feasible
                    ? "SD_StatusWillRest".Translate().Colorize(ColorLibrary.RedReadable)
                    : "SD_StatusDayShift".Translate());

                foreach (var vehicle in caravan.VehiclesListForReading)
                {
                    sb.AppendLine();
                    sb.Append("  - " + VehicleLine(vehicle, plan, state));
                }

                if (!plan.Feasible)
                {
                    if (!ShiftClock.IsNight(hour))
                    {
                        sb.AppendLine();
                        sb.Append("SD_StatusWillRest".Translate().Colorize(ColorLibrary.RedReadable));
                    }
                    sb.AppendLine();
                    sb.Append(Bullets(plan.blockers));
                }
            }

            sb.AppendLine();
            sb.Append("SD_NextChangeAt".Translate(ShiftClock.NextChangeLabel(hour),
                ShiftClock.HoursUntilChange(hour).ToString("0.#")));
            return sb.ToString();
        }

        private static string VehicleLine(VehiclePawn vehicle, NightPlan plan, CaravanState state)
        {
            if (ShiftController.DrivesItself(vehicle))
                return "SD_VehicleAutonomous".Translate(vehicle.LabelShortCap);

            var seat = vehicle.handlers.FirstOrDefault(ShiftController.IsMovementSeat);
            var driver = seat?.thingOwner.InnerListForReading.FirstOrDefault();
            string driverLabel = driver != null ? driver.LabelShort : (string)"SD_NoDriver".Translate();

            var swap = plan.swaps.FirstOrDefault(r => r.vehicle == vehicle);
            string coLabel = swap == null ? (string)"SD_CoDriverNone".Translate() : NightDriverLabel(vehicle, swap.coDriver);

            return "SD_VehicleLine".Translate(vehicle.LabelShortCap, driverLabel, coLabel);
        }

        private static string NightDriverLabel(VehiclePawn vehicle, Pawn nightDriver) =>
            SharedDrivingTracker.Instance?.GetChosen(vehicle) == nightDriver
                ? "SD_CoDriverChosen".Translate(nightDriver.LabelShort)
                : "SD_CoDriverAuto".Translate(nightDriver.LabelShort);

        private static string Bullets(IEnumerable<string> lines) =>
            string.Join("\n", lines.Select(l => "  - " + l));

        public static string InspectLine(VehicleCaravan caravan)
        {
            var state = SharedDrivingTracker.Instance?.Get(caravan);
            if (state == null || !state.enabled)
                return null;

            if (state.nightActive)
                return ShiftController.DrivesThroughNight(caravan)
                    ? "SD_InspectNight".Translate()
                    : "SD_InspectWillRest".Translate();

            bool feasible = ShiftController.Plan(caravan, state, includeOutside: true).Feasible;
            return feasible ? "SD_InspectDay".Translate() : "SD_InspectWillRest".Translate();
        }
    }
}
