using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using SoundCalcs.Compute;
using SoundCalcs.Domain;

namespace SoundCalcs.Tests
{
    /// <summary>Datasheet coverage angles and polar-table directivity.</summary>
    public class DirectivityDataTests
    {
        private static readonly Vec3 Down = new Vec3(0, 0, -1);

        private static Vec3 OffAxis(double deg) =>
            new Vec3(Math.Sin(deg * Math.PI / 180), 0, -Math.Cos(deg * Math.PI / 180));

        private static double Db(double gain) => 20 * Math.Log10(gain);

        private const string Table =
            "# measured, 1/1 octave\n" +
            "angle,125,250,500,1k,2k,4k,8k\n" +
            "0,0,0,0,0,0,0,0\n" +
            "30,0,0,-1,-2,-3,-4,-6\n" +
            "60,-1,-2,-4,-6,-9,-12,-15\n" +
            "90,-2,-4,-8,-12,-15,-18,-20\n" +
            "180,-3,-6,-12,-18,-20,-22,-25\n";

        [Fact]
        public void PolarTable_ParsesAndInterpolatesInDb()
        {
            var t = PolarTable.Parse(Table);
            Assert.Equal(new double[] { 0, 30, 60, 90, 180 }, t.AnglesDeg);
            Assert.Equal(-6, t.AttenuationAt(3, 60), 9);
            Assert.Equal(-4, t.AttenuationAt(3, 45), 9);     // halfway between −2 and −6
            Assert.Equal(-25, t.AttenuationAt(6, 180), 9);
            Assert.Equal(0, t.AttenuationAt(0, 0), 9);
        }

        [Theory]
        [InlineData("angle,125,250,500,1k,2k,4k\n0,0,0,0,0,0,0\n90,1,1,1,1,1,1\n")]          // missing 8k
        [InlineData("angle,125,250,500,1k,2k,4k,8k\n10,0,0,0,0,0,0,0\n90,0,0,0,0,0,0,0\n")]    // not starting at 0
        [InlineData("angle,125,250,500,1k,2k,4k,8k\n0,0,0,0,0,0,0,0\n0,0,0,0,0,0,0,0\n")]      // not increasing
        public void PolarTable_RejectsBadFiles(string text)
        {
            Assert.Throws<FormatException>(() => PolarTable.Parse(text));
        }

        [Fact]
        public void PolarTableProvider_GainFollowsTable_AndQMatchesIntegral()
        {
            var p = new PolarTableProvider(90, PolarTable.Parse(Table));
            Assert.Equal(-6, Db(p.GetDirectivityGainForBand(Down, OffAxis(60), 3)), 6);
            Assert.Equal(-22.5, Db(p.GetDirectivityGainForBand(Down, OffAxis(135), 6)), 6); // halfway 90°→180°

            // Omnidirectional table → Q = 1; hemispherical (−∞ behind) → Q = 2
            var omni = PolarTable.Parse("angle,125,250,500,1k,2k,4k,8k\n0,0,0,0,0,0,0,0\n180,0,0,0,0,0,0,0\n");
            Assert.Equal(1.0, new PolarTableProvider(90, omni).DirectivityFactor, 3);
            var half = PolarTable.Parse("angle,125,250,500,1k,2k,4k,8k\n0,0,0,0,0,0,0,0\n90,0,0,0,0,0,0,0\n90.01,-200,-200,-200,-200,-200,-200,-200\n180,-200,-200,-200,-200,-200,-200,-200\n");
            Assert.Equal(2.0, new PolarTableProvider(90, half).DirectivityFactor, 2);
        }

        [Fact]
        public void CoverageAngles_GiveMinus6dbAtHalfAngle_AndOmniAt180()
        {
            double[] coverage = { 180, 180, 140, 100, 90, 70, 60 };
            var cone = new SimpleConeProvider(90, 60, -12, coverage);
            for (int k = 2; k < 7; k++)
                Assert.Equal(-6.02, Db(cone.GetDirectivityGainForBand(Down, OffAxis(coverage[k] / 2), k)), 2);
            // 125/250 Hz omni: no loss even behind the speaker
            Assert.Equal(0, Db(cone.GetDirectivityGainForBand(Down, OffAxis(170), 0)), 6);
            Assert.Equal(0, Db(cone.GetDirectivityGainForBand(Down, OffAxis(170), 1)), 6);
        }

        [Fact]
        public void Factory_UsesPolarFile_AndFallsBackWhenUnreadable()
        {
            string path = Path.Combine(Path.GetTempPath(), $"polar_{Guid.NewGuid():N}.csv");
            File.WriteAllText(path, Table);
            try
            {
                var mapping = new SpeakerProfileMapping
                {
                    ProfileSource = ProfileSourceType.SimpleConical, OnAxisSplDb = 90, DirectivityFilePath = path
                };
                Assert.IsType<PolarTableProvider>(DirectivityProviderFactory.Create(mapping));

                mapping.DirectivityFilePath = path + ".missing";
                Assert.IsType<SimpleConeProvider>(DirectivityProviderFactory.Create(mapping));

                mapping.ProfileSource = ProfileSourceType.SimpleOmni;          // omni ignores the file
                mapping.DirectivityFilePath = path;
                Assert.IsType<SimpleOmniProvider>(DirectivityProviderFactory.Create(mapping));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PolarTableSampledFromConeModel_ReproducesTheConeModel()
        {
            // A table sampled every 2.5° from the built-in cone model must give the same gains.
            var cone = new SimpleConeProvider(90, 50, -12);
            var sb = new StringBuilder("angle,125,250,500,1k,2k,4k,8k\n");
            for (double a = 0; a <= 180.0001; a += 2.5)
                sb.Append(a.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(string.Join(",", Enumerable.Range(0, 7).Select(k =>
                      Db(cone.GetDirectivityGainForBand(Down, OffAxis(a), k)).ToString("R", System.Globalization.CultureInfo.InvariantCulture))))
                  .Append('\n');
            var table = new PolarTableProvider(90, PolarTable.Parse(sb.ToString()));
            for (double a = 1; a < 180; a += 7)
                for (int k = 0; k < 7; k++)
                    Assert.InRange(Db(table.GetDirectivityGainForBand(Down, OffAxis(a), k)) -
                                   Db(cone.GetDirectivityGainForBand(Down, OffAxis(a), k)), -0.35, 0.35);
        }
    }
}
