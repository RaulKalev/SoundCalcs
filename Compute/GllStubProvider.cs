using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Stub GLL provider. Returns approximate directivity until real GLL parsing
    /// is implemented. Behaves like a wide conical pattern for MVP.
    /// </summary>
    public class GllStubProvider : ISpeakerDirectivityProvider
    {
        public double OnAxisSplAtOneMeter { get; }

        private readonly SimpleConeProvider _fallback;

        /// <param name="onAxisSplDb">On-axis SPL at 1m in dB.</param>
        /// <param name="gllFilePath">Path stored for reference; not parsed in MVP.</param>
        public GllStubProvider(double onAxisSplDb = 90.0, string gllFilePath = "")
        {
            OnAxisSplAtOneMeter = onAxisSplDb;
            // Stub: use a reasonable default cone as approximation
            _fallback = new SimpleConeProvider(onAxisSplDb, 70.0, -10.0);

            if (!string.IsNullOrEmpty(gllFilePath))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SoundCalcs] GLL stub loaded for: {gllFilePath} (real parsing not yet implemented)");
            }
        }

        public double GetDirectivityGain(Vec3 facingDirection, Vec3 toReceiver)
        {
            return _fallback.GetDirectivityGain(facingDirection, toReceiver);
        }
    }
}
