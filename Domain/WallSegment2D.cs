namespace SoundCalcs.Domain
{
    /// <summary>
    /// A 2D wall centerline segment extracted from a linked Revit model.
    /// Coordinates are in meters, projected onto the XY plane.
    /// </summary>
    public class WallSegment2D
    {
        /// <summary>Start point of the wall centerline in meters (XY).</summary>
        public Vec2 Start { get; set; }

        /// <summary>End point of the wall centerline in meters (XY).</summary>
        public Vec2 End { get; set; }

        /// <summary>Floor elevation of this wall in meters.</summary>
        public double BaseElevationM { get; set; }

        /// <summary>Wall height in meters.</summary>
        public double HeightM { get; set; }

        /// <summary>Wall thickness in meters (for boundary offset).</summary>
        public double ThicknessM { get; set; }

        public double Length => Vec2.Distance(Start, End);

        public override string ToString() => $"Wall {Start} → {End} (len={Length:F2}m)";
    }
}
