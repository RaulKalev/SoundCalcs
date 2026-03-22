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

        public bool IsValid => LinkInstanceId > 0 && !string.IsNullOrEmpty(LinkName);
    }
}
