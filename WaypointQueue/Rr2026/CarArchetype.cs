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
        // Any other locomotive type (CarType.IsLocomotive: an "L" type that isn't a tender)
        LocomotiveOther,
    }

    public static class CarArchetypeCompat
    {
        extension(Car car)
        {
            public CarArchetype Archetype =>
                car.CarType.IsLocomotiveSteam ? CarArchetype.LocomotiveSteam
                : car.CarType.IsLocomotiveDiesel ? CarArchetype.LocomotiveDiesel
                : car.CarType.IsTender ? CarArchetype.Tender
                : car.CarType.IsLocomotive ? CarArchetype.LocomotiveOther
                : CarArchetype.Other;
        }

        public static bool IsLocomotive(this CarArchetype archetype) =>
            archetype == CarArchetype.LocomotiveSteam || archetype == CarArchetype.LocomotiveDiesel || archetype == CarArchetype.LocomotiveOther;
    }
}
