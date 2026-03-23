using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace SoundCalcs.Revit
{
    /// <summary>
    /// Persists the user-defined horizontal aim angle for each speaker in Revit
    /// ExtensibleStorage so that rotations set in the acoustic preview survive
    /// document save/reload and plugin restarts.
    ///
    /// The angle is in degrees: 0 = East (+X), 90 = North (+Y), measured CCW.
    /// </summary>
    public static class SpeakerRotationStorage
    {
        // Stable GUID — never change after first deployment to a project.
        static readonly Guid   SchemaGuid = new Guid("3F8A1B2C-D4E5-4F60-9A7B-C8D9E0F12345");
        const string SchemaName = "SoundCalcsSpeakerAim";
        const string FieldName  = "AimAngleDeg";

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
        /// Write the aim angle (degrees) to the speaker element in a new transaction.
        /// Must be called on the Revit API thread inside an active document context.
        /// </summary>
        public static void Write(Document doc, int elementId, double angleDeg)
        {
            Element elem = doc.GetElement(RevitCompat.ToElementId(elementId));
            if (elem == null) return;

            Schema schema  = GetOrCreateSchema();
            Entity entity  = new Entity(schema);
            entity.Set(FieldName, angleDeg);

            using (var tx = new Transaction(doc, "Set Speaker Aim Angle"))
            {
                tx.Start();
                elem.SetEntity(entity);
                tx.Commit();
            }
        }

        /// <summary>
        /// Try to read the stored aim angle from the given element.
        /// Returns false when no angle has been stored.
        /// </summary>
        public static bool TryRead(Element elem, out double angleDeg)
        {
            angleDeg = 0;
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return false;

            Entity entity = elem.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return false;

            angleDeg = entity.Get<double>(FieldName);
            return true;
        }
    }
}
