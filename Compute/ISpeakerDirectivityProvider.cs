using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Returns directivity attenuation for a speaker given a direction from the source.
    /// Implementations must be pure math -- no Revit references.
    /// </summary>
    public interface ISpeakerDirectivityProvider
    {
        /// <summary>
        /// Get the linear gain factor (0.0 .. 1.0) for a given direction
        /// relative to the speaker's facing axis.
        /// </summary>
        /// <param name="facingDirection">Normalized forward axis of the speaker.</param>
        /// <param name="toReceiver">Normalized direction from speaker to receiver.</param>
        /// <returns>Linear gain multiplier. 1.0 = on-axis, less for off-axis.</returns>
        double GetDirectivityGain(Vec3 facingDirection, Vec3 toReceiver);

        /// <summary>
        /// On-axis SPL at 1 meter in dB.
        /// </summary>
        double OnAxisSplAtOneMeter { get; }
    }
}
