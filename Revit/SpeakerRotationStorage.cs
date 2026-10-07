using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SoundCalcs.Domain;

namespace SoundCalcs.Revit
{
    /// <summary>
    /// Persists the user's horizontal aim correction for each speaker in Revit ExtensibleStorage so that
    /// aims set in the acoustic preview survive document save/reload, plugin restarts and moving the speaker.
    ///
    /// The correction is stored in degrees relative to the family's own facing (see
    /// <see cref="SpeakerInstance.AimOffsetDeg"/>): a speaker moved to another wall turns with its family
    /// and keeps the correction. Speakers aimed by earlier versions carry an absolute angle
    /// (0 = East (+X), 90 = North (+Y), CCW), which is read as a correction from the current facing.
    /// </summary>
    public static class SpeakerRotationStorage
    {
        // Stable GUIDs — never change after first deployment to a project.
        static readonly Guid   LegacySchemaGuid = new Guid("3F8A1B2C-D4E5-4F60-9A7B-C8D9E0F12345");
        const string LegacyFieldName = "AimAngleDeg";

        static readonly Guid   SchemaGuid = new Guid("7C2E4A91-3B5D-4E8F-A1C6-D09B2F7E5A34");
        const string SchemaName = "SoundCalcsSpeakerAimOffset";
        const string FieldName  = "AimOffsetDeg";

        static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldName, typeof(double));
            return builder.Finish();
        }

        /// <summary>
        /// Store the correction that aims the speaker at <paramref name="aimDeg"/> (absolute, degrees) in a
        /// new transaction. Must be called on the Revit API thread inside an active document context.
        /// </summary>
        public static void Write(Document doc, int elementId, double aimDeg)
        {
            Element elem = doc.GetElement(RevitCompat.ToElementId(elementId));
            if (elem == null) return;

            double? modelAim = ModelAimDeg(elem);
            Schema schema  = GetOrCreateSchema();
            Entity entity  = new Entity(schema);
            entity.Set(FieldName, SpeakerAim.OffsetFor(aimDeg, modelAim));

            using (var tx = new Transaction(doc, "Set Speaker Aim Angle"))
            {
                tx.Start();
                elem.SetEntity(entity);
                // The absolute angle of earlier versions would no longer follow the family.
                Schema legacy = Schema.Lookup(LegacySchemaGuid);
                if (legacy != null && elem.GetEntity(legacy)?.IsValid() == true)
                    elem.DeleteEntity(legacy);
                tx.Commit();
            }
        }

        /// <summary>
        /// Try to read the stored aim correction from the given element, relative to
        /// <paramref name="modelAimDeg"/> (the family's current horizontal facing).
        /// Returns false when no aim has been stored.
        /// </summary>
        public static bool TryReadOffset(Element elem, double? modelAimDeg, out double offsetDeg)
        {
            offsetDeg = 0;

            Schema schema = Schema.Lookup(SchemaGuid);
            Entity entity = schema != null ? elem.GetEntity(schema) : null;
            if (entity != null && entity.IsValid())
            {
                offsetDeg = entity.Get<double>(FieldName);
                return true;
            }

            Schema legacy = Schema.Lookup(LegacySchemaGuid);
            Entity legacyEntity = legacy != null ? elem.GetEntity(legacy) : null;
            if (legacyEntity != null && legacyEntity.IsValid())
            {
                offsetDeg = SpeakerAim.OffsetFor(legacyEntity.Get<double>(LegacyFieldName), modelAimDeg);
                return true;
            }
            return false;
        }

        /// <summary>Horizontal facing of the family in degrees, or null when it faces up or down.</summary>
        public static double? ModelAimDeg(Element elem)
        {
            if (!(elem is FamilyInstance fi)) return null;
            return SpeakerAim.HorizontalAngleDeg(UnitConversion.DirectionToVec3(fi.FacingOrientation));
        }
    }
}
