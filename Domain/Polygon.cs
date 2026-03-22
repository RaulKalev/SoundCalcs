using System.Collections.Generic;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// A coplanar polygon defined by an ordered list of vertices in meters.
    /// Represents a wall, floor, or ceiling surface for future reflection calculations.
    /// </summary>
    public class Polygon
    {
        public List<Vec3> Vertices { get; set; } = new List<Vec3>();

        /// <summary>
        /// Absorption coefficient (0.0 = fully reflective, 1.0 = fully absorbing).
        /// Default is a reasonable mid-range value for generic surfaces.
        /// </summary>
        public double AbsorptionCoefficient { get; set; } = 0.3;

        /// <summary>
        /// Material name for display and future material mapping.
        /// </summary>
        public string MaterialName { get; set; } = "Generic";

        public Vec3 Normal
        {
            get
            {
                if (Vertices.Count < 3) return Vec3.UnitZ;
                return Vec3.Cross(Vertices[1] - Vertices[0], Vertices[2] - Vertices[0]).Normalized();
            }
        }

        public Vec3 Centroid
        {
            get
            {
                if (Vertices.Count == 0) return Vec3.Zero;
                double x = 0, y = 0, z = 0;
                foreach (Vec3 v in Vertices)
                {
                    x += v.X;
                    y += v.Y;
                    z += v.Z;
                }
                int n = Vertices.Count;
                return new Vec3(x / n, y / n, z / n);
            }
        }

        /// <summary>
        /// Fan-triangulate the polygon for mesh-based operations.
        /// Assumes the polygon is convex or at least star-shaped from vertex 0.
        /// </summary>
        public List<Triangle> Triangulate()
        {
            var triangles = new List<Triangle>();
            for (int i = 1; i < Vertices.Count - 1; i++)
            {
                triangles.Add(new Triangle(Vertices[0], Vertices[i], Vertices[i + 1]));
            }
            return triangles;
        }
    }
}
