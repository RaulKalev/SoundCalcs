namespace SoundCalcs.Domain
{
    /// <summary>
    /// Identifies the linked Revit model used as the architectural source.
    /// </summary>
    public class LinkSelection
    {
        /// <summary>
        /// Display name of the linked model (e.g., "Arch_Model.rvt").
        /// </summary>
        public string LinkName { get; set; } = "";

        /// <summary>
        /// Revit ElementId (as int) of the RevitLinkInstance in the host model.
        /// -1 means no link selected.
        /// </summary>
        public int LinkInstanceId { get; set; } = -1;

        /// <summary>
        /// Full file path of the linked model, for display and validation.
        /// </summary>
        public string FilePath { get; set; } = "";

        /// <summary>
        /// True when the link is an IFC file (Revit links an IFC through a converted ".ifc.RVT" copy):
        /// its walls are shapes, read with <see cref="IfcWallBuilder"/>.
        /// </summary>
        public bool IsIfc { get; set; }

        /// <summary>The original .ifc file next to the converted copy, when the link is an IFC.</summary>
        public string IfcFilePath { get; set; } = "";

        /// <summary>"IFC" for IFC links (shown next to the name), else empty.</summary>
        public string KindLabel => IsIfc ? "IFC" : "";

        /// <summary>Whether a linked file path is an IFC link, and the original .ifc it was converted from.</summary>
        public static bool DetectIfc(string path, out string ifcPath)
        {
            ifcPath = "";
            string p = (path ?? "").Trim();
            if (p.EndsWith(".ifc.rvt", System.StringComparison.OrdinalIgnoreCase))
            {
                ifcPath = p.Substring(0, p.Length - 4);
                return true;
            }
            if (p.EndsWith(".ifc", System.StringComparison.OrdinalIgnoreCase))
            {
                ifcPath = p;
                return true;
            }
            return false;
        }

        public bool IsValid => LinkInstanceId > 0 && !string.IsNullOrEmpty(LinkName);
    }
}
