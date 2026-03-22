using Autodesk.Revit.DB;

namespace SoundCalcs.Revit
{
    /// <summary>
    /// Compatibility helpers for API differences between Revit 2024 (net48) and 2026 (net8.0).
    /// </summary>
    public static class RevitCompat
    {
        /// <summary>
        /// Get the integer value of an ElementId.
        /// Revit 2024 uses IntegerValue property; Revit 2026 uses Value property.
        /// </summary>
        public static int GetIdValue(ElementId id)
        {
#if NET48
            return id.IntegerValue;
#else
            return (int)id.Value;
#endif
        }

        /// <summary>
        /// Create an ElementId from an integer.
        /// </summary>
        public static ElementId ToElementId(int value)
        {
#if NET48
            return new ElementId(value);
#else
            return new ElementId((long)value);
#endif
        }
    }
}
