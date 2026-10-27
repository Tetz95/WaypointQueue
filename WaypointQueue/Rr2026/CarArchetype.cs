#if RR2026
// Railroader 2026.1 dropped car archetypes for AAR car types (CarType: "LS..." steam, "LD..." diesel, "LT..." tender).
// The archetypes this mod checks are derived from the car type, so the checks read as before.
namespace Model
{
    public enum CarArchetype
    {
        Other,
        LocomotiveSteam,
        LocomotiveDiesel,
        Tender,
    }

    public static class CarArchetypeCompat
    {
        extension(Car car)
        {
            public CarArchetype Archetype =>
                car.CarType.IsLocomotiveSteam ? CarArchetype.LocomotiveSteam
                : car.CarType.IsLocomotiveDiesel ? CarArchetype.LocomotiveDiesel
                : car.CarType.IsTender ? CarArchetype.Tender
                : CarArchetype.Other;
        }

        public static bool IsLocomotive(this CarArchetype archetype) =>
            archetype == CarArchetype.LocomotiveSteam || archetype == CarArchetype.LocomotiveDiesel;
    }
}
#endif
