namespace SoundCalcs.Domain
{
    /// <summary>
    /// A single receiver position in the analysis grid.
    /// Position is in meters.
    /// </summary>
    public class ReceiverPoint
    {
        public Vec3 Position { get; set; }

        /// <summary>
        /// Sequential index within the grid, used for result mapping.
        /// </summary>
        public int Index { get; set; }

        public ReceiverPoint() { }

        public ReceiverPoint(Vec3 position, int index)
        {
            Position = position;
            Index = index;
        }
    }
}
