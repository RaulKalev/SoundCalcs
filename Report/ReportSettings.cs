using System;
using System.Collections.Generic;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.Report
{
    /// <summary>Language of the report text.</summary>
    public enum ReportLanguage
    {
        English,
        Estonian
    }

    /// <summary>Paper size; pages are always portrait.</summary>
    public enum ReportPageFormat
    {
        A4,
        A3
    }

    /// <summary>Who and what the report is for: the cover page and the page header. Stored with the project.</summary>
    public class ProjectInfo
    {
        public string ProjectName { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string Client { get; set; } = "";
        public string Company { get; set; } = "";
        public string Author { get; set; } = "";
        public string CheckedBy { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>PNG or JPEG drawn on the cover; empty = none.</summary>
        public string LogoPath { get; set; } = "";

        public ProjectInfo Clone() => (ProjectInfo)MemberwiseClone();
    }

    /// <summary>
    /// Pass/fail targets evaluated per room. Defaults follow common voice-alarm practice (EN 50849 /
    /// IEC 60268-16: STI ≥ 0.50; SPL ≥ 65 dBA and ≥ 10 dB above the background; ≤ 6 dB variation).
    /// Stored with the project, so each project can use its own values.
    /// </summary>
    public class ReportCriteria
    {
        public bool CheckStiMin { get; set; } = true;
        /// <summary>Lowest STI allowed at any point of a room.</summary>
        public double StiMin { get; set; } = 0.50;

        public bool CheckStiMean { get; set; }
        /// <summary>Lowest room-average STI allowed.</summary>
        public double StiMeanMin { get; set; } = 0.50;

        public bool CheckSplSpread { get; set; } = true;
        /// <summary>Largest broadband SPL variation (max − min) allowed within a room, dB.</summary>
        public double SplSpreadMaxDb { get; set; } = 6.0;

        public bool CheckSplMinA { get; set; } = true;
        /// <summary>Lowest A-weighted level allowed at any point, dBA.</summary>
        public double SplMinDbA { get; set; } = 65.0;

        public bool CheckSignalToNoise { get; set; } = true;
        /// <summary>Lowest margin of the A-weighted level over the A-weighted background noise at any point, dB.</summary>
        public double SignalToNoiseMinDb { get; set; } = 10.0;

        /// <summary>
        /// Share of a room's points (0..1) that has to meet the point criteria (STI min, SPL min, signal to noise).
        /// 1 = every point; e.g. 0.9 ignores the worst 10 % (points close to walls and corners).
        /// </summary>
        public double PointCoverage { get; set; } = 1.0;

        /// <summary>Rooms without speakers are listed but not judged (sound from the next room is not a design target).</summary>
        public bool SkipRoomsWithoutSpeakers { get; set; } = true;

        public ReportCriteria Clone() => (ReportCriteria)MemberwiseClone();
    }

    /// <summary>What a report contains. The last options used are stored with the project.</summary>
    public class ReportOptions
    {
        public ReportLanguage Language { get; set; } = ReportLanguage.English;
        public ReportPageFormat PageFormat { get; set; } = ReportPageFormat.A4;

        public bool IncludeCover { get; set; } = true;
        public bool IncludeContents { get; set; } = true;
        public bool IncludeSummary { get; set; } = true;
        public bool IncludeInputs { get; set; } = true;
        public bool IncludeWalls { get; set; } = true;
        public bool IncludeSpeakers { get; set; } = true;
        public bool IncludeSpeakerInstances { get; set; } = true;
        public bool IncludeRoomDetails { get; set; } = true;
        public bool IncludeValueCharts { get; set; } = true;
        public bool IncludeSpeakerContributions { get; set; } = true;
        public bool IncludeCriteria { get; set; } = true;
        public bool IncludeMethod { get; set; } = true;

        /// <summary>Contour lines on the plan figures.</summary>
        public bool ShowIsolines { get; set; } = true;

        /// <summary>Quantities drawn as plan figures for every area.</summary>
        public List<VisualizationMode> PlanModes { get; set; } =
            new List<VisualizationMode> { VisualizationMode.SPL_A, VisualizationMode.STI };

        /// <summary>Quantities printed as value charts (numbers on the grid) for every room.</summary>
        public List<VisualizationMode> ValueChartModes { get; set; } =
            new List<VisualizationMode> { VisualizationMode.STI, VisualizationMode.SPL_A };

        /// <summary>Areas included (by id); empty = all calculated areas.</summary>
        public List<string> AreaIds { get; set; } = new List<string>();

        /// <summary>Rooms left out, as <see cref="RoomKey"/> values.</summary>
        public List<string> ExcludedRooms { get; set; } = new List<string>();

        /// <summary>Key of a room within an area for <see cref="ExcludedRooms"/>.</summary>
        public static string RoomKey(string areaId, string roomName) => (areaId ?? "") + "|" + (roomName ?? "");

        public ReportOptions Clone()
        {
            var c = (ReportOptions)MemberwiseClone();
            c.PlanModes = new List<VisualizationMode>(PlanModes ?? new List<VisualizationMode>());
            c.ValueChartModes = new List<VisualizationMode>(ValueChartModes ?? new List<VisualizationMode>());
            c.AreaIds = new List<string>(AreaIds ?? new List<string>());
            c.ExcludedRooms = new List<string>(ExcludedRooms ?? new List<string>());
            return c;
        }

        /// <summary>Paper size in PDF points (1/72 inch), portrait.</summary>
        public static (float Width, float Height) PageSizePt(ReportPageFormat format) =>
            format == ReportPageFormat.A3 ? (841.89f, 1190.55f) : (595.28f, 841.89f);
    }
}
