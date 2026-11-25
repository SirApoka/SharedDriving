using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using SmashTools;
using Vehicles;
using Vehicles.World;
using Verse;

namespace SharedDriving
{
    internal static class ShiftClock
    {
        public const float DayStart = 6f;
        public const float NightStart = 18f;

        public static float LocalHour(Caravan caravan) =>
            GenDate.HourFloat(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(caravan.Tile).x);

        public static bool IsNight(float hour) => hour >= NightStart || hour < DayStart;

        public static float HoursUntilChange(float hour)
        {
            if (!IsNight(hour))
                return NightStart - hour;
            return hour >= NightStart ? 24f - hour + DayStart : DayStart - hour;
        }

        public static string NextChangeLabel(float hour) => IsNight(hour) ? "6:00" : "18:00";
    }

    internal sealed class NightPlan
    {
        public readonly List<SwapRecord> swaps = new List<SwapRecord>();
        public readonly List<string> blockers = new List<string>();
        public bool Feasible => blockers.Count == 0;
    }

    internal static class ShiftController
    {
        private const int WarningInterval = GenDate.TicksPerHour * 12;

        public static bool IsMovementSeat(VehicleRoleHandler h) =>
            h?.role != null && (h.role.HandlingTypes & HandlingType.Movement) != HandlingType.None;

        public static bool IsPassengerSeat(VehicleRoleHandler h) =>
            h?.role != null && h.role.HandlingTypes == HandlingType.None;

        public static bool DrivesItself(VehiclePawn v) =>
            (v.MovementPermissions & VehiclePermissions.Autonomous) != VehiclePermissions.None;

        public static List<Pawn> PawnsOutside(VehicleCaravan caravan) =>
            caravan.DismountedPawnsListForReading.Where(p => p != null && !p.Dead).ToList();

        // People on foot or riding, and animals (VF stores animals in a vehicle's cargo, so the player
        // can fix that one from the caravan's Pawns tab).
        public static List<string> OutsideReasons(VehicleCaravan caravan)
        {
            var reasons = new List<string>();
            var outside = PawnsOutside(caravan);
            var people = outside.Where(p => !p.IsAnimal).ToList();
            var animals = outside.Where(p => p.IsAnimal).ToList();
            if (people.Count > 0)
                reasons.Add("SD_ReasonOutsidePeople".Translate(people.Select(p => p.LabelShortCap).ToCommaList()));
            if (animals.Count > 0)
                reasons.Add("SD_ReasonOutsideAnimals".Translate(animals.Select(p => p.LabelShortCap).ToCommaList(),
                    "VF_TabPassengers".Translate(), "VF_Caravan_Cargo".Translate()));
            return reasons;
        }

        public static bool CanTakeWheel(VehicleRoleHandler driverSeat, Pawn pawn) =>
            pawn != null && !pawn.Dead && !pawn.Downed && driverSeat.CanOperateRole(pawn);

        // During the night shift the regular driver sits in a passenger seat and the co-driver drives.
        // Co-driver choices apply to the next night, so list candidates as seated in the day arrangement:
        // drop the sleeping regular driver, add back the co-driver who is driving now.
        public static void ToDayArrangement(VehiclePawn vehicle, VehicleRoleHandler driverSeat,
            List<Pawn> candidates, ICollection<Pawn> inDriverSeatNow)
        {
            var tracker = SharedDrivingTracker.Instance;
            if (tracker == null)
                return;
            foreach (var r in tracker.NightSwaps(vehicle, driverSeat))
            {
                if (!candidates.Contains(r.driver) || !inDriverSeatNow.Contains(r.coDriver) || !CanTakeWheel(driverSeat, r.coDriver))
                    continue;
                candidates.Remove(r.driver);
                if (!candidates.Contains(r.coDriver))
                    candidates.Add(r.coDriver);
            }
        }

        public static IEnumerable<Pawn> PassengerCandidates(VehiclePawn vehicle, VehicleRoleHandler driverSeat)
        {
            foreach (var h in vehicle.handlers)
            {
                if (!IsPassengerSeat(h))
                    continue;
                foreach (Pawn p in h.thingOwner.InnerListForReading)
                {
                    if (CanTakeWheel(driverSeat, p))
                        yield return p;
                }
            }
        }

        // The night shift: every pawn in a driver seat swaps with a passenger who can operate that seat.
        // Turret seats are never touched. Vehicles that drive themselves need no co-driver.
        public static NightPlan Plan(VehicleCaravan caravan, CaravanState state, bool includeOutside)
        {
            var plan = new NightPlan();

            if (includeOutside)
            {
                plan.blockers.AddRange(OutsideReasons(caravan));
            }

            foreach (var vehicle in caravan.VehiclesListForReading)
            {
                if (DrivesItself(vehicle))
                    continue;

                var used = new HashSet<Pawn>();
                foreach (var seat in vehicle.handlers)
                {
                    if (!IsMovementSeat(seat))
                        continue;

                    var drivers = seat.thingOwner.InnerListForReading.ToList();
                    if (drivers.Count == 0)
                    {
                        if (seat.role.SlotsToOperate > 0)
                            plan.blockers.Add("SD_ReasonNoDriver".Translate(vehicle.LabelShortCap, seat.role.label));
                        continue;
                    }

                    foreach (var driver in drivers)
                    {
                        var co = PickCoDriver(vehicle, seat, state, used);
                        if (co == null)
                        {
                            plan.blockers.Add("SD_ReasonNoCoDriver".Translate(vehicle.LabelShortCap, seat.role.label));
                            continue;
                        }
                        used.Add(co);
                        plan.swaps.Add(new SwapRecord(vehicle, seat, driver, (VehicleRoleHandler)co.ParentHolder, co));
                    }
                }
            }
            return plan;
        }

        private static Pawn PickCoDriver(VehiclePawn vehicle, VehicleRoleHandler seat, CaravanState state, HashSet<Pawn> used)
        {
            var candidates = PassengerCandidates(vehicle, seat).Where(p => !used.Contains(p)).ToList();
            if (candidates.Count == 0)
                return null;

            var chosen = SharedDrivingTracker.Instance?.GetChosen(vehicle);
            if (chosen != null && candidates.Contains(chosen))
                return chosen;

            return candidates.MaxBy(p => p.needs?.rest?.CurLevel ?? 1f);
        }

        public static bool DrivesThroughNight(VehicleCaravan caravan)
        {
            var s = SharedDrivingTracker.Instance?.Get(caravan);
            if (s == null || !s.enabled || !s.nightActive)
                return false;
            if (PawnsOutside(caravan).Count > 0)
                return false;
            foreach (var v in caravan.VehiclesListForReading)
            {
                if (!v.HasEnoughOperators)
                    return false;
            }
            return true;
        }

        // Used for the arrival estimate: true when the caravan is expected to keep moving at night.
        public static bool WouldDriveThroughNight(VehicleCaravan caravan)
        {
            var s = SharedDrivingTracker.Instance?.Get(caravan);
            if (s == null || !s.enabled)
                return false;
            if (s.nightActive)
                return DrivesThroughNight(caravan);
            return Plan(caravan, s, includeOutside: true).Feasible;
        }

        public static void SetEnabled(VehicleCaravan caravan, bool on)
        {
            var tracker = SharedDrivingTracker.Instance;
            if (tracker == null)
                return;
            var s = tracker.GetOrCreate(caravan);

            if (on)
            {
                s.enabled = true;
                if (ShiftClock.IsNight(ShiftClock.LocalHour(caravan)))
                {
                    TryStartNight(caravan, s, forceWarning: true);
                }
                else
                {
                    var plan = Plan(caravan, s, includeOutside: true);
                    if (!plan.Feasible)
                        Warn(caravan, s, "SD_MessageWillRest", plan.blockers, force: true);
                }
            }
            else
            {
                bool hadSwaps = s.nightActive && s.swaps.Count > 0;
                EndNight(caravan, s);
                s.enabled = false;
                if (hadSwaps)
                {
                    Messages.Message("SD_MessageRestored".Translate(caravan.LabelCap), caravan,
                        MessageTypeDefOf.SilentInput, historical: false);
                }
            }
        }

        public static void Update(VehicleCaravan caravan, CaravanState s)
        {
            if (!s.enabled)
                return;

            bool night = ShiftClock.IsNight(ShiftClock.LocalHour(caravan));
            if (!night)
            {
                if (s.nightActive)
                {
                    bool hadSwaps = s.swaps.Count > 0;
                    EndNight(caravan, s);
                    if (hadSwaps)
                    {
                        Messages.Message("SD_MessageDayShift".Translate(caravan.LabelCap), caravan,
                            MessageTypeDefOf.SilentInput, historical: false);
                    }
                }
                ForgetNightChoiceAtWheel(caravan);
                return;
            }

            if (!s.nightActive)
            {
                TryStartNight(caravan, s, forceWarning: false);
                return;
            }

            // Someone else was put at the wheel (dragged in the Pawns tab): that pawn becomes the night driver.
            AdoptNightDrivers(caravan, s);

            // A night driver who can no longer drive (downed, gone) ends the arrangement;
            // the next plan picks someone else or the caravan rests.
            foreach (var r in s.swaps)
            {
                var seat = r.DriverHandler;
                if (seat == null || !seat.thingOwner.Contains(r.coDriver) || !CanTakeWheel(seat, r.coDriver))
                {
                    EndNight(caravan, s);
                    TryStartNight(caravan, s, forceWarning: true);
                    return;
                }
            }

            var outside = OutsideReasons(caravan);
            if (outside.Count > 0)
                Warn(caravan, s, "SD_MessageWillRest", outside, force: false);
        }

        // Model decided 2026-11-24: each vehicle has a day driver (SwapRecord.driver at night, whoever is at the
        // wheel by day) and a night driver (SwapRecord.coDriver). Putting someone at the wheel during the night
        // makes them the night driver, remembered as the chosen one (same as the right-click menu). If the day
        // driver is the one put back at the wheel, the roles trade places: the pawn who left the wheel becomes
        // the day driver and takes over at 6:00. Nothing the player does is undone mid-shift.
        private static void AdoptNightDrivers(VehicleCaravan caravan, CaravanState s)
        {
            var tracker = SharedDrivingTracker.Instance;
            for (int i = s.swaps.Count - 1; i >= 0; i--)
            {
                var r = s.swaps[i];
                var seat = r.DriverHandler;
                if (seat == null || r.coDriver == null || seat.thingOwner.Contains(r.coDriver))
                    continue;

                // The pawn now at the wheel that no other record of this seat accounts for.
                var atWheel = seat.thingOwner.InnerListForReading
                    .FirstOrDefault(p => p != null && !s.swaps.Any(o => o != r && o.vehicle == r.vehicle && o.coDriver == p));
                if (atWheel == null || !CanTakeWheel(seat, atWheel))
                    continue; // nobody able at the wheel: handled as "night driver gone" below

                var previous = r.coDriver;
                if (atWheel == r.driver)
                {
                    // Day driver back at the wheel: the previous night driver becomes the day driver.
                    bool canTakeDay = previous.ParentHolder is VehicleRoleHandler h && h.vehicle == r.vehicle
                        && CanTakeWheel(seat, previous);
                    if (canTakeDay)
                    {
                        r.driver = previous;
                        r.coDriverSeat = r.vehicle.handlers.IndexOf((VehicleRoleHandler)previous.ParentHolder);
                    }
                    else
                    {
                        // No one to hand the wheel back to at 6:00: this pawn keeps driving.
                        s.swaps.RemoveAt(i);
                    }
                }
                else if (r.driver?.ParentHolder is VehicleRoleHandler dayHolder && dayHolder.vehicle == r.vehicle)
                {
                    r.coDriverSeat = r.vehicle.handlers.IndexOf(dayHolder);
                }
                r.coDriver = atWheel;
                tracker?.SetChosen(r.vehicle, atWheel);

                Messages.Message("SD_MessageNewNightDriver".Translate(caravan.LabelCap, r.vehicle.LabelShortCap,
                    atWheel.LabelShort), caravan, MessageTypeDefOf.SilentInput, historical: false);
            }
        }

        // Called right after a drag in the Pawns tab, so roles and icons update at once (even while paused)
        // instead of on the next periodic check.
        public static void OnSeatsChanged(VehicleCaravan caravan)
        {
            var s = SharedDrivingTracker.Instance?.Get(caravan);
            if (s == null || !s.enabled)
                return;
            if (s.nightActive)
                AdoptNightDrivers(caravan, s);
            else if (!ShiftClock.IsNight(ShiftClock.LocalHour(caravan)))
                ForgetNightChoiceAtWheel(caravan);
        }

        // By day, whoever is at the wheel is the day driver. If the player puts the chosen night driver there,
        // that pawn is now the day driver, so the night choice goes back to automatic.
        private static void ForgetNightChoiceAtWheel(VehicleCaravan caravan)
        {
            var tracker = SharedDrivingTracker.Instance;
            if (tracker == null)
                return;
            foreach (var vehicle in caravan.VehiclesListForReading)
            {
                var chosen = tracker.GetChosen(vehicle);
                if (chosen != null && vehicle.handlers.Any(h => IsMovementSeat(h) && h.thingOwner.Contains(chosen)))
                    tracker.SetChosen(vehicle, null);
            }
        }

        // Right-click menu during the night shift: seat the chosen night driver right away.
        public static void SeatNightDriverNow(VehicleCaravan caravan, VehiclePawn vehicle, Pawn pawn)
        {
            var s = SharedDrivingTracker.Instance?.Get(caravan);
            if (s == null || !s.nightActive || pawn == null)
                return;
            var r = s.swaps.FirstOrDefault(x => x.vehicle == vehicle && x.coDriver != pawn && x.driver != pawn);
            var seat = r?.DriverHandler;
            if (seat == null || !(pawn.ParentHolder is VehicleRoleHandler from) || from.vehicle != vehicle || !seat.thingOwner.Contains(r.coDriver))
                return;
            if (SwapSeats(vehicle, seat, r.coDriver, from, pawn))
                r.coDriver = pawn;
        }

        private static void TryStartNight(VehicleCaravan caravan, CaravanState s, bool forceWarning)
        {
            var plan = Plan(caravan, s, includeOutside: true);
            if (!plan.Feasible)
            {
                Warn(caravan, s, "SD_MessageWillRest", plan.blockers, forceWarning);
                return;
            }

            var done = new List<SwapRecord>();
            foreach (var r in plan.swaps)
            {
                if (!SwapSeats(r.vehicle, r.DriverHandler, r.driver, r.CoDriverHandler, r.coDriver))
                {
                    Log.Warning($"{Startup.Tag} Could not swap {r.driver} and {r.coDriver} in {r.vehicle}. Night shift cancelled.");
                    s.swaps = done;
                    EndNight(caravan, s);
                    return;
                }
                done.Add(r);
            }

            s.swaps = done;
            s.nightActive = true;

            if (done.Count > 0)
            {
                var parts = done.Select(r => (string)"SD_MessageSwapNight".Translate(
                    r.vehicle.LabelShortCap, r.coDriver.LabelShort, r.driver.LabelShort));
                Messages.Message("SD_MessageNightShift".Translate(caravan.LabelCap, string.Join("; ", parts) + "."),
                    caravan, MessageTypeDefOf.SilentInput, historical: false);
            }
        }

        public static void EndNight(VehicleCaravan caravan, CaravanState s)
        {
            for (int i = s.swaps.Count - 1; i >= 0; i--)
                SwapBack(s.swaps[i]);
            s.swaps.Clear();
            s.nightActive = false;
        }

        private static void SwapBack(SwapRecord r)
        {
            var v = r.vehicle;
            var driverSeat = r.DriverHandler;
            if (v == null || driverSeat == null || r.coDriver == null || r.driver == null || r.driver.Dead)
                return;
            if (!driverSeat.thingOwner.Contains(r.coDriver))
                return;

            var coSeat = r.CoDriverHandler;
            if (coSeat != null && coSeat.thingOwner.Contains(r.driver))
            {
                SwapSeats(v, driverSeat, r.coDriver, coSeat, r.driver);
                return;
            }

            // The driver moved to another seat of the same vehicle meanwhile: swap with wherever they are.
            if (r.driver.ParentHolder is VehicleRoleHandler current && current.vehicle == v && current != driverSeat)
                SwapSeats(v, driverSeat, r.coDriver, current, r.driver);
        }

        // Same operation Vehicle Framework uses when the player swaps two pawns in the caravan tab.
        private static bool SwapSeats(VehiclePawn vehicle, VehicleRoleHandler seatA, Pawn pawnA,
            VehicleRoleHandler seatB, Pawn pawnB)
        {
            if (vehicle == null || seatA == null || seatB == null || seatA == seatB)
                return false;
            if (!seatA.thingOwner.Contains(pawnA) || !seatB.thingOwner.Contains(pawnB))
                return false;

            seatA.thingOwner.Swap(seatB.thingOwner, pawnA, pawnB);
            bool ok = seatA.thingOwner.Contains(pawnB) && seatB.thingOwner.Contains(pawnA);
            vehicle.EventRegistry?[VehicleEventDefOf.PawnChangedSeats]?.ExecuteEvents();
            return ok;
        }

        private static void Warn(VehicleCaravan caravan, CaravanState s, string key, List<string> reasons, bool force)
        {
            int now = Find.TickManager.TicksGame;
            if (!force && now - s.lastWarningTick < WarningInterval)
                return;
            s.lastWarningTick = now;
            Messages.Message(key.Translate(caravan.LabelCap, string.Join("; ", reasons) + "."), caravan,
                MessageTypeDefOf.CautionInput, historical: false);
        }
    }
}
