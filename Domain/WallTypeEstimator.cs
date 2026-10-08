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
                WallTypeInfo same = surface.Value == WallAbsorptionPreset.Wood
                    ? FindClosestWall(stc)
                    : FindClosest(stc, surface.Value);
                if (same.Surface == surface.Value && Math.Abs(same.StcRating - stc) <= 3) return same;
            }
            return FindClosestWall(stc);
        }

        /// <summary>The wall construction closest to <paramref name="stc"/>: never a door, opening or curtain.</summary>
        public static WallTypeInfo FindClosestWall(int stc) =>
            WallTypeCatalog.All
                .Where(w => !w.Key.StartsWith("door_") && w.Surface != WallAbsorptionPreset.Open && w.Surface != WallAbsorptionPreset.Curtain)
                .OrderBy(w => Math.Abs(w.StcRating - stc)).First();

        /// <summary>
        /// The thickness a wall type name states as its last number in millimetres, as Revit writes them
        /// ("SS-01 150", "VS-01-VÄLINE OSA 313 (IFC)"); null when there is none between 50 and 1000 mm.
        /// </summary>
        public static double? NominalThicknessM(string typeName)
        {
            Match m = Regex.Match(typeName ?? "", @"(?<![\d.,x×*])(\d{2,4})\s*(mm)?\s*(\(IFC\))?\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            double mm = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return mm >= 50 && mm <= 1000 ? mm / 1000 : (double?)null;
        }

        /// <summary>
        /// Type for a door (<paramref name="isDoor"/>) or window named <paramref name="name"/>: a readable
        /// <paramref name="acousticRating"/> sets the rating; else glazed doors and curtain panels count as glass,
        /// other doors as solid core doors, windows as double glazing.
        /// </summary>
        public static WallTypeInfo EstimateOpening(string name, bool isDoor, string acousticRating = null)
        {
            string n = Clean(name);
            bool glass = n.Contains("glass") || n.Contains("glaz") || n.Contains("klaas") || n.Contains("curtain") || n.Contains("panel");
            if (TryParseRating(acousticRating, out int rated))
                return FindClosest(rated, isDoor && !glass ? WallAbsorptionPreset.Wood : WallAbsorptionPreset.Glass);
            // A glass door seals worse than the glazing around it
            if (isDoor) return WallTypeCatalog.FindByKey(glass ? "glass_double" : "door_solid");
            if (n.Contains("curtain") || n.Contains("panel") || n.Contains("fassaad") || n.Contains("facade")) return WallTypeCatalog.FindByKey("glass_curtain");
            return WallTypeCatalog.FindByKey("glass_double");
        }

        /// <summary>
        /// Whether a material is an insulation, membrane or air layer: it says nothing about what the wall is made
        /// of ("Insulation - Fiberglass", "Soojustus - Mineraalvill", "Air Space").
        /// </summary>
        public static bool IsInsulation(string material)
        {
            string n = (material ?? "").ToLowerInvariant();
            return Regex.IsMatch(n, @"insulat|soojustus|isolatsioon|wool|vill\b|mineraal|fib(er|re)\s*glass|membra|vapou?r|aurut|tuulet|air\s*space|õhkvahe|\bxps\b|\beps\b|\bpir\b|vahtpol");
        }

        // Lower case, without the words that name glass but mean insulation ("fiberglass", "glass wool")
        private static string Clean(string name) =>
            Regex.Replace((name ?? "").ToLowerInvariant(), @"fib(er|re)\s*glass|glass\s*wool|klaasvill", " ");

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
            string n = Clean(name);
            if (n.Contains("glass") || n.Contains("glaz") || n.Contains("curtain") || n.Contains("klaas")) return WallAbsorptionPreset.Glass;
            if (n.Contains("concrete") || n.Contains("beton") || n.Contains("betoon")) return WallAbsorptionPreset.Concrete;
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
            string n = Clean(typeName);

            bool isConcrete = n.Contains("concrete") || n.Contains("beton") || n.Contains("betoon");
            bool isMasonry  = n.Contains("brick") || n.Contains("masonry") ||
                              n.Contains("cmu")   || n.Contains("block") ||
                              n.Contains("plokk") || n.Contains("tellis");
            bool isStud     = n.Contains("stud")  || n.Contains("timber") ||
                              n.Contains("gypsum")|| n.Contains("drywall") ||
                              n.Contains("plasterboard") || n.Contains("kips");
            bool isGlass    = n.Contains("glass") || n.Contains("glaz") ||
                              n.Contains("curtain") || n.Contains("klaas");

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
