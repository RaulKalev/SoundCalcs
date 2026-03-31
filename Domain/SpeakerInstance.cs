namespace SoundCalcs.Domain
{
    /// <summary>
    /// A single speaker instance extracted from the Revit host model.
    /// Positions and orientations are in meters (converted from Revit feet).
    /// </summary>
    public class SpeakerInstance
    {
        /// <summary>
        /// Revit ElementId as integer for round-tripping.
        /// </summary>
        public int ElementId { get; set; }

        /// <summary>
        /// Composite key: "FamilyName : TypeName"
        /// </summary>
        public string TypeKey { get; set; }

        /// <summary>
        /// Position in meters.
        /// </summary>
        public Vec3 Position { get; set; }

        /// <summary>
        /// Forward-facing direction (normalized). Defaults to -Z (downward).
        /// </summary>
        public Vec3 FacingDirection { get; set; } = new Vec3(0, 0, -1);

        /// <summary>
        /// Level name for display purposes.
        /// </summary>
        public string LevelName { get; set; } = "";

        /// <summary>
        /// Elevation of the assigned level in meters (e.g. Level 1 = 0, Level 2 = 3.5).
        /// </summary>
        public double LevelElevationM { get; set; }

        /// <summary>
        /// Speaker height above its assigned level in meters (Position.Z - LevelElevationM).
        /// </summary>
        public double ElevationFromLevelM { get; set; }

        /// <summary>
        /// A/B line designation read from the configured Revit parameter (e.g. "A", "B").
        /// Empty string when the parameter is not configured or not set on the element.
        /// </summary>
        public string AbLine { get; set; } = "";

        public override string ToString() => $"{TypeKey} @ {Position} [Id={ElementId}]";
    }
}
