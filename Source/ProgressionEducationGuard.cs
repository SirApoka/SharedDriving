using Vehicles;

namespace SharedDriving
{
    // Progression: Education's prefix on VehicleTabHelper_Passenger.HandleDragEvent passes the drop target
    // to ProficiencyUtility.CheckVehicleOperation without checking that it is a seat. Dropping a pawn onto
    // a vehicle's cargo (Pawn_InventoryTracker) or the Dismounted list makes it read 'role' from the wrong
    // type and throw, which also leaves VF's tab drawer stuck ("not re-entrant" every frame).
    // Non-seat targets have no proficiency requirement, so answer "can operate" for them.
    internal static class ProgressionEducationGuard
    {
        public static bool CheckVehicleOperation_Prefix(object __1, ref (bool, string) __result)
        {
            if (__1 is VehicleRoleHandler)
                return true;
            __result = (true, null);
            return false;
        }
    }
}
