using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Directivity from a measured polar table (rotationally symmetric about the facing axis),
    /// per octave band. On-axis level is the profile's SPL; the table gives the attenuation.
    /// </summary>
    public class PolarTableProvider : ISpeakerDirectivityProvider
    {
        private readonly PolarTable _table;

        public double OnAxisSplAtOneMeter { get; }

        /// <summary>Q at 1 kHz: 2 / ∫₀^π g²(θ)·sin θ dθ (axisymmetric).</summary>
        public double DirectivityFactor { get; }

        public PolarTableProvider(double onAxisSplDb, PolarTable table)
        {
            OnAxisSplAtOneMeter = onAxisSplDb;
            _table = table;
            DirectivityFactor = ComputeQ(table, 3);
        }

        public double GetDirectivityGain(Vec3 facingDirection, Vec3 toReceiver) =>
            GetDirectivityGainForBand(facingDirection, toReceiver, 3);

        public double GetDirectivityGainForBand(Vec3 facingDirection, Vec3 toReceiver, int bandIndex)
        {
            double cos = Math.Max(-1.0, Math.Min(1.0, Vec3.Dot(facingDirection, toReceiver)));
            double angleDeg = Math.Acos(cos) * 180.0 / Math.PI;
            return Math.Pow(10.0, _table.AttenuationAt(bandIndex, angleDeg) / 20.0);
        }

        internal static double ComputeQ(PolarTable table, int band)
        {
            const int steps = 720;
            double integral = 0;
            for (int i = 0; i < steps; i++)
            {
                double theta = (i + 0.5) * Math.PI / steps;
                double g2 = Math.Pow(10.0, table.AttenuationAt(band, theta * 180.0 / Math.PI) / 10.0);
                integral += g2 * Math.Sin(theta) * Math.PI / steps;
            }
            return integral > 1e-9 ? 2.0 / integral : 1.0;
        }
    }
}
