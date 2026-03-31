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

        /// <summary>
        /// Index into the Rooms list identifying which room contains this receiver.
        /// -1 means the receiver is not inside any detected room.
        /// </summary>
        public int RoomIndex { get; set; } = -1;

        public ReceiverPoint() { }

        public ReceiverPoint(Vec3 position, int index)
        {
            Position = position;
            Index = index;
        }
    }
}
