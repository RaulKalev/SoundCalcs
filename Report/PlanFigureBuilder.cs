using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Report
{
    /// <summary>Builds the plan figures of a report from a job: the same grid, colours and range as the viewer.</summary>
    public static class PlanFigureBuilder
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The colour range the viewer uses for this job and quantity (P2–P98).</summary>
        public static (double Min, double Max) Range(AcousticJobOutput output, VisualizationMode mode)
        {
            double[] vals = output.Results.Select(r => HeatmapMath.GetValue(r, mode)).ToArray();
            return HeatmapMath.ComputeViewerRange(vals, mode);
        }

        /// <summary>The whole area: every room, wall and speaker, rooms labelled, optional contour lines.</summary>
        public static PlanFigure ForArea(AcousticJobInput input, AcousticJobOutput output, VisualizationMode mode, bool isolines)
        {
            PlanFigure f = Base(input, output, mode, Range(output, mode));
            var extent = new Extent();
            foreach (RoomPolygon r in output.Rooms ?? new List<RoomPolygon>())
                foreach (Vec2 v in r.Vertices) extent.Add(v);
            foreach (ReceiverResult r in output.Results) extent.Add(new Vec2(r.Position.X, r.Position.Y));
            foreach (ComputeSource s in input.Sources) extent.Add(new Vec2(s.Position.X, s.Position.Y));
            extent.Apply(f, 0.6);
            AddRooms(f, output, -1);
            int[] cellRoom = CellRooms(f.Grid, output);
            if (f.Grid != null)
            {
                var rooms = output.Rooms ?? new List<RoomPolygon>();
                for (int ri = 0; ri < rooms.Count; ri++)
                    AddRoomLayer(f, cellRoom, ri, rooms[ri]);
                // Receivers in no room (or a job without rooms) are drawn unclipped
                bool loose = Enumerable.Range(0, cellRoom.Length).Any(i => f.Grid.Filled[i] && cellRoom[i] == -1);
                if (rooms.Count == 0 || loose)
                    f.Layers.Add(new HeatmapLayer { Grid = rooms.Count == 0 ? f.Grid : HeatmapMath.MaskGrid(f.Grid, i => cellRoom[i] == -1) });
            }
            if (isolines) AddIsolines(f, mode, cellRoom, null);
            return f;
        }

        /// <summary>
        /// One room, cropped to it with a margin: its heatmap (clipped to the room), the walls and speakers around it,
        /// optional contour lines and optional grid values (a value chart, with the heatmap faint underneath).
        /// <paramref name="range"/> is the area's range, so colours mean the same in every room of the area.
        /// </summary>
        public static PlanFigure ForRoom(AcousticJobInput input, AcousticJobOutput output, RoomStatistics room,
            VisualizationMode mode, (double Min, double Max) range, bool values, bool isolines)
        {
            PlanFigure f = Base(input, output, mode, range);
            RoomPolygon poly = room.RoomIndex >= 0 && room.RoomIndex < output.Rooms.Count ? output.Rooms[room.RoomIndex] : null;
            var extent = new Extent();
            if (poly != null)
                foreach (Vec2 v in poly.Vertices) extent.Add(v);
            foreach (int i in room.ResultIndices)
                extent.Add(new Vec2(output.Results[i].Position.X, output.Results[i].Position.Y));
            extent.Apply(f, 1.0);
            AddRooms(f, output, room.RoomIndex);
            int[] cellRoom = CellRooms(f.Grid, output);
            if (f.Grid != null)
            {
                if (poly != null) AddRoomLayer(f, cellRoom, room.RoomIndex, poly);
                else f.Layers.Add(new HeatmapLayer { Grid = HeatmapMath.MaskGrid(f.Grid, i => cellRoom[i] == room.RoomIndex) });
            }
            if (isolines) AddIsolines(f, mode, cellRoom, room.RoomIndex);

            if (values && f.Grid != null)
            {
                f.HeatmapOpacity = 0.35;
                ReceiverResult min = null, max = null;
                foreach (int i in room.ResultIndices)
                {
                    ReceiverResult r = output.Results[i];
                    double v = HeatmapMath.GetValue(r, mode);
                    if (min == null || v < HeatmapMath.GetValue(min, mode)) min = r;
                    if (max == null || v > HeatmapMath.GetValue(max, mode)) max = r;
                }
                foreach (int i in room.ResultIndices)
                {
                    ReceiverResult r = output.Results[i];
                    f.Values.Add(new FigureValue
                    {
                        Position = new Vec2(r.Position.X, r.Position.Y),
                        Text = ReportText.Format(mode, HeatmapMath.GetValue(r, mode)),
                        Col = HeatmapMath.Quantise(r.Position.X, f.Grid.XMin, f.Grid.Spacing),
                        Row = HeatmapMath.Quantise(r.Position.Y, f.Grid.YMin, f.Grid.Spacing),
                        IsMin = ReferenceEquals(r, min),
                        IsMax = ReferenceEquals(r, max) && !ReferenceEquals(r, min)
                    });
                }
            }
            return f;
        }

        static PlanFigure Base(AcousticJobInput input, AcousticJobOutput output, VisualizationMode mode, (double Min, double Max) range)
        {
            var f = new PlanFigure { Mode = mode, LegendTitle = ReportText.ModeShort(mode), MinVal = range.Min, MaxVal = range.Max };
            if (output.Results.Count > 0)
            {
                double[] vals = output.Results.Select(r => HeatmapMath.GetValue(r, mode)).ToArray();
                double spacing = HeatmapMath.ViewerGridSpacing(output.Results);
                // Two spare cells around the receivers, so room layers can grow out to the walls
                f.Grid = HeatmapMath.Pad(HeatmapMath.BuildViewerGrid(output.Results, vals, range.Min, range.Max - range.Min, spacing), 2);
            }

            foreach (ComputeWall w in input.Walls ?? new List<ComputeWall>())
                f.Walls.Add(new FigureWall
                {
                    A = w.Start, B = w.End, IsObstacle = w.IsObstacle,
                    IsOpening = w.AbsorptionByBand != null && w.AbsorptionByBand.All(a => a >= 0.999)
                });

            for (int i = 0; i < input.Sources.Count; i++)
            {
                ComputeSource s = input.Sources[i];
                double hx = s.FacingDirection.X, hy = s.FacingDirection.Y, h = Math.Sqrt(hx * hx + hy * hy);
                f.Speakers.Add(new FigureSpeaker
                {
                    Position = new Vec2(s.Position.X, s.Position.Y),
                    Aim = h > 0.5 ? new Vec2(hx / h, hy / h) : (Vec2?)null,
                    Label = SpeakerLabel(i)
                });
            }

            Rgba[] colors = HeatmapMath.ViewerLegendColors();
            int n = colors.Length;
            for (int i = n - 1; i >= 0; i--)
            {
                double lo = range.Min + (range.Max - range.Min) * i / n, hi = range.Min + (range.Max - range.Min) * (i + 1) / n;
                f.Legend.Add(new LegendEntry { Color = colors[i], Label = HeatmapMath.ViewerLegendLabel(lo, hi, mode) });
            }
            return f;
        }

        /// <summary>Label of the speaker at index <paramref name="i"/> in the job (S1, S2, ...), used in figures and tables.</summary>
        public static string SpeakerLabel(int i) => "S" + (i + 1).ToString(Inv);

        /// <summary>A room name without the boundary prefix ("Boundary – Room 1" → "Room 1").</summary>
        public static string RoomDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int k = name.IndexOf(" – ", StringComparison.Ordinal);
            return k > 0 && k + 3 < name.Length ? name.Substring(k + 3) : name;
        }

        static void AddRooms(PlanFigure f, AcousticJobOutput output, int highlight)
        {
            var rooms = output.Rooms ?? new List<RoomPolygon>();
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomPolygon r = rooms[i];
                if (r.Vertices.Count < 3) continue;
                f.Rooms.Add(new FigureRoom
                {
                    Rings = r.Rings().Select(ring => ring.ToList()).ToList(),
                    Label = RoomDisplayName(r.Name),
                    LabelAt = ClearOfSpeakers(LabelPoint(r, output, i), r, f.Speakers),
                    Highlight = i == highlight
                });
            }
        }

        /// <summary>
        /// Moves a label point off speakers: when one is within 1.2 m, the first point up or down the room (at a quarter
        /// and a third of its height) that is inside it and clear of every speaker.
        /// </summary>
        static Vec2 ClearOfSpeakers(Vec2 at, RoomPolygon room, List<FigureSpeaker> speakers)
        {
            bool Clear(Vec2 p) => speakers.All(s => Vec2.Distance(s.Position, p) > 1.2);
            if (Clear(at)) return at;
            double h = room.BoundsMax.Y - room.BoundsMin.Y;
            foreach (double k in new[] { 0.25, -0.25, 0.33, -0.33, 0.15, -0.15 })
            {
                var p = new Vec2(at.X, at.Y + k * h);
                if (room.ContainsPoint(p) && Clear(p)) return p;
            }
            return at;
        }

        /// <summary>Where a room's name goes: its centroid if that lies inside, else the mean of its receivers.</summary>
        public static Vec2 LabelPoint(RoomPolygon r, AcousticJobOutput output, int roomIndex)
        {
            double a = 0, cx = 0, cy = 0;
            var v = r.Vertices;
            for (int i = 0; i < v.Count; i++)
            {
                Vec2 p = v[i], q = v[(i + 1) % v.Count];
                double cr = p.X * q.Y - q.X * p.Y;
                a += cr; cx += (p.X + q.X) * cr; cy += (p.Y + q.Y) * cr;
            }
            if (Math.Abs(a) > 1e-9)
            {
                var c = new Vec2(cx / (3 * a), cy / (3 * a));
                if (r.ContainsPoint(c)) return c;
            }
            var mine = output.Results.Where(x => RoomStatisticsCalculator.RoomOf(x, output.Rooms) == roomIndex).ToList();
            if (mine.Count > 0)
            {
                var m = new Vec2(mine.Average(x => x.Position.X), mine.Average(x => x.Position.Y));
                // The mean of an L-shaped room's points can fall outside it: take the point nearest the mean
                if (r.ContainsPoint(m)) return m;
                ReceiverResult best = mine.OrderBy(x => (x.Position.X - m.X) * (x.Position.X - m.X) + (x.Position.Y - m.Y) * (x.Position.Y - m.Y)).First();
                return new Vec2(best.Position.X, best.Position.Y);
            }
            return v.Count > 0 ? v[0] : Vec2.Zero;
        }

        /// <summary>Room index (into the job's rooms) of each grid cell's receiver; -1 for empty cells and cells in no room.</summary>
        public static int[] CellRooms(HeatmapGrid grid, AcousticJobOutput output)
        {
            if (grid == null) return new int[0];
            var cells = Enumerable.Repeat(-1, grid.Cols * grid.Rows).ToArray();
            foreach (ReceiverResult r in output.Results)
            {
                int c = HeatmapMath.Quantise(r.Position.X, grid.XMin, grid.Spacing), row = HeatmapMath.Quantise(r.Position.Y, grid.YMin, grid.Spacing);
                if ((uint)c < (uint)grid.Cols && (uint)row < (uint)grid.Rows)
                    cells[row * grid.Cols + c] = RoomStatisticsCalculator.RoomOf(r, output.Rooms);
            }
            return cells;
        }

        /// <summary>A room's own cells, grown two cells outward and clipped to its outline, so its colours reach its walls.</summary>
        static void AddRoomLayer(PlanFigure f, int[] cellRoom, int roomIndex, RoomPolygon room)
        {
            if (!cellRoom.Contains(roomIndex) || room.Vertices.Count < 3) return;
            HeatmapGrid own = HeatmapMath.MaskGrid(f.Grid, i => cellRoom[i] == roomIndex);
            f.Layers.Add(new HeatmapLayer { Grid = HeatmapMath.Dilate(own, 2), ClipRings = room.Rings().Select(r => r.ToList()).ToList() });
        }

        /// <summary>Contour lines of each room on its own (or of one room), so no line runs along a wall between rooms.</summary>
        static void AddIsolines(PlanFigure f, VisualizationMode mode, int[] cellRoom, int? onlyRoom)
        {
            if (f.Grid == null) return;
            var rooms = onlyRoom.HasValue ? new List<int> { onlyRoom.Value }
                : Enumerable.Range(0, cellRoom.Length).Where(i => f.Grid.Filled[i]).Select(i => cellRoom[i]).Distinct().ToList();
            var grids = rooms.Select(ri => HeatmapMath.MaskGrid(f.Grid, i => cellRoom[i] == ri)).ToList();
            foreach (double level in HeatmapMath.ContourLevels(f.MinVal, f.MaxVal, mode))
            {
                var lines = grids.SelectMany(g => HeatmapMath.Contour(g, level)).ToList();
                if (lines.Count == 0) continue;
                f.Isolines.Add(new FigureIsoline
                {
                    Level = level,
                    Label = mode == VisualizationMode.STI ? level.ToString("0.00", Inv) : level.ToString("0.#", Inv),
                    Lines = lines
                });
            }
        }

        class Extent
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

            public void Add(Vec2 p)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            public void Apply(PlanFigure f, double margin)
            {
                if (minX > maxX) { minX = minY = 0; maxX = maxY = 1; }
                // At least 2 m across, so a one-point room still reads as a plan
                double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
                double hw = Math.Max(1.0, (maxX - minX) / 2 + margin), hh = Math.Max(1.0, (maxY - minY) / 2 + margin);
                f.MinX = cx - hw; f.MaxX = cx + hw; f.MinY = cy - hh; f.MaxY = cy + hh;
            }
        }
    }
}
