using System.Collections.Generic;
using RimWorld.Planet;
using Vehicles;
using Verse;

namespace SharedDriving
{
    // One seat swap done at the start of a night shift. Seats are stored as indexes into
    // vehicle.handlers so the record survives save/load.
    public class SwapRecord : IExposable
    {
        public VehiclePawn vehicle;
        public Pawn driver;
        public Pawn coDriver;
        public int driverSeat = -1;
        public int coDriverSeat = -1;

        public SwapRecord() { }

        public SwapRecord(VehiclePawn vehicle, VehicleRoleHandler driverHandler, Pawn driver,
            VehicleRoleHandler coDriverHandler, Pawn coDriver)
        {
            this.vehicle = vehicle;
            this.driver = driver;
            this.coDriver = coDriver;
            driverSeat = vehicle.handlers.IndexOf(driverHandler);
            coDriverSeat = vehicle.handlers.IndexOf(coDriverHandler);
        }

        public VehicleRoleHandler DriverHandler => Seat(driverSeat);
        public VehicleRoleHandler CoDriverHandler => Seat(coDriverSeat);

        private VehicleRoleHandler Seat(int index)
        {
            var handlers = vehicle?.handlers;
            return handlers != null && index >= 0 && index < handlers.Count ? handlers[index] : null;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref vehicle, "vehicle");
            Scribe_References.Look(ref driver, "driver");
            Scribe_References.Look(ref coDriver, "coDriver");
            Scribe_Values.Look(ref driverSeat, "driverSeat", -1);
            Scribe_Values.Look(ref coDriverSeat, "coDriverSeat", -1);
        }
    }

    public class CaravanState : IExposable
    {
        public Caravan caravan;
        public bool enabled;
        public bool nightActive;
        public int lastWarningTick = -999999;
        public List<SwapRecord> swaps = new List<SwapRecord>();

        // Saves made before co-driver choices moved to the tracker (per vehicle). Read once, then migrated.
        internal List<VehiclePawn> legacyChosenVehicles;
        internal List<Pawn> legacyChosenPawns;

        public CaravanState() { }

        public CaravanState(Caravan caravan)
        {
            this.caravan = caravan;
        }

        public bool IsEmpty => !enabled && !nightActive;

        public void ExposeData()
        {
            Scribe_References.Look(ref caravan, "caravan");
            Scribe_Values.Look(ref enabled, "enabled");
            Scribe_Values.Look(ref nightActive, "nightActive");
            Scribe_Values.Look(ref lastWarningTick, "lastWarningTick", -999999);
            Scribe_Collections.Look(ref swaps, "swaps", LookMode.Deep);

            if (Scribe.mode != LoadSaveMode.Saving)
            {
                Scribe_Collections.Look(ref legacyChosenVehicles, "chosenVehicles", LookMode.Reference);
                Scribe_Collections.Look(ref legacyChosenPawns, "chosenPawns", LookMode.Reference);
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                swaps ??= new List<SwapRecord>();
                swaps.RemoveAll(r => r == null || r.vehicle == null);
            }
        }
    }

    public class SharedDrivingTracker : WorldComponent
    {
        private const int CheckInterval = 250;

        private List<CaravanState> states = new List<CaravanState>();

        // Night co-driver chosen by the player, per vehicle (set from the toggle or the seat dialog).
        private List<VehiclePawn> chosenVehicles = new List<VehiclePawn>();
        private List<Pawn> chosenPawns = new List<Pawn>();

        public SharedDrivingTracker(World world) : base(world) { }

        public static SharedDrivingTracker Instance => Find.World?.GetComponent<SharedDrivingTracker>();

        public CaravanState Get(Caravan caravan)
        {
            if (caravan == null)
                return null;
            foreach (var s in states)
            {
                if (s.caravan == caravan)
                    return s;
            }
            return null;
        }

        public CaravanState GetOrCreate(Caravan caravan)
        {
            var s = Get(caravan);
            if (s == null)
            {
                s = new CaravanState(caravan);
                states.Add(s);
            }
            return s;
        }

        // Active night-shift swaps of this vehicle's driver seat.
        public IEnumerable<SwapRecord> NightSwaps(VehiclePawn vehicle, VehicleRoleHandler driverSeat)
        {
            foreach (var s in states)
            {
                if (s == null || !s.nightActive || s.swaps == null)
                    continue;
                foreach (var r in s.swaps)
                {
                    if (r != null && r.vehicle == vehicle && r.driver != null && r.coDriver != null && r.DriverHandler == driverSeat)
                        yield return r;
                }
            }
        }

        public Pawn GetChosen(VehiclePawn vehicle)
        {
            int i = chosenVehicles.IndexOf(vehicle);
            return i >= 0 ? chosenPawns[i] : null;
        }

        public void SetChosen(VehiclePawn vehicle, Pawn pawn)
        {
            int i = chosenVehicles.IndexOf(vehicle);
            if (i >= 0)
            {
                chosenVehicles.RemoveAt(i);
                chosenPawns.RemoveAt(i);
            }
            if (vehicle != null && pawn != null)
            {
                chosenVehicles.Add(vehicle);
                chosenPawns.Add(pawn);
            }
        }

        public override void WorldComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckInterval != 0)
                return;

            for (int i = states.Count - 1; i >= 0; i--)
            {
                var s = states[i];
                if (s.caravan == null || s.caravan.Destroyed || s.IsEmpty)
                {
                    states.RemoveAt(i);
                    continue;
                }
                if (s.caravan.Spawned && s.caravan is Vehicles.World.VehicleCaravan vc)
                    ShiftController.Update(vc, s);
            }

            for (int i = chosenVehicles.Count - 1; i >= 0; i--)
            {
                var v = chosenVehicles[i];
                var p = chosenPawns[i];
                if (v == null || v.Destroyed || p == null || p.Dead)
                {
                    chosenVehicles.RemoveAt(i);
                    chosenPawns.RemoveAt(i);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref states, "states", LookMode.Deep);
            Scribe_Collections.Look(ref chosenVehicles, "chosenVehicles", LookMode.Reference);
            Scribe_Collections.Look(ref chosenPawns, "chosenPawns", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                states ??= new List<CaravanState>();
                chosenVehicles ??= new List<VehiclePawn>();
                chosenPawns ??= new List<Pawn>();

                foreach (var s in states)
                {
                    if (s?.legacyChosenVehicles == null || s.legacyChosenPawns == null)
                        continue;
                    int n = System.Math.Min(s.legacyChosenVehicles.Count, s.legacyChosenPawns.Count);
                    for (int i = 0; i < n; i++)
                    {
                        if (s.legacyChosenVehicles[i] != null && s.legacyChosenPawns[i] != null && GetChosen(s.legacyChosenVehicles[i]) == null)
                            SetChosen(s.legacyChosenVehicles[i], s.legacyChosenPawns[i]);
                    }
                    s.legacyChosenVehicles = null;
                    s.legacyChosenPawns = null;
                }
                states.RemoveAll(s => s == null || s.caravan == null);

                int count = System.Math.Min(chosenVehicles.Count, chosenPawns.Count);
                chosenVehicles.RemoveRange(count, chosenVehicles.Count - count);
                chosenPawns.RemoveRange(count, chosenPawns.Count - count);
                for (int i = count - 1; i >= 0; i--)
                {
                    if (chosenVehicles[i] == null || chosenPawns[i] == null)
                    {
                        chosenVehicles.RemoveAt(i);
                        chosenPawns.RemoveAt(i);
                    }
                }
            }
        }
    }
}
