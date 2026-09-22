using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Creates the appropriate ISpeakerDirectivityProvider from a profile mapping.
    /// </summary>
    public static class DirectivityProviderFactory
    {
        /// <summary>
        /// Polar-table provider when the mapping names a readable directivity file, else null
        /// (an unreadable file is logged and the cone / coverage-angle model is used).
        /// </summary>
        private static ISpeakerDirectivityProvider FromMeasuredData(SpeakerProfileMapping mapping)
        {
            string path = mapping.DirectivityFilePath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return new PolarTableProvider(mapping.OnAxisSplDb, PolarTable.Load(path));
            }
            catch (Exception ex)
            {
                IO.FileLogger.Log($"[Directivity] '{mapping.TypeKey}': cannot use '{path}' ({ex.Message}); using the cone model.");
                return null;
            }
        }

        public static ISpeakerDirectivityProvider Create(SpeakerProfileMapping mapping)
        {
            if (mapping == null)
                return new SimpleOmniProvider();

            switch (mapping.ProfileSource)
            {
                case ProfileSourceType.SimpleOmni:
                    return new SimpleOmniProvider(mapping.OnAxisSplDb);

                case ProfileSourceType.SimpleConical:
                case ProfileSourceType.WallMounted:
                    return FromMeasuredData(mapping) ?? new SimpleConeProvider(
                        mapping.OnAxisSplDb,
                        mapping.ConeHalfAngleDeg,
                        mapping.OffAxisAttenuationDb,
                        mapping.CoverageAngleByBandDeg);

                case ProfileSourceType.GllFile:
                    return new GllStubProvider(mapping.OnAxisSplDb, mapping.GllFilePath);

                default:
                    return new SimpleOmniProvider(mapping.OnAxisSplDb);
            }
        }
    }
}
