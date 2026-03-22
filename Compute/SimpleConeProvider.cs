using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Conical speaker: full gain inside the cone, attenuated outside.
    /// Uses a smooth cosine-power rolloff at the cone boundary.
    /// </summary>
    public class SimpleConeProvider : ISpeakerDirectivityProvider
    {
        public double OnAxisSplAtOneMeter { get; }

        private readonly double _coneHalfAngleRad;
        private readonly double _offAxisLinearGain;

        /// <param name="onAxisSplDb">On-axis SPL at 1m in dB.</param>
        /// <param name="coneHalfAngleDeg">Half-angle of coverage cone in degrees.</param>
        /// <param name="offAxisAttenuationDb">Attenuation outside cone in dB (negative, e.g. -12).</param>
        public SimpleConeProvider(double onAxisSplDb = 90.0, double coneHalfAngleDeg = 60.0, double offAxisAttenuationDb = -12.0)
        {
            OnAxisSplAtOneMeter = onAxisSplDb;
            _coneHalfAngleRad = coneHalfAngleDeg * Math.PI / 180.0;
            // Convert dB attenuation to linear gain: 10^(dB/20)
            _offAxisLinearGain = Math.Pow(10.0, offAxisAttenuationDb / 20.0);
        }

        public double GetDirectivityGain(Vec3 facingDirection, Vec3 toReceiver)
        {
            double cosAngle = Vec3.Dot(facingDirection, toReceiver);
            // Clamp for numerical safety
            cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));
            double angle = Math.Acos(cosAngle);

            if (angle <= _coneHalfAngleRad)
            {
                // Inside cone: full gain with slight cosine rolloff for realism
                double t = angle / _coneHalfAngleRad; // 0 at center, 1 at edge
                return 1.0 - t * t * (1.0 - _offAxisLinearGain) * 0.1; // gentle rolloff
            }
            else
            {
                // Outside cone: apply off-axis attenuation with smooth transition
                double overshoot = (angle - _coneHalfAngleRad) / (Math.PI - _coneHalfAngleRad);
                overshoot = Math.Min(overshoot, 1.0);
                // Interpolate from edge gain down to off-axis floor
                double edgeGain = 1.0 - (1.0 - _offAxisLinearGain) * 0.1;
                return edgeGain + (_offAxisLinearGain - edgeGain) * overshoot;
            }
        }
    }
}
