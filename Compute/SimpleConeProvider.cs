using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Conical speaker: -6 dB at the rated coverage angle edge,
    /// cosine-power rolloff beyond to off-axis floor.
    /// Supports frequency-dependent beaming: higher bands use a narrower cone.
    /// </summary>
    public class SimpleConeProvider : ISpeakerDirectivityProvider
    {
        public double OnAxisSplAtOneMeter { get; }
        public double DirectivityFactor { get; }

        private readonly double _coneHalfAngleRad;
        private readonly double _offAxisLinearGain;
        private readonly double _n;

        // Reference frequency for directivity scaling (cone angle specified at 1 kHz)
        private const double RefFreqHz = 1000.0;

        /// <param name="onAxisSplDb">On-axis SPL at 1m in dB.</param>
        /// <param name="coneHalfAngleDeg">Half-angle of coverage cone in degrees (specified at 1 kHz).</param>
        /// <param name="offAxisAttenuationDb">Attenuation outside cone in dB (negative, e.g. -12).</param>
        public SimpleConeProvider(double onAxisSplDb = 90.0, double coneHalfAngleDeg = 60.0, double offAxisAttenuationDb = -12.0)
        {
            OnAxisSplAtOneMeter = onAxisSplDb;
            _coneHalfAngleRad = Math.Max(coneHalfAngleDeg, 1.0) * Math.PI / 180.0;
            _offAxisLinearGain = Math.Pow(10.0, offAxisAttenuationDb / 20.0);

            double cosHalf = Math.Cos(_coneHalfAngleRad);
            _n = (cosHalf > 0.0001 && cosHalf < 0.9999)
                ? Math.Log(0.5) / Math.Log(cosHalf)
                : 2.0;

            double denom = 1.0 - cosHalf;
            DirectivityFactor = denom > 1e-6 ? 2.0 / denom : 1.0;
        }

        public double GetDirectivityGain(Vec3 facingDirection, Vec3 toReceiver)
        {
            return ComputeGain(facingDirection, toReceiver, _n);
        }

        public double GetDirectivityGainForBand(Vec3 facingDirection, Vec3 toReceiver, int bandIndex)
        {
            // Scale the cosine-power exponent by frequency ratio.
            // Higher frequencies beam more narrowly → larger exponent → faster rolloff.
            double freqRatio = OctaveBands.CenterFrequencies[bandIndex] / RefFreqHz;
            double nBand = _n * freqRatio;
            return ComputeGain(facingDirection, toReceiver, nBand);
        }

        private double ComputeGain(Vec3 facingDirection, Vec3 toReceiver, double n)
        {
            double cosAngle = Vec3.Dot(facingDirection, toReceiver);
            cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));

            if (cosAngle <= 0)
                return _offAxisLinearGain;

            double gain = Math.Pow(cosAngle, n);

            double edgeGain = 0.5;
            if (gain < edgeGain)
            {
                double t = (edgeGain - gain) / edgeGain;
                t = Math.Min(t, 1.0);
                gain = edgeGain * (1.0 - t) + _offAxisLinearGain * t;
            }

            return Math.Max(gain, _offAxisLinearGain);
        }
    }
}
