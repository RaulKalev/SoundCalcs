using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using SoundCalcs.Domain;
using SoundCalcs.Revit;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.Visualization
{
    /// <summary>
    /// Renders acoustic analysis results as colored FilledRegion elements in the active Revit
    /// plan view.  Each dB band gets its own FilledRegionType with a solid fill color; adjacent
    /// grid cells in the same band are merged into a single polygon boundary so the result looks
    /// like a smooth color-block heatmap instead of disconnected squares.
    ///
    /// Must be called on the Revit API thread inside a Transaction.
    /// </summary>
    public class FilledRegionRenderer
    {
        // -----------------------------------------------------------------------
        // Color palette – 8 bands quiet (red) → loud (green). Defined in HeatmapMath
        // so the headless harness and tests use exactly the same colours.
        // -----------------------------------------------------------------------
        private static readonly (string Name, byte R, byte G, byte B)[] BandColors =
            HeatmapMath.RevitBandColors;

        private const string RegionTypePrefix = "SC_SPL_";
        private const string StiRegionTypePrefix = "SC_STI_";

        /// <summary>STI intelligibility labels for the 8 color bands.</summary>
        private static readonly string[] StiLabels = HeatmapMath.StiLabels;

        // Cached across calls within a session so we don't re-query the pattern every render
        private static ElementId _solidFillPatternId = ElementId.InvalidElementId;
        private static ElementId _invisibleLinesStyleId = ElementId.InvalidElementId;

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Get the band info for building a UI legend.
        /// Returns (bandIndex, colorHex, labelText) for each band.
        /// </summary>
        public static List<(int Band, string ColorHex, string Label)> GetLegendBands(
            double minSpl, double maxSpl, string suffix = " dB")
            => HeatmapMath.GetLegendBands(minSpl, maxSpl, suffix);

        /// <summary>
        /// Get the band info for building a STI legend.
        /// Returns (bandIndex, colorHex, labelText) for each band.
        /// </summary>
        public static List<(int Band, string ColorHex, string Label)> GetStiLegendBands(
            double minSti, double maxSti)
            => HeatmapMath.GetStiLegendBands(minSti, maxSti);

        public void Render(Document doc, View view, AcousticJobOutput output,
            VisualizationMode mode = VisualizationMode.SPL,
            double? minSplThreshold = null)
        {
            if (output == null || output.Results.Count == 0)
            {
                Debug.WriteLine("[SoundCalcs] No results to visualize.");
                return;
            }

            // Range, per-receiver bands and merged rectangles are decided in pure
            // code (HeatmapMath) so the headless harness can verify them.
            FilledRegionPlan plan = HeatmapMath.PlanFilledRegions(output.Results, mode, minSplThreshold);
            if (plan == null)
            {
                Debug.WriteLine("[SoundCalcs] All points below SPL threshold — nothing to render.");
                return;
            }

            int octaveBandIdx = plan.OctaveBandIndex;
            bool isPerBand = octaveBandIdx >= 0;
            double minVal = plan.MinVal;
            double maxVal = plan.MaxVal;
            int numBands = plan.NumBands;
            var strips = plan.Strips;

            using (Transaction tx = new Transaction(doc, "SoundCalcs: Render Heatmap"))
            {
                tx.Start();
                try
                {
                    ClearOldRegions(doc);

                    ElementId[] regionTypeIds;
                    if (mode == VisualizationMode.STI)
                        regionTypeIds = EnsureStiFilledRegionTypes(doc, minVal, maxVal);
                    else if (isPerBand)
                        regionTypeIds = EnsureBandFilledRegionTypes(doc, minVal, maxVal, octaveBandIdx);
                    else
                        regionTypeIds = EnsureFilledRegionTypes(doc, minVal, maxVal);
                    // SPL_A and C80 reuse the broadband FilledRegionTypes (same color ramp)

                    double mToFt = UnitConversion.MetersToFeet;
                    int created = 0;
                    ElementId invisibleStyle = GetInvisibleLinesStyleId(doc);

                    for (int band = 0; band < numBands; band++)
                    {
                        ElementId typeId = regionTypeIds[band];
                        if (typeId == ElementId.InvalidElementId) continue;

                        if (!strips.ContainsKey(band)) continue;
                        foreach (var (x0, y0, x1, y1, z) in strips[band])
                        {
                            try
                            {
                                var p0 = new XYZ(x0 * mToFt, y0 * mToFt, z * mToFt);
                                var p1 = new XYZ(x1 * mToFt, y0 * mToFt, z * mToFt);
                                var p2 = new XYZ(x1 * mToFt, y1 * mToFt, z * mToFt);
                                var p3 = new XYZ(x0 * mToFt, y1 * mToFt, z * mToFt);

                                var loop = new CurveLoop();
                                loop.Append(Line.CreateBound(p0, p1));
                                loop.Append(Line.CreateBound(p1, p2));
                                loop.Append(Line.CreateBound(p2, p3));
                                loop.Append(Line.CreateBound(p3, p0));

                                var region = FilledRegion.Create(doc, typeId, view.Id,
                                    new List<CurveLoop> { loop });

                                if (invisibleStyle != ElementId.InvalidElementId)
                                    region.SetLineStyleId(invisibleStyle);

                                created++;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine(
                                    $"[SoundCalcs] Strip create failed band {band}: {ex.Message}");
                            }
                        }
                    }

                    tx.Commit();

                    // Write back the exact range used so the UI legend matches.
                    output.RenderedMinVal = minVal;
                    output.RenderedMaxVal = maxVal;
                    output.RenderedMode = mode.ToString();

                    string modeLabel;
                    if (mode == VisualizationMode.STI)
                        modeLabel = "STI";
                    else if (mode == VisualizationMode.SPL_A)
                        modeLabel = "SPL (A-weighted, dBA)";
                    else if (mode == VisualizationMode.C80)
                        modeLabel = "Clarity C80";
                    else if (isPerBand)
                        modeLabel = $"SPL @{OctaveBands.Labels[octaveBandIdx]} Hz";
                    else
                        modeLabel = "SPL";
                    Debug.WriteLine($"[SoundCalcs] {modeLabel} heatmap rendered. " +
                        $"{output.Results.Count} pts, range {minVal:F2}\u2013{maxVal:F2}");
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    Debug.WriteLine($"[SoundCalcs] RENDER FAILED: {ex}");
                    throw;
                }
            }
        }

        public void Clear(Document doc, View view)
        {
            using (Transaction tx = new Transaction(doc, "SoundCalcs: Clear Heatmap"))
            {
                tx.Start();
                try
                {
                    ClearOldRegions(doc);
                    tx.Commit();
                    Debug.WriteLine("[SoundCalcs] Heatmap cleared.");
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    Debug.WriteLine($"[SoundCalcs] Clear failed: {ex.Message}");
                }
            }
        }

        // -----------------------------------------------------------------------
        // FilledRegionType management
        // -----------------------------------------------------------------------

        private static void ClearOldRegions(Document doc)
        {
            HashSet<ElementId> scTypeIds = GetSoundCalcsRegionTypeIds(doc);
            if (scTypeIds.Count == 0) return;

            using (var coll = new FilteredElementCollector(doc))
            {
                List<ElementId> toDelete = coll
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .Where(fr => scTypeIds.Contains(fr.GetTypeId()))
                    .Select(fr => fr.Id)
                    .ToList();

                foreach (ElementId id in toDelete)
                {
                    try { doc.Delete(id); }
                    catch { }
                }
            }
        }

        private static HashSet<ElementId> GetSoundCalcsRegionTypeIds(Document doc)
        {
            var ids = new HashSet<ElementId>();
            using (var coll = new FilteredElementCollector(doc))
            {
                foreach (FilledRegionType frt in coll
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>())
                {
                    if (IsSoundCalcsType(frt.Name))
                        ids.Add(frt.Id);
                }
            }
            return ids;
        }

        private static ElementId[] EnsureFilledRegionTypes(Document doc, double minSpl, double maxSpl)
        {
            int numBands = BandColors.Length;
            double range = maxSpl - minSpl;
            double step = numBands > 0 && range > 0 ? range / numBands : 0;

            // Build display names with dB ranges
            var bandNames = new string[numBands];
            for (int i = 0; i < numBands; i++)
            {
                double lo = minSpl + i * step;
                double hi = minSpl + (i + 1) * step;
                bandNames[i] = $"{RegionTypePrefix}{lo:F1}-{hi:F1} dB";
            }

            // Pick any non-SC type as a duplication template
            FilledRegionType template = null;
            using (var coll = new FilteredElementCollector(doc))
            {
                template = coll
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault(frt =>
                        !frt.Name.StartsWith(RegionTypePrefix, StringComparison.OrdinalIgnoreCase));
            }

            if (template == null)
                throw new InvalidOperationException(
                    "[SoundCalcs] No FilledRegionType in document to use as template.");

            // Catalogue existing SC types
            var existing = new Dictionary<string, FilledRegionType>(StringComparer.OrdinalIgnoreCase);
            using (var coll = new FilteredElementCollector(doc))
            {
                foreach (FilledRegionType frt in coll
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>())
                {
                    if (frt.Name.StartsWith(RegionTypePrefix, StringComparison.OrdinalIgnoreCase))
                        existing[frt.Name] = frt;
                }
            }

            ElementId solidFillId = GetSolidFillPatternId(doc);
            var typeIds = new ElementId[BandColors.Length];

            for (int i = 0; i < BandColors.Length; i++)
            {
                string name = bandNames[i];
                (string _, byte r, byte g, byte b) = BandColors[i];

                FilledRegionType frt = existing.TryGetValue(name, out FilledRegionType found)
                    ? found
                    : template.Duplicate(name) as FilledRegionType;

                if (frt == null)
                {
                    typeIds[i] = ElementId.InvalidElementId;
                    continue;
                }

                try
                {
                    if (solidFillId != ElementId.InvalidElementId)
                        frt.ForegroundPatternId = solidFillId;
                    frt.ForegroundPatternColor = new Color(r, g, b);
                    frt.IsMasking = false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SoundCalcs] Region type styling ({name}): {ex.Message}");
                }

                typeIds[i] = frt.Id;
            }

            return typeIds;
        }

        private static ElementId[] EnsureStiFilledRegionTypes(Document doc, double minSti, double maxSti)
        {
            int numBands = BandColors.Length;
            double range = maxSti - minSti;
            double step = numBands > 0 && range > 0 ? range / numBands : 0;

            var bandNames = new string[numBands];
            for (int i = 0; i < numBands; i++)
            {
                double lo = minSti + i * step;
                double hi = minSti + (i + 1) * step;
                string quality = i < StiLabels.Length ? StiLabels[i] : "";
                bandNames[i] = $"{StiRegionTypePrefix}{lo:F2}-{hi:F2} ({quality})";
            }

            FilledRegionType template = null;
            using (var coll = new FilteredElementCollector(doc))
            {
                template = coll
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault(frt =>
                        !frt.Name.StartsWith(RegionTypePrefix, StringComparison.OrdinalIgnoreCase)
                        && !frt.Name.StartsWith(StiRegionTypePrefix, StringComparison.OrdinalIgnoreCase));
            }

            if (template == null)
                throw new InvalidOperationException(
                    "[SoundCalcs] No FilledRegionType in document to use as template.");

            var existing = new Dictionary<string, FilledRegionType>(StringComparer.OrdinalIgnoreCase);
            using (var coll = new FilteredElementCollector(doc))
            {
                foreach (FilledRegionType frt in coll
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>())
                {
                    if (frt.Name.StartsWith(StiRegionTypePrefix, StringComparison.OrdinalIgnoreCase))
                        existing[frt.Name] = frt;
                }
            }

            ElementId solidFillId = GetSolidFillPatternId(doc);
            var typeIds = new ElementId[numBands];

            for (int i = 0; i < numBands; i++)
            {
                string name = bandNames[i];
                (string _, byte r, byte g, byte b) = BandColors[i];

                FilledRegionType frt = existing.TryGetValue(name, out FilledRegionType found)
                    ? found
                    : template.Duplicate(name) as FilledRegionType;

                if (frt == null)
                {
                    typeIds[i] = ElementId.InvalidElementId;
                    continue;
                }

                try
                {
                    if (solidFillId != ElementId.InvalidElementId)
                        frt.ForegroundPatternId = solidFillId;
                    frt.ForegroundPatternColor = new Color(r, g, b);
                    frt.IsMasking = false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SoundCalcs] STI region type styling ({name}): {ex.Message}");
                }

                typeIds[i] = frt.Id;
            }

            return typeIds;
        }

        /// <summary>
        /// Ensures per-octave-band FilledRegionTypes exist for a specific frequency band.
        /// Uses prefix "SC_SPL_{freq}_" to distinguish from broadband and STI types.
        /// </summary>
        private static ElementId[] EnsureBandFilledRegionTypes(Document doc, double minSpl, double maxSpl, int bandIndex)
        {
            string freq = OctaveBands.Labels[bandIndex];
            string prefix = $"SC_SPL_{freq}_";

            int numBands = BandColors.Length;
            double range = maxSpl - minSpl;
            double step = numBands > 0 && range > 0 ? range / numBands : 0;

            var bandNames = new string[numBands];
            for (int i = 0; i < numBands; i++)
            {
                double lo = minSpl + i * step;
                double hi = minSpl + (i + 1) * step;
                bandNames[i] = $"{prefix}{lo:F1}-{hi:F1} dB";
            }

            FilledRegionType template = null;
            using (var coll = new FilteredElementCollector(doc))
            {
                template = coll
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault(frt => !IsSoundCalcsType(frt.Name));
            }

            if (template == null)
                throw new InvalidOperationException(
                    "[SoundCalcs] No FilledRegionType in document to use as template.");

            var existing = new Dictionary<string, FilledRegionType>(StringComparer.OrdinalIgnoreCase);
            using (var coll = new FilteredElementCollector(doc))
            {
                foreach (FilledRegionType frt in coll
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>())
                {
                    if (frt.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        existing[frt.Name] = frt;
                }
            }

            ElementId solidFillId = GetSolidFillPatternId(doc);
            var typeIds = new ElementId[numBands];

            for (int i = 0; i < numBands; i++)
            {
                string name = bandNames[i];
                (string _, byte r, byte g, byte b) = BandColors[i];

                FilledRegionType frt = existing.TryGetValue(name, out FilledRegionType found)
                    ? found
                    : template.Duplicate(name) as FilledRegionType;

                if (frt == null)
                {
                    typeIds[i] = ElementId.InvalidElementId;
                    continue;
                }

                try
                {
                    if (solidFillId != ElementId.InvalidElementId)
                        frt.ForegroundPatternId = solidFillId;
                    frt.ForegroundPatternColor = new Color(r, g, b);
                    frt.IsMasking = false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SoundCalcs] Band region type styling ({name}): {ex.Message}");
                }

                typeIds[i] = frt.Id;
            }

            return typeIds;
        }

        /// <summary>
        /// Returns true if a FilledRegionType name belongs to SoundCalcs.
        /// </summary>
        private static bool IsSoundCalcsType(string name)
        {
            return name.StartsWith(RegionTypePrefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(StiRegionTypePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static ElementId GetSolidFillPatternId(Document doc)
        {
            if (_solidFillPatternId != ElementId.InvalidElementId)
                return _solidFillPatternId;

            using (var coll = new FilteredElementCollector(doc))
            {
                foreach (FillPatternElement fpe in coll
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>())
                {
                    FillPattern fp = fpe.GetFillPattern();
                    if (fp != null && fp.IsSolidFill)
                    {
                        _solidFillPatternId = fpe.Id;
                        return _solidFillPatternId;
                    }
                }
            }

            return ElementId.InvalidElementId;
        }

        /// <summary>
        /// Find the built-in "&lt;Invisible lines&gt;" GraphicsStyle element.
        /// Cached across render calls within a session.
        /// </summary>
        private static ElementId GetInvisibleLinesStyleId(Document doc)
        {
            if (_invisibleLinesStyleId != ElementId.InvalidElementId)
                return _invisibleLinesStyleId;

            using (var coll = new FilteredElementCollector(doc))
            {
                foreach (GraphicsStyle gs in coll
                    .OfClass(typeof(GraphicsStyle))
                    .Cast<GraphicsStyle>())
                {
                    if (gs.GraphicsStyleCategory != null &&
                        gs.GraphicsStyleCategory.Name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _invisibleLinesStyleId = gs.Id;
                        return _invisibleLinesStyleId;
                    }
                }
            }

            Debug.WriteLine("[SoundCalcs] 'Invisible lines' style not found; boundaries will remain visible.");
            return ElementId.InvalidElementId;
        }

        // -----------------------------------------------------------------------
        // Geometry: merge grid cells into closed CurveLoops
        // -----------------------------------------------------------------------

        /// <summary>
        /// Convert a band's receiver results into groups of CurveLoops suitable for
        /// FilledRegion.Create.  Connected cells (4-connectivity) are merged; boundaries
        /// are extracted as directed edge chains and stitched into closed loops.
        /// </summary>
        private static List<List<CurveLoop>> BuildMergedLoops(
            List<ReceiverResult> bandResults,
            double originX, double originY,
            double gridSpacingM, double halfM,
            List<List<(double x, double y)>> clipPolygons)
        {
            // Build (col, row) cell set and world-position lookup
            var cells = new HashSet<(int, int)>(bandResults.Count);
            var cellWorld = new Dictionary<(int, int), (double cx, double cy, double cz)>(bandResults.Count);

            foreach (ReceiverResult r in bandResults)
            {
                int col = Quantise(r.Position.X, originX, gridSpacingM);
                int row = Quantise(r.Position.Y, originY, gridSpacingM);
                cells.Add((col, row));
                cellWorld[(col, row)] = (r.Position.X, r.Position.Y, r.Position.Z);
            }

            // --- Flood fill: connected components ---
            var visited = new HashSet<(int, int)>(cells.Count);
            var result = new List<List<CurveLoop>>();

            foreach (var seed in cells)
            {
                if (visited.Contains(seed)) continue;

                var component = new HashSet<(int, int)>();
                var queue = new Queue<(int, int)>();
                queue.Enqueue(seed);
                visited.Add(seed);

                while (queue.Count > 0)
                {
                    var (c, rr) = queue.Dequeue();
                    component.Add((c, rr));

                    foreach (var nb in FourNeighbours(c, rr))
                    {
                        if (!visited.Contains(nb) && cells.Contains(nb))
                        {
                            visited.Add(nb);
                            queue.Enqueue(nb);
                        }
                    }
                }

                List<CurveLoop> loops = ExtractBoundaryLoops(
                    component, cellWorld, originX, originY, gridSpacingM, halfM);

                // Clip to room boundary polygons if available
                if (clipPolygons.Count > 0 && loops.Count > 0)
                    loops = ClipLoopsToPolygons(loops, clipPolygons);

                if (loops.Count > 0)
                    result.Add(loops);
            }

            return result;
        }

        private static (int, int)[] EightNeighbours(int c, int r)
        {
            return new[]
            {
                (c - 1, r), (c + 1, r), (c, r - 1), (c, r + 1),
                (c - 1, r - 1), (c - 1, r + 1), (c + 1, r - 1), (c + 1, r + 1)
            };
        }

        private static (int, int)[] FourNeighbours(int c, int r)
        {
            return new[] { (c - 1, r), (c + 1, r), (c, r - 1), (c, r + 1) };
        }

        /// <summary>
        /// For one connected component, collect directed boundary edges (cell edges whose
        /// opposite neighbour is outside the component), then stitch them into closed loops.
        /// </summary>
        private static List<CurveLoop> ExtractBoundaryLoops(
            HashSet<(int col, int row)> component,
            Dictionary<(int, int), (double cx, double cy, double cz)> cellWorld,
            double originX, double originY,
            double gridSpacingM, double halfM)
        {
            // edgeMap: start-vertex-key → end-vertex world position (metres)
            var edgeMap = new Dictionary<(long, long), (double ex, double ey, double z)>();

            double defaultZ = 0;
            bool zSet = false;

            foreach (var (col, row) in component)
            {
                double cx, cy, cz;
                if (cellWorld.TryGetValue((col, row), out var wp))
                {
                    cx = wp.cx; cy = wp.cy; cz = wp.cz;
                }
                else
                {
                    cx = originX + col * gridSpacingM;
                    cy = originY + row * gridSpacingM;
                    cz = defaultZ;
                }

                if (!zSet) { defaultZ = cz; zSet = true; }

                double bl_x = cx - halfM, bl_y = cy - halfM;
                double br_x = cx + halfM, br_y = cy - halfM;
                double tr_x = cx + halfM, tr_y = cy + halfM;
                double tl_x = cx - halfM, tl_y = cy + halfM;

                // Bottom (left→right): exposed when no cell at (col, row-1)
                if (!component.Contains((col, row - 1)))
                    PutEdge(edgeMap, bl_x, bl_y, br_x, br_y, cz);

                // Right (bottom→top): exposed when no cell at (col+1, row)
                if (!component.Contains((col + 1, row)))
                    PutEdge(edgeMap, br_x, br_y, tr_x, tr_y, cz);

                // Top (right→left): exposed when no cell at (col, row+1)
                if (!component.Contains((col, row + 1)))
                    PutEdge(edgeMap, tr_x, tr_y, tl_x, tl_y, cz);

                // Left (top→bottom): exposed when no cell at (col-1, row)
                if (!component.Contains((col - 1, row)))
                    PutEdge(edgeMap, tl_x, tl_y, bl_x, bl_y, cz);
            }

            return StitchEdges(edgeMap);
        }

        private static (long, long) VKey(double x, double y)
        {
            // 0.001 mm precision avoids FP rounding while staying unambiguous
            return ((long)Math.Round(x * 1_000_000), (long)Math.Round(y * 1_000_000));
        }

        private static void PutEdge(
            Dictionary<(long, long), (double ex, double ey, double z)> map,
            double sx, double sy, double ex, double ey, double z)
        {
            map[VKey(sx, sy)] = (ex, ey, z);
        }

        private static List<CurveLoop> StitchEdges(
            Dictionary<(long, long), (double ex, double ey, double z)> edgeMap)
        {
            var remaining = new Dictionary<(long, long), (double ex, double ey, double z)>(edgeMap);
            var loops = new List<CurveLoop>();
            double mToFt = UnitConversion.MetersToFeet;

            while (remaining.Count > 0)
            {
                var startKey = remaining.Keys.First();
                var pts = new List<(double x, double y, double z)>();
                var cur = startKey;

                int guard = 0;
                while (remaining.ContainsKey(cur))
                {
                    if (++guard > 500_000) break;

                    var (ex, ey, ez) = remaining[cur];
                    pts.Add((cur.Item1 / 1_000_000.0, cur.Item2 / 1_000_000.0, ez));
                    remaining.Remove(cur);
                    cur = VKey(ex, ey);
                }

                if (pts.Count < 3) continue;

                pts = CollapseCollinear(pts);
                if (pts.Count < 3) continue;

                try
                {
                    var loop = new CurveLoop();
                    int n = pts.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var (x0, y0, z0) = pts[i];
                        var (x1, y1, z1) = pts[(i + 1) % n];
                        var p0 = new XYZ(x0 * mToFt, y0 * mToFt, z0 * mToFt);
                        var p1 = new XYZ(x1 * mToFt, y1 * mToFt, z1 * mToFt);
                        if (p0.DistanceTo(p1) < 1e-7) continue;
                        loop.Append(Line.CreateBound(p0, p1));
                    }

                    if (loop.NumberOfCurves() >= 3)
                        loops.Add(loop);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SoundCalcs] CurveLoop build failed: {ex.Message}");
                }
            }

            return loops;
        }

        /// <summary>Remove collinear intermediate vertices to minimize curve segment count.</summary>
        private static List<(double x, double y, double z)> CollapseCollinear(
            List<(double x, double y, double z)> pts)
        {
            if (pts.Count <= 3) return pts;

            var result = new List<(double x, double y, double z)>(pts.Count);
            int n = pts.Count;

            for (int i = 0; i < n; i++)
            {
                var (px, py, _) = pts[(i + n - 1) % n];
                var (cx, cy, cz) = pts[i];
                var (nx, ny, _) = pts[(i + 1) % n];

                double cross = (cx - px) * (ny - cy) - (cy - py) * (nx - cx);
                if (Math.Abs(cross) > 1e-12)
                    result.Add((cx, cy, cz));
            }

            return result.Count >= 3 ? result : pts;
        }

        /// <summary>
        /// Chaikin corner-cutting subdivision. Each iteration replaces every edge
        /// (P_i → P_{i+1}) with two new points at 25 % and 75 %, rounding off
        /// staircase corners into smooth diagonals.  Closed-polygon variant.
        /// </summary>
        private static List<(double x, double y, double z)> ChaikinSmooth(
            List<(double x, double y, double z)> pts, int iterations)
        {
            if (pts.Count < 3 || iterations <= 0) return pts;

            var current = pts;
            for (int iter = 0; iter < iterations; iter++)
            {
                int n = current.Count;
                var next = new List<(double x, double y, double z)>(n * 2);

                for (int i = 0; i < n; i++)
                {
                    var (x0, y0, z0) = current[i];
                    var (x1, y1, z1) = current[(i + 1) % n];

                    // Q = 0.75·P_i + 0.25·P_{i+1}
                    next.Add((0.75 * x0 + 0.25 * x1,
                              0.75 * y0 + 0.25 * y1,
                              z0));
                    // R = 0.25·P_i + 0.75·P_{i+1}
                    next.Add((0.25 * x0 + 0.75 * x1,
                              0.25 * y0 + 0.75 * y1,
                              z0));
                }

                current = next;
            }

            return current;
        }

        // -----------------------------------------------------------------------
        // Polygon clipping (Sutherland-Hodgman)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Clip each CurveLoop against the room boundary polygons.
        /// Converts loop→2D polygon, clips via Sutherland-Hodgman, rebuilds CurveLoop.
        /// </summary>
        private static List<CurveLoop> ClipLoopsToPolygons(
            List<CurveLoop> loops,
            List<List<(double x, double y)>> clipPolygons)
        {
            double ftToM = UnitConversion.FeetToMeters;
            double mToFt = UnitConversion.MetersToFeet;
            var clippedLoops = new List<CurveLoop>();

            foreach (CurveLoop loop in loops)
            {
                // Extract vertices from CurveLoop (in feet) → metres
                var pts = new List<(double x, double y, double z)>();
                double z = 0;
                foreach (Curve curve in loop)
                {
                    XYZ p = curve.GetEndPoint(0);
                    pts.Add((p.X * ftToM, p.Y * ftToM, p.Z * ftToM));
                    z = p.Z * ftToM;
                }

                if (pts.Count < 3) continue;

                // Build 2D polygon
                var poly2D = new List<(double x, double y)>(pts.Count);
                foreach (var p in pts)
                    poly2D.Add((p.x, p.y));

                // Clip against each room polygon (intersect)
                var bestClipped = poly2D;
                foreach (var clipPoly in clipPolygons)
                {
                    var clipped = SutherlandHodgmanClip(poly2D, clipPoly);
                    if (clipped.Count >= 3)
                    {
                        bestClipped = clipped;
                        break; // use the first clip polygon that produces a valid result
                    }
                }

                if (bestClipped.Count < 3) continue;

                // Simplify collinear
                var simplified = CollapseCollinear2D(bestClipped);
                if (simplified.Count < 3) continue;

                // Rebuild CurveLoop
                try
                {
                    var newLoop = new CurveLoop();
                    int n = simplified.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var (x0, y0) = simplified[i];
                        var (x1, y1) = simplified[(i + 1) % n];
                        var p0 = new XYZ(x0 * mToFt, y0 * mToFt, z * mToFt);
                        var p1 = new XYZ(x1 * mToFt, y1 * mToFt, z * mToFt);
                        if (p0.DistanceTo(p1) < 1e-7) continue;
                        newLoop.Append(Line.CreateBound(p0, p1));
                    }

                    if (newLoop.NumberOfCurves() >= 3)
                        clippedLoops.Add(newLoop);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SoundCalcs] Clip loop rebuild failed: {ex.Message}");
                }
            }

            return clippedLoops.Count > 0 ? clippedLoops : loops; // fallback to unclipped
        }

        /// <summary>
        /// Sutherland-Hodgman polygon clipping: clips subject polygon to the convex or
        /// concave clip polygon boundary.
        /// </summary>
        private static List<(double x, double y)> SutherlandHodgmanClip(
            List<(double x, double y)> subject,
            List<(double x, double y)> clip)
        {
            if (subject.Count < 3 || clip.Count < 3)
                return subject;

            var output = new List<(double x, double y)>(subject);
            int clipCount = clip.Count;

            for (int i = 0; i < clipCount; i++)
            {
                if (output.Count == 0) break;

                var input = new List<(double x, double y)>(output);
                output.Clear();

                var edgeStart = clip[i];
                var edgeEnd = clip[(i + 1) % clipCount];

                for (int j = 0; j < input.Count; j++)
                {
                    var current = input[j];
                    var previous = input[(j + input.Count - 1) % input.Count];

                    bool curInside = IsInsideEdge(current, edgeStart, edgeEnd);
                    bool prevInside = IsInsideEdge(previous, edgeStart, edgeEnd);

                    if (curInside)
                    {
                        if (!prevInside)
                        {
                            var intersection = LineIntersect(previous, current, edgeStart, edgeEnd);
                            if (intersection.HasValue)
                                output.Add(intersection.Value);
                        }
                        output.Add(current);
                    }
                    else if (prevInside)
                    {
                        var intersection = LineIntersect(previous, current, edgeStart, edgeEnd);
                        if (intersection.HasValue)
                            output.Add(intersection.Value);
                    }
                }
            }

            return output;
        }

        private static bool IsInsideEdge(
            (double x, double y) point,
            (double x, double y) edgeStart,
            (double x, double y) edgeEnd)
        {
            // Left side of directed edge = inside (assumes CCW winding of clip polygon)
            // For CW clip polygon this works the same way due to how SH iterates
            return (edgeEnd.x - edgeStart.x) * (point.y - edgeStart.y)
                 - (edgeEnd.y - edgeStart.y) * (point.x - edgeStart.x) >= 0;
        }

        private static (double x, double y)? LineIntersect(
            (double x, double y) a1, (double x, double y) a2,
            (double x, double y) b1, (double x, double y) b2)
        {
            double dx1 = a2.x - a1.x, dy1 = a2.y - a1.y;
            double dx2 = b2.x - b1.x, dy2 = b2.y - b1.y;
            double denom = dx1 * dy2 - dy1 * dx2;

            if (Math.Abs(denom) < 1e-15) return null;

            double t = ((b1.x - a1.x) * dy2 - (b1.y - a1.y) * dx2) / denom;
            return (a1.x + t * dx1, a1.y + t * dy1);
        }

        private static List<(double x, double y)> CollapseCollinear2D(
            List<(double x, double y)> pts)
        {
            if (pts.Count <= 3) return pts;

            var result = new List<(double x, double y)>(pts.Count);
            int n = pts.Count;

            for (int i = 0; i < n; i++)
            {
                var (px, py) = pts[(i + n - 1) % n];
                var (cx, cy) = pts[i];
                var (nx, ny) = pts[(i + 1) % n];

                double cross = (cx - px) * (ny - cy) - (cy - py) * (nx - cx);
                if (Math.Abs(cross) > 1e-10)
                    result.Add((cx, cy));
            }

            return result.Count >= 3 ? result : pts;
        }

        // -----------------------------------------------------------------------
        // Misc utilities
        // -----------------------------------------------------------------------

        private static int Quantise(double value, double origin, double spacing)
            => HeatmapMath.Quantise(value, origin, spacing);
    }
}
