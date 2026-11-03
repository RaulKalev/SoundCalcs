using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace SoundCalcs.Revit
{
    /// <summary>
    /// Keeps a copy of the project's areas (the same JSON as the project file) in the Revit model, on one DataStorage
    /// element, so a colleague opening the model (or the central model) can load them. Written only when the user asks:
    /// a transaction on every edit would fill Revit's undo list and fail on read-only or borrowed elements.
    /// </summary>
    public static class ProjectModelStorage
    {
        // Stable GUID: never change after first deployment.
        static readonly Guid SchemaGuid = new Guid("B3E5C1A2-6F47-4D8E-9C21-5A7D0E3F8B64");
        const string SchemaName = "SoundCalcsProjectAreas";
        const string JsonField = "ProjectJson";
        const string SavedField = "SavedUtc";
        const string ByField = "SavedBy";

        static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation("SoundCalcs areas, report criteria and report options of this project (JSON).");
            builder.AddSimpleField(JsonField, typeof(string));
            builder.AddSimpleField(SavedField, typeof(string));
            builder.AddSimpleField(ByField, typeof(string));
            return builder.Finish();
        }

        static DataStorage Find(Document doc, Schema schema)
        {
            if (schema == null) return null;
            return new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema)?.IsValid() == true);
        }

        /// <summary>What the model holds: the JSON and when and by whom it was stored, or null when nothing is stored.</summary>
        public static (string Json, string SavedUtc, string SavedBy)? Read(Document doc)
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            DataStorage ds = Find(doc, schema);
            if (ds == null) return null;
            Entity e = ds.GetEntity(schema);
            return (e.Get<string>(JsonField), e.Get<string>(SavedField), e.Get<string>(ByField));
        }

        /// <summary>
        /// Stores <paramref name="json"/> in its own transaction. Must run on the Revit API thread. False (with the
        /// reason) when the document is read-only, the element is borrowed by someone else, or the transaction failed.
        /// </summary>
        public static bool Write(Document doc, string json, out string error)
        {
            error = null;
            if (doc.IsReadOnly) { error = "the model is read-only"; return false; }
            Schema schema = GetOrCreateSchema();
            DataStorage ds = Find(doc, schema);
            if (ds != null && doc.IsWorkshared &&
                WorksharingUtils.GetCheckoutStatus(doc, ds.Id) == CheckoutStatus.OwnedByOtherUser)
            {
                error = "the stored areas are borrowed by another user";
                return false;
            }
            using (var tx = new Transaction(doc, "Store SoundCalcs areas"))
            {
                tx.Start();
                if (ds == null) ds = DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(JsonField, json ?? "");
                entity.Set(SavedField, DateTime.UtcNow.ToString("o"));
                entity.Set(ByField, doc.Application?.Username ?? Environment.UserName);
                ds.SetEntity(entity);
                if (tx.Commit() != TransactionStatus.Committed) { error = "the change could not be saved"; return false; }
            }
            return true;
        }
    }
}
