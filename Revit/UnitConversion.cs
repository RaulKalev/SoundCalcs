namespace SoundCalcs.Revit
{
    /// <summary>
    /// Conversion helpers between Revit internal units (feet) and compute units (meters).
    /// </summary>
    public static class UnitConversion
    {
        public const double FeetToMeters = 0.3048;
        public const double MetersToFeet = 1.0 / FeetToMeters;

        public static double FtToM(double feet) => feet * FeetToMeters;
        public static double MToFt(double meters) => meters * MetersToFeet;

        public static Domain.Vec3 XyzToVec3(Autodesk.Revit.DB.XYZ xyz)
        {
            return new Domain.Vec3(
                xyz.X * FeetToMeters,
                xyz.Y * FeetToMeters,
                xyz.Z * FeetToMeters);
        }

        public static Autodesk.Revit.DB.XYZ Vec3ToXyz(Domain.Vec3 v)
        {
            return new Autodesk.Revit.DB.XYZ(
                v.X * MetersToFeet,
                v.Y * MetersToFeet,
                v.Z * MetersToFeet);
        }

        /// <summary>
        /// Convert a Revit XYZ direction (unitless) to Vec3 without scaling.
        /// </summary>
        public static Domain.Vec3 DirectionToVec3(Autodesk.Revit.DB.XYZ dir)
        {
            return new Domain.Vec3(dir.X, dir.Y, dir.Z);
        }
    }
}
