using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Creates the appropriate ISpeakerDirectivityProvider from a profile mapping.
    /// </summary>
    public static class DirectivityProviderFactory
    {
        public static ISpeakerDirectivityProvider Create(SpeakerProfileMapping mapping)
        {
            if (mapping == null)
                return new SimpleOmniProvider();

            switch (mapping.ProfileSource)
            {
                case ProfileSourceType.SimpleOmni:
                    return new SimpleOmniProvider(mapping.OnAxisSplDb);

                case ProfileSourceType.SimpleConical:
                    return new SimpleConeProvider(
                        mapping.OnAxisSplDb,
                        mapping.ConeHalfAngleDeg,
                        mapping.OffAxisAttenuationDb);

                case ProfileSourceType.GllFile:
                    return new GllStubProvider(mapping.OnAxisSplDb, mapping.GllFilePath);

                default:
                    return new SimpleOmniProvider(mapping.OnAxisSplDb);
            }
        }
    }
}
