using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Omnidirectional speaker: equal radiation in all directions.
    /// </summary>
    public class SimpleOmniProvider : ISpeakerDirectivityProvider
    {
        public double OnAxisSplAtOneMeter { get; }

        public SimpleOmniProvider(double onAxisSplDb = 90.0)
        {
            OnAxisSplAtOneMeter = onAxisSplDb;
        }

        public double GetDirectivityGain(Vec3 facingDirection, Vec3 toReceiver)
        {
            // Omni: no directional attenuation
            return 1.0;
        }
    }
}
