using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Vehicles;
using Verse;

namespace SharedDriving
{
    // Explains Vehicle Framework's seat checks (VehicleRoleHandler.CanOperateRole) in words.
    // Progression: Education adds a vehicle proficiency requirement to driver seats; it is read by
    // reflection so that mod stays optional.
    internal static class SeatRequirements
    {
        private static bool peResolved;
        private static MethodInfo peCheck;
        private static MethodInfo peTier;
        private static FieldInfo peSettings;
        private static FieldInfo peSystemOn;
        private static FieldInfo peVehicleOn;

        public static bool HasMovement(VehicleRoleHandler h) => (h.role.HandlingTypes & HandlingType.Movement) != HandlingType.None;
        public static bool HasTurret(VehicleRoleHandler h) => (h.role.HandlingTypes & HandlingType.Turret) != HandlingType.None;

        private static void ResolvePE()
        {
            if (peResolved)
                return;
            peResolved = true;
            try
            {
                var util = AccessTools.TypeByName("ProgressionEducation.ProficiencyUtility");
                var mod = AccessTools.TypeByName("ProgressionEducation.EducationMod");
                if (util == null || mod == null)
                    return;
                peCheck = AccessTools.Method(util, "CheckVehicleOperation");
                peTier = AccessTools.Method(util, "GetRequiredVehicleTierFromDef");
                peSettings = AccessTools.Field(mod, "settings");
                if (peSettings != null)
                {
                    peSystemOn = AccessTools.Field(peSettings.FieldType, "enableProficiencySystem");
                    peVehicleOn = AccessTools.Field(peSettings.FieldType, "enableVehicleProficiency");
                }
            }
            catch (Exception e)
            {
                peCheck = null;
                peTier = null;
                Log.Warning($"{Startup.Tag} Progression: Education found but its vehicle proficiency could not be read. Seat tooltips will not mention it. {e.Message}");
            }
        }

        private static bool PEVehicleProficiencyOn
        {
            get
            {
                ResolvePE();
                try
                {
                    var s = peSettings?.GetValue(null);
                    return s != null && peSystemOn != null && peVehicleOn != null
                        && (bool)peSystemOn.GetValue(s) && (bool)peVehicleOn.GetValue(s);
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string PERequiredTier(VehiclePawn vehicle)
        {
            if (!PEVehicleProficiencyOn || peTier == null)
                return null;
            try
            {
                return (peTier.Invoke(null, new object[] { vehicle.def }) as Def)?.label;
            }
            catch
            {
                return null;
            }
        }

        private static string PEFailure(Pawn pawn, VehicleRoleHandler handler)
        {
            if (!PEVehicleProficiencyOn || peCheck == null)
                return null;
            try
            {
                if (peCheck.Invoke(null, new object[] { pawn, handler }) is ValueTuple<bool, string> r && !r.Item1)
                    return r.Item2;
            }
            catch { }
            return null;
        }

        public static List<string> WhyNot(Pawn pawn, VehicleRoleHandler handler)
        {
            var list = new List<string>();
            if (handler.role.HandlingTypes == HandlingType.None)
                return list;

            if (!pawn.RaceProps.ToolUser)
                list.Add("SD_WhyNotToolUser".Translate());
            if (HasTurret(handler) && pawn.WorkTagIsDisabled(WorkTags.Violent))
                list.Add("SD_WhyNotViolence".Translate());
            if (pawn.Downed)
                list.Add("SD_WhyNotDowned".Translate());
            if (pawn.InMentalState)
                list.Add("SD_WhyNotMental".Translate());
            if (pawn.IsPrisoner)
                list.Add("SD_WhyNotPrisoner".Translate());
            if (pawn.IsColonyMech)
                list.Add("SD_WhyNotMech".Translate());
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                list.Add("SD_WhyNotManipulation".Translate());
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Consciousness))
                list.Add("SD_WhyNotConsciousness".Translate());
            if (pawn.Faction != handler.vehicle.Faction)
                list.Add("SD_WhyNotFaction".Translate());

            var pe = PEFailure(pawn, handler);
            if (!pe.NullOrEmpty())
                list.Add(pe);

            if (list.Count == 0)
                list.Add("SD_WhyNotOther".Translate());
            return list;
        }

        public static string Describe(VehicleRoleHandler handler)
        {
            var types = handler.role.HandlingTypes;
            if (types == HandlingType.None)
                return "SD_ReqPassenger".Translate();

            var lines = new List<string> { "SD_ReqBase".Translate() };
            if (HasTurret(handler))
                lines.Add("SD_ReqTurret".Translate());
            if (HasMovement(handler))
            {
                var tier = PERequiredTier(handler.vehicle);
                if (!tier.NullOrEmpty())
                    lines.Add("SD_ReqProficiency".Translate(tier));
            }
            return string.Join("\n", lines);
        }
    }
}
