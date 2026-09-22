using System.Collections.Generic;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>A selectable room-surface finish (floor or ceiling) and its absorption preset.</summary>
    public class SurfaceMaterialInfo
    {
        public WallAbsorptionPreset Preset { get; }
        public string DisplayName { get; }

        /// <summary>Per-octave-band absorption coefficients (125 Hz – 8 kHz).</summary>
        public double[] AbsorptionByBand => OctaveBands.AbsorptionPresets[Preset];

        public SurfaceMaterialInfo(WallAbsorptionPreset preset, string displayName)
        {
            Preset = preset;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
        public override bool Equals(object obj) => obj is SurfaceMaterialInfo other && Preset == other.Preset;
        public override int GetHashCode() => Preset.GetHashCode();
    }

    /// <summary>Floor and ceiling finishes offered in the Grid tab.</summary>
    public static class SurfaceMaterialCatalog
    {
        public const WallAbsorptionPreset DefaultFloor = WallAbsorptionPreset.Concrete;
        public const WallAbsorptionPreset DefaultCeiling = WallAbsorptionPreset.Drywall;

        public static readonly List<SurfaceMaterialInfo> FloorOptions = new List<SurfaceMaterialInfo>
        {
            new SurfaceMaterialInfo(WallAbsorptionPreset.Concrete, "Hard floor (concrete / tile / vinyl)"),
            new SurfaceMaterialInfo(WallAbsorptionPreset.Wood,     "Wood / parquet"),
            new SurfaceMaterialInfo(WallAbsorptionPreset.Carpet,   "Carpet"),
        };

        public static readonly List<SurfaceMaterialInfo> CeilingOptions = new List<SurfaceMaterialInfo>
        {
            new SurfaceMaterialInfo(WallAbsorptionPreset.Drywall,       "Plasterboard / gypsum"),
            new SurfaceMaterialInfo(WallAbsorptionPreset.Concrete,      "Exposed concrete"),
            new SurfaceMaterialInfo(WallAbsorptionPreset.Wood,          "Wood panelling"),
            new SurfaceMaterialInfo(WallAbsorptionPreset.AcousticTile,  "Acoustic ceiling tiles"),
            new SurfaceMaterialInfo(WallAbsorptionPreset.AcousticPanel, "Acoustic panels / absorbers"),
        };

        /// <summary>The option for <paramref name="preset"/>, or the list's first entry.</summary>
        public static SurfaceMaterialInfo Find(List<SurfaceMaterialInfo> options, WallAbsorptionPreset preset) =>
            options.FirstOrDefault(o => o.Preset == preset) ?? options[0];
    }
}
