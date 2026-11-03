using System;
using System.Collections.Generic;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Report
{
    /// <summary>
    /// A report as a list of blocks: what to show, in order. It has no fonts, sizes or page positions; a painter lays it
    /// out on pages (<c>SkiaReportPainter</c>). Built by <see cref="ReportBuilder"/>, which the harness and tests cover.
    /// </summary>
    public class ReportDocument
    {
        public string Title { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Author { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;
        public ReportPageFormat PageFormat { get; set; } = ReportPageFormat.A4;

        /// <summary>Page header, left (project name and number).</summary>
        public string HeaderLeft { get; set; } = "";

        /// <summary>Page footer, left (report title, revision and date).</summary>
        public string FooterLeft { get; set; } = "";

        /// <summary>Footer page number pattern with {0} = page and {1} = page count.</summary>
        public string PageNumberFormat { get; set; } = "Page {0} of {1}";

        /// <summary>Title of the contents block.</summary>
        public string ContentsTitle { get; set; } = "Contents";

        public List<ReportBlock> Blocks { get; set; } = new List<ReportBlock>();
    }

    public abstract class ReportBlock
    {
    }

    /// <summary>The cover: title, project fields, logo and the areas in the report. Always a page of its own.</summary>
    public class CoverBlock : ReportBlock
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string LogoPath { get; set; } = "";
        public List<KeyValuePair<string, string>> Fields { get; set; } = new List<KeyValuePair<string, string>>();
        public string ListTitle { get; set; } = "";
        public List<string> ListItems { get; set; } = new List<string>();
        public string Note { get; set; } = "";
    }

    /// <summary>Contents: every level 1 and 2 heading with its page (filled in by the painter's layout).</summary>
    public class ContentsBlock : ReportBlock
    {
    }

    public class PageBreakBlock : ReportBlock
    {
    }

    public class HeadingBlock : ReportBlock
    {
        /// <summary>1 = section (starts a page, named in the page header), 2 = subsection, 3 = minor heading.</summary>
        public int Level { get; set; } = 1;
        public string Text { get; set; } = "";
        public bool PageBreakBefore { get; set; }

        /// <summary>Unique id for contents links; set by the builder.</summary>
        public string Anchor { get; set; } = "";
    }

    public enum ParagraphStyle
    {
        Body,
        Note,
        Caption,
        Warning
    }

    public class ParagraphBlock : ReportBlock
    {
        public string Text { get; set; } = "";
        public ParagraphStyle Style { get; set; } = ParagraphStyle.Body;
    }

    /// <summary>Label/value pairs in one or two columns of pairs.</summary>
    public class KeyValueBlock : ReportBlock
    {
        public List<KeyValuePair<string, string>> Items { get; set; } = new List<KeyValuePair<string, string>>();
        public int Columns { get; set; } = 2;
    }

    public enum CellAlign
    {
        Left,
        Right,
        Center
    }

    public enum CellStatus
    {
        None,
        Pass,
        Fail,
        Muted
    }

    public class TableColumn
    {
        public string Header { get; set; } = "";
        public CellAlign Align { get; set; } = CellAlign.Left;

        /// <summary>Relative width; columns share the table width by weight.</summary>
        public float Weight { get; set; } = 1f;

        public TableColumn() { }

        public TableColumn(string header, float weight, CellAlign align = CellAlign.Left)
        {
            Header = header; Weight = weight; Align = align;
        }
    }

    public class TableRow
    {
        public string[] Cells { get; set; } = new string[0];

        /// <summary>Per-cell status colour (null = none).</summary>
        public CellStatus[] Status { get; set; }

        /// <summary>A group row (e.g. the area name in a multi-area table): bold on a shaded band.</summary>
        public bool IsGroup { get; set; }

        public bool Bold { get; set; }

        public TableRow() { }

        public TableRow(params string[] cells) { Cells = cells; }
    }

    public class TableBlock : ReportBlock
    {
        public string Caption { get; set; } = "";
        public List<TableColumn> Columns { get; set; } = new List<TableColumn>();
        public List<TableRow> Rows { get; set; } = new List<TableRow>();

        /// <summary>Text size relative to body text (wide tables use smaller text).</summary>
        public float TextScale { get; set; } = 1f;

        public TableRow AddRow(params string[] cells)
        {
            var r = new TableRow(cells);
            Rows.Add(r);
            return r;
        }
    }

    /// <summary>A horizontal bar split into coloured shares with a legend (the STI category distribution).</summary>
    public class DistributionBlock : ReportBlock
    {
        public string Caption { get; set; } = "";
        public List<DistributionSegment> Segments { get; set; } = new List<DistributionSegment>();
    }

    public class DistributionSegment
    {
        public string Label { get; set; } = "";
        public double Fraction { get; set; }
        public Rgba Color { get; set; }
    }

    public class FigureBlock : ReportBlock
    {
        public PlanFigure Figure { get; set; }
        public string Caption { get; set; } = "";

        /// <summary>
        /// Caption used instead when the painter has to thin out grid values to keep them legible; {0} = the step
        /// (every n-th point in each direction). Empty = keep <see cref="Caption"/>.
        /// </summary>
        public string CaptionWhenThinned { get; set; } = "";

        /// <summary>Largest height as a share of the page's content height.</summary>
        public float MaxHeightFraction { get; set; } = 0.62f;
    }

    /// <summary>
    /// A plan drawing: heatmap, walls, rooms, speakers, contour lines or grid values and a legend, in world metres.
    /// Everything here is decided by <see cref="PlanFigureBuilder"/>; the painter only scales and draws it.
    /// </summary>
    public class PlanFigure
    {
        public VisualizationMode Mode { get; set; }

        /// <summary>Legend header ("STI", "dBA", ...).</summary>
        public string LegendTitle { get; set; } = "";

        /// <summary>World rectangle to show (metres); the painter keeps its aspect and puts +Y up.</summary>
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        /// <summary>The whole job's grid (values for contours and value charts).</summary>
        public HeatmapGrid Grid { get; set; }

        /// <summary>
        /// What the heatmap is drawn from: one layer per room (its own cells, grown to the walls, clipped to its
        /// outline), or the whole grid unclipped when the job has no rooms.
        /// </summary>
        public List<HeatmapLayer> Layers { get; set; } = new List<HeatmapLayer>();

        /// <summary>Heatmap opacity (value charts show the colours faintly under the numbers).</summary>
        public double HeatmapOpacity { get; set; } = 1.0;


        public List<FigureWall> Walls { get; set; } = new List<FigureWall>();
        public List<FigureRoom> Rooms { get; set; } = new List<FigureRoom>();
        public List<FigureSpeaker> Speakers { get; set; } = new List<FigureSpeaker>();
        public List<FigureIsoline> Isolines { get; set; } = new List<FigureIsoline>();
        public List<FigureValue> Values { get; set; } = new List<FigureValue>();

        /// <summary>Legend swatches, highest value first.</summary>
        public List<LegendEntry> Legend { get; set; } = new List<LegendEntry>();

        /// <summary>Range the colours span (the viewer's P2–P98 range).</summary>
        public double MinVal { get; set; }
        public double MaxVal { get; set; }

        public double WidthM => MaxX - MinX;
        public double HeightM => MaxY - MinY;
    }

    public class HeatmapLayer
    {
        public HeatmapGrid Grid { get; set; }

        /// <summary>Rings the layer is clipped to (even-odd: outline and holes); null = not clipped.</summary>
        public List<List<Vec2>> ClipRings { get; set; }
    }

    public class FigureWall
    {
        public Vec2 A { get; set; }
        public Vec2 B { get; set; }
        public bool IsObstacle { get; set; }

        /// <summary>Openings ("Open (No Wall)") are drawn dashed.</summary>
        public bool IsOpening { get; set; }
    }

    public class FigureRoom
    {
        public List<List<Vec2>> Rings { get; set; } = new List<List<Vec2>>();
        public string Label { get; set; } = "";
        public Vec2 LabelAt { get; set; }

        /// <summary>The room a room figure is about (drawn stronger; other rooms are context).</summary>
        public bool Highlight { get; set; }
    }

    public class FigureSpeaker
    {
        public Vec2 Position { get; set; }

        /// <summary>Unit horizontal aim for wall-mounted speakers; null for ceiling speakers (aim down).</summary>
        public Vec2? Aim { get; set; }

        public string Label { get; set; } = "";
    }

    public class FigureIsoline
    {
        public double Level { get; set; }
        public string Label { get; set; } = "";
        public List<List<Vec2>> Lines { get; set; } = new List<List<Vec2>>();
    }

    public class FigureValue
    {
        public Vec2 Position { get; set; }
        public string Text { get; set; } = "";

        /// <summary>Grid cell of the value, so the painter can thin out values that would overlap.</summary>
        public int Col { get; set; }
        public int Row { get; set; }

        /// <summary>The room's lowest / highest value: always drawn, and marked.</summary>
        public bool IsMin { get; set; }
        public bool IsMax { get; set; }
    }

    public class LegendEntry
    {
        public Rgba Color { get; set; }
        public string Label { get; set; } = "";
    }
}
