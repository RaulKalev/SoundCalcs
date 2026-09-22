using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Conical speaker: -6 dB at the rated coverage angle edge,
    /// cosine-power rolloff beyond to off-axis floor.
    /// Supports frequency-dependent beaming: higher bands use a narrower cone, and
    /// bands below 1 kHz radiate more evenly all round (a shallower off-axis floor,
    /// scaled by f/1 kHz), so low frequencies don't drop abruptly behind the speaker.
    /// </summary>
    public class SimpleConeProvider : ISpeakerDirectivityProvider
    {
        public double OnAxisSplAtOneMeter { get; }
        public double DirectivityFactor { get; private set; }

        private readonly double _coneHalfAngleRad;
        private readonly double _offAxisLinearGain;
        private readonly double _offAxisDb;
        private readonly double _n;

        // Reference frequency for directivity scaling (cone angle specified at 1 kHz)
        private const double RefFreqHz = 1000.0;

        /// <param name="onAxisSplDb">On-axis SPL at 1m in dB.</param>
        /// <param name="coneHalfAngleDeg">Half-angle of coverage cone in degrees (specified at 1 kHz).</param>
        /// <param name="offAxisAttenuationDb">Attenuation outside cone in dB (negative, e.g. -12).</param>
        // Optional per-band cosine exponents from datasheet coverage angles (null = scale with f)
        private readonly double[] _nByBand;
        private readonly bool[] _omniBand;

        /// <param name="coverageFullDegByBand">Optional −6 dB full coverage angle per octave band
        /// (7 values); ≥ 180° makes that band omnidirectional. Overrides the frequency scaling.</param>
        public SimpleConeProvider(double onAxisSplDb, double coneHalfAngleDeg, double offAxisAttenuationDb,
            double[] coverageFullDegByBand)
            : this(onAxisSplDb,
                   coverageFullDegByBand != null && coverageFullDegByBand.Length == OctaveBands.Count
                       ? Math.Min(coverageFullDegByBand[3] / 2.0, 89.0) : coneHalfAngleDeg,
                   offAxisAttenuationDb)
        {
            if (coverageFullDegByBand == null || coverageFullDegByBand.Length != OctaveBands.Count) return;
            _nByBand = new double[OctaveBands.Count];
            _omniBand = new bool[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                double half = coverageFullDegByBand[k] / 2.0;
                if (half >= 89.5) { _omniBand[k] = true; continue; }
                double cosHalf = Math.Cos(Math.Max(half, 1.0) * Math.PI / 180.0);
                _nByBand[k] = Math.Log(0.5) / Math.Log(cosHalf);
            }
            if (coverageFullDegByBand[3] >= 179) DirectivityFactor = 1.0;
        }

        public SimpleConeProvider(double onAxisSplDb = 90.0, double coneHalfAngleDeg = 60.0, double offAxisAttenuationDb = -12.0)
        {
            OnAxisSplAtOneMeter = onAxisSplDb;
            _coneHalfAngleRad = Math.Max(coneHalfAngleDeg, 1.0) * Math.PI / 180.0;
            _offAxisDb = Math.Min(0.0, offAxisAttenuationDb);
            _offAxisLinearGain = Math.Pow(10.0, _offAxisDb / 20.0);

            double cosHalf = Math.Cos(_coneHalfAngleRad);
            _n = (cosHalf > 0.0001 && cosHalf < 0.9999)
                ? Math.Log(0.5) / Math.Log(cosHalf)
                : 2.0;

            double denom = 1.0 - cosHalf;
            DirectivityFactor = denom > 1e-6 ? 2.0 / denom : 1.0;
        }

        public double GetDirectivityGain(Vec3 facingDirection, Vec3 toReceiver)
        {
            return ComputeGain(facingDirection, toReceiver, _n, _offAxisLinearGain);
        }

        public double GetDirectivityGainForBand(Vec3 facingDirection, Vec3 toReceiver, int bandIndex)
        {
            double freqRatio = OctaveBands.CenterFrequencies[bandIndex] / RefFreqHz;
            if (_omniBand != null && _omniBand[bandIndex]) return 1.0;
            // Datasheet coverage angles when given; otherwise scale the cosine-power exponent by
            // frequency ratio (higher frequencies beam more narrowly → faster rolloff).
            double nBand = _nByBand != null ? _nByBand[bandIndex] : _n * freqRatio;
            // Rear floor: as entered when coverage comes from data; otherwise shallower below 1 kHz
            double floor = _nByBand != null
                ? _offAxisLinearGain
                : Math.Pow(10.0, _offAxisDb * Math.Min(1.0, freqRatio) / 20.0);
            return ComputeGain(facingDirection, toReceiver, nBand, floor);
        }

        private static double ComputeGain(Vec3 facingDirection, Vec3 toReceiver, double n, double floor)
        {
            double cosAngle = Vec3.Dot(facingDirection, toReceiver);
            cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));

            if (cosAngle <= 0)
                return floor;

            double gain = Math.Pow(cosAngle, n);

            double edgeGain = 0.5;
            if (gain < edgeGain)
            {
                double t = (edgeGain - gain) / edgeGain;
                t = Math.Min(t, 1.0);
                gain = edgeGain * (1.0 - t) + floor * t;
            }

            return Math.Max(gain, floor);
        }
    }
}
