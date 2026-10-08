using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// First guess of the acoustic wall type of a detected wall, from its type name, thickness and (for IFC
    /// walls) its Pset_WallCommon.AcousticRating. The user reviews and overrides it in the walls table.
    /// </summary>
    public static class WallTypeEstimator
    {
        /// <summary>
        /// Wall type for a wall named <paramref name="typeName"/> (type name, plus material names for IFC walls).
        /// A readable <paramref name="acousticRating"/> (STC or Rw) sets the rating; the name picks the surface.
        /// </summary>
        public static WallTypeInfo Estimate(string typeName, double thicknessM, string acousticRating = null)
        {
            // Measured thicknesses (IFC shapes) come with rounding noise: 0.0999999 m is a 100 mm wall
            thicknessM = Math.Round(thicknessM, 3);
            int stc = TryParseRating(acousticRating, out int rated) ? rated : EstimateStc(typeName, thicknessM);
            // Keep the material the name gives (a concrete wall stays concrete, not the brick type nearest in STC),
            // as long as that material has a type within 3 dB of the rating; else the rating decides
            WallAbsorptionPreset? surface = SurfaceFromName(typeName);
            if (surface.HasValue)
            {
                WallTypeInfo same = FindClosest(stc, surface.Value);
                if (same.Surface == surface.Value && Math.Abs(same.StcRating - stc) <= 3) return same;
            }
            return WallTypeCatalog.FindClosestByStc(stc);
        }

        /// <summary>
        /// The sound insulation number in an AcousticRating value: "STC 50", "Rw 52 dB", "Rw (C;Ctr) = 48 (-1;-4)", "45".
        /// Rw and STC are treated as equal (they are within a few dB for common walls).
        /// </summary>
        public static bool TryParseRating(string value, out int rating)
        {
            rating = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            // Fire classes ("EI 60", "REI 90") are not sound insulation
            if (Regex.IsMatch(value, @"(?i)(^|[^a-z])(R?EI|EW)\s*\d")) return false;
            // A number not part of a longer one or negative ("STC-50" is fine: the dash follows letters)
            foreach (Match m in Regex.Matches(value, @"(?<![\d.])(?<![^A-Za-z]-)(?<!^-)\d{2}(?:[.,]\d+)?(?![\d])"))
            {
                double v = double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                if (v >= 10 && v <= 90) { rating = (int)Math.Round(v, MidpointRounding.AwayFromZero); return true; }
            }
            return false;
        }

        /// <summary>
        /// Whether an object type names a wall (an IfcBuildingElementProxy exported for a wall), not something on
        /// one: "Wall", "Partition wall", but not "Wall cabinet", "Wall-mounted TV" or "Drywall ceiling".
        /// </summary>
        public static bool NamesAWall(string objectType)
        {
            string n = (objectType ?? "").ToLowerInvariant();
            if (!Regex.IsMatch(n, @"(?<![a-z])walls?(?![a-z\-])")) return false;
            return !Regex.IsMatch(n, @"mount|cabinet|\btv\b|lamp|light|luminaire|socket|outlet|switch|shelf|hung|sconce|clock|heater|radiator|ceiling|floor");
        }

        /// <summary>Surface material named in a wall type / material name, if any.</summary>
        public static WallAbsorptionPreset? SurfaceFromName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("glass") || n.Contains("glaz") || n.Contains("curtain") || n.Contains("klaas")) return WallAbsorptionPreset.Glass;
            if (n.Contains("concrete") || n.Contains("beton")) return WallAbsorptionPreset.Concrete;
            if (n.Contains("brick") || n.Contains("masonry") || n.Contains("cmu") || n.Contains("block") || n.Contains("tellis") || n.Contains("plokk"))
                return WallAbsorptionPreset.Brick;
            if (n.Contains("stud") || n.Contains("gypsum") || n.Contains("drywall") || n.Contains("plasterboard") || n.Contains("kips"))
                return WallAbsorptionPreset.Drywall;
            if (n.Contains("timber") || n.Contains("wood") || n.Contains("puit")) return WallAbsorptionPreset.Wood;
            return null;
        }

        /// <summary>Catalog entry with the given surface closest to <paramref name="stc"/>; any surface when none has it.</summary>
        public static WallTypeInfo FindClosest(int stc, WallAbsorptionPreset surface)
        {
            var same = WallTypeCatalog.All.Where(w => w.Surface == surface).ToList();
            if (same.Count == 0) return WallTypeCatalog.FindClosestByStc(stc);
            return same.OrderBy(w => Math.Abs(w.StcRating - stc)).First();
        }

        /// <summary>
        /// Estimate STC from wall type name keywords and physical thickness.
        /// </summary>
        public static int EstimateStc(string typeName, double thicknessM)
        {
            string n = (typeName ?? "").ToLowerInvariant();

            bool isConcrete = n.Contains("concrete") || n.Contains("beton");
            bool isMasonry  = n.Contains("brick") || n.Contains("masonry") ||
                              n.Contains("cmu")   || n.Contains("block");
            bool isStud     = n.Contains("stud")  || n.Contains("timber") ||
                              n.Contains("gypsum")|| n.Contains("drywall") ||
                              n.Contains("plasterboard");
            bool isGlass    = n.Contains("glass") || n.Contains("glaz") ||
                              n.Contains("curtain");

            if (isGlass)    return 32;

            if (isConcrete)
            {
                if (thicknessM >= 0.25) return 57;
                if (thicknessM >= 0.18) return 55;
                if (thicknessM >= 0.13) return 50;
                return 45;
            }

            if (isMasonry)
            {
                if (thicknessM >= 0.25) return 55;
                if (thicknessM >= 0.18) return 50;
                return 45;
            }

            if (isStud)
            {
                if (thicknessM >= 0.15) return 45;
                if (thicknessM >= 0.10) return 40;
                return 35;
            }

            // Generic: thickness only
            if (thicknessM >= 0.30) return 55;
            if (thicknessM >= 0.20) return 50;
            if (thicknessM >= 0.15) return 45;
            if (thicknessM >= 0.10) return 40;
            if (thicknessM >= 0.07) return 35;
            return 30;
        }
    }
}
