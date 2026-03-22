namespace SoundCalcs.Domain
{
    /// <summary>
    /// A triangle defined by three vertices in meters.
    /// Used for tessellated surface representation.
    /// </summary>
    public struct Triangle
    {
        public readonly Vec3 V0;
        public readonly Vec3 V1;
        public readonly Vec3 V2;

        public Triangle(Vec3 v0, Vec3 v1, Vec3 v2)
        {
            V0 = v0;
            V1 = v1;
            V2 = v2;
        }

        public Vec3 Normal => Vec3.Cross(V1 - V0, V2 - V0).Normalized();

        public Vec3 Centroid => new Vec3(
            (V0.X + V1.X + V2.X) / 3.0,
            (V0.Y + V1.Y + V2.Y) / 3.0,
            (V0.Z + V1.Z + V2.Z) / 3.0);

        public double Area
        {
            get
            {
                Vec3 cross = Vec3.Cross(V1 - V0, V2 - V0);
                return cross.Length * 0.5;
            }
        }
    }
}
