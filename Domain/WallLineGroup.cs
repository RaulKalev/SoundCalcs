using System;
using System.Collections.Generic;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Describes an acoustic wall type with its STC (Sound Transmission Class) rating.
    /// </summary>
    public class WallTypeInfo
    {
        public string Key { get; }
        public string DisplayName { get; }
        public int StcRating { get; }

        public WallTypeInfo(string key, string displayName, int stcRating)
        {
            Key = key;
            DisplayName = displayName;
            StcRating = stcRating;
        }

        public override string ToString() => DisplayName;
        public override bool Equals(object obj) => obj is WallTypeInfo other && Key == other.Key;
        public override int GetHashCode() => Key.GetHashCode();
    }

    /// <summary>
    /// Catalogue of common wall types with STC ratings.
    /// </summary>
    public static class WallTypeCatalog
    {
        public static readonly List<WallTypeInfo> All = new List<WallTypeInfo>
        {
            new WallTypeInfo("concrete_200",     "200mm Concrete — STC 55",               55),
            new WallTypeInfo("concrete_150",     "150mm Concrete — STC 50",               50),
            new WallTypeInfo("cmu_200",          "200mm CMU Block — STC 50",              50),
            new WallTypeInfo("cmu_200_plaster",  "200mm CMU + Plaster — STC 55",          55),
            new WallTypeInfo("brick_230",        "230mm Brick — STC 52",                  52),
            new WallTypeInfo("brick_double",     "Double Brick + Cavity — STC 58",        58),
            new WallTypeInfo("stud_single",      "Single Stud 1×GWB — STC 35",           35),
            new WallTypeInfo("stud_single_2gwb", "Single Stud 2×GWB — STC 40",           40),
            new WallTypeInfo("stud_insulated",   "Single Stud Insulated 1×GWB — STC 42", 42),
            new WallTypeInfo("stud_insul_2gwb",  "Single Stud Insulated 2×GWB — STC 50", 50),
            new WallTypeInfo("stud_double",      "Double Stud 2×GWB — STC 55",           55),
            new WallTypeInfo("stud_staggered",   "Staggered Stud Insulated — STC 52",    52),
            new WallTypeInfo("metal_stud",       "Metal Stud 1×GWB — STC 38",            38),
            new WallTypeInfo("metal_stud_2gwb",  "Metal Stud 2×GWB — STC 45",            45),
            new WallTypeInfo("metal_insul_2gwb", "Metal Stud Insulated 2×GWB — STC 52",  52),
            new WallTypeInfo("glass_single",     "Single Glazing 6mm — STC 28",          28),
            new WallTypeInfo("glass_double",     "Double Glazing — STC 33",               33),
            new WallTypeInfo("glass_laminated",  "Laminated Glass 10mm — STC 36",         36),
            new WallTypeInfo("glass_curtain",    "Curtain Wall / IGU — STC 38",          38),
            new WallTypeInfo("glass_acoustic",   "Acoustic Glass — STC 42",              42),
            new WallTypeInfo("door_hollow",      "Hollow Core Door — STC 20",             20),
            new WallTypeInfo("door_solid",       "Solid Core Door — STC 30",              30),
            new WallTypeInfo("door_acoustic",    "Acoustic Door — STC 40",                40),
            new WallTypeInfo("partition_movable","Movable Partition — STC 42",            42),
            new WallTypeInfo("curtain_fabric",   "Fabric Curtain / Drape — STC 10",      10),
            new WallTypeInfo("open",             "Open (No Wall) — STC 0",                 0),
        };

        public static WallTypeInfo Default => All[0];

        public static WallTypeInfo FindByKey(string key)
        {
            foreach (var w in All)
                if (w.Key == key) return w;
            return Default;
        }

        /// <summary>
        /// Returns the catalog entry whose STC rating is closest to <paramref name="stc"/>.
        /// </summary>
        public static WallTypeInfo FindClosestByStc(int stc)
        {
            WallTypeInfo best = All[0];
            int bestDiff = int.MaxValue;
            foreach (var w in All)
            {
                int diff = Math.Abs(w.StcRating - stc);
                if (diff < bestDiff) { bestDiff = diff; best = w; }
            }
            return best;
        }
    }

    /// <summary>
    /// Groups detail lines by their Revit line style, allowing the user
    /// to assign an acoustic wall type per line style.
    /// </summary>
    public class WallLineGroup
    {
        /// <summary>Name of the Revit line style (e.g. "Lines", "Wide Lines", custom styles).</summary>
        public string LineStyleName { get; set; } = "";

        /// <summary>Number of detail line segments with this style.</summary>
        public int SegmentCount { get; set; }

        /// <summary>Total length of segments in meters.</summary>
        public double TotalLengthM { get; set; }

        /// <summary>Assigned acoustic wall type.</summary>
        public WallTypeInfo WallType { get; set; } = WallTypeCatalog.Default;

        /// <summary>The wall segments belonging to this group.</summary>
        public List<WallSegment2D> Segments { get; set; } = new List<WallSegment2D>();
    }
}
