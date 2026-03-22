using System.Collections.Generic;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Speakers grouped by FamilyType. Used for the mapping UI to show
    /// one row per type with instance count and profile controls.
    /// </summary>
    public class SpeakerTypeGroup
    {
        /// <summary>
        /// Composite key: "FamilyName : TypeName"
        /// </summary>
        public string TypeKey { get; set; }

        public string FamilyName { get; set; }
        public string TypeName { get; set; }

        /// <summary>
        /// All instances of this type.
        /// </summary>
        public List<SpeakerInstance> Instances { get; set; } = new List<SpeakerInstance>();

        public int Count => Instances.Count;

        /// <summary>
        /// ElementId of the first instance, for quick selection in Revit.
        /// </summary>
        public int SampleElementId => Instances.Count > 0 ? Instances[0].ElementId : -1;

        /// <summary>
        /// Profile mapping for this speaker type.
        /// </summary>
        public SpeakerProfileMapping Mapping { get; set; } = new SpeakerProfileMapping();
    }
}
