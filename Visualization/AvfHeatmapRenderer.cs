using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using SoundCalcs.Domain;
using SoundCalcs.Revit;

namespace SoundCalcs.Visualization
{
    /// <summary>
    /// Renders acoustic analysis results as an AVF heatmap on the active Revit view.
    /// Strategy:
    ///   1. Try face-based rendering on host-model floors (smooth interpolated surface)
    ///   2. Fallback to XYZ-based markers (always works, less pretty)
    /// Must be called on the Revit API thread.
    /// </summary>
    public class AvfHeatmapRenderer
    {
        private const string SchemaName = "SoundCalcs SPL";
        private const string StyleName = "SoundCalcs Heatmap";

        /// <summary>
        /// Render results on the given view.
        /// </summary>
        public void Render(Document doc, View view, AcousticJobOutput output)
        {
            if (output == null || output.Results.Count == 0)
            {
                Debug.WriteLine("[SoundCalcs] No results to visualize.");
                return;
            }

            using (Transaction tx = new Transaction(doc, "SoundCalcs: Render Heatmap"))
            {
                tx.Start();

                try
                {
                    // --- Step 1: Get or create SpatialFieldManager ---
                    SpatialFieldManager sfm = SpatialFieldManager.GetSpatialFieldManager(view);
                    if (sfm != null)
                    {
                        sfm.Clear();
                    }
                    else
                    {
                        sfm = SpatialFieldManager.CreateSpatialFieldManager(view, 1);
                    }

                    // --- Step 2: Register result schema ---
                    AnalysisResultSchema schema = new AnalysisResultSchema(SchemaName, "SPL (dB)");
                    schema.SetUnits(new List<string> { "dB" }, new List<double> { 1.0 });
                    schema.CurrentUnits = 0;
                    int schemaIndex = sfm.RegisterResult(schema);

                    // --- Step 3: Try transient surfaces, then host faces, then fallback to XYZ ---
                    bool usedFaces = false;
                    int rendered = 0;

                    // Strategy A: Transient Surfaces (for Linked/Virtual Rooms)
                    // If we have room polygons from the analysis, generate temporary surfaces to paint on.
                    if (output.Rooms != null && output.Rooms.Count > 0)
                    {
                        // DEBUG: Force XYZ markers to verify coordinates visually
                        /*
                        try
                        {
                            rendered = RenderOnTransientSurfaces(doc, sfm, schemaIndex, output);
                            if (rendered > 0)
                            {
                                usedFaces = true;
                                Debug.WriteLine($"[SoundCalcs] Transient surface: rendered {rendered} points on generated surfaces.");
                            }
                        }
                        catch (Exception transEx)
                        {
                            Debug.WriteLine($"[SoundCalcs] Transient rendering failed: {transEx}");
                        }
                        */
                    }

                    // Strategy B: Host Floors (if A failed or no rooms provided)
                    if (rendered == 0)
                    {
                        try
                        {
                            rendered = RenderOnHostFloors(doc, sfm, schemaIndex, output);
                            if (rendered > 0)
                            {
                                usedFaces = true;
                                Debug.WriteLine($"[SoundCalcs] Host face: rendered {rendered} points on floor faces.");
                            }
                        }
                        catch (Exception faceEx)
                        {
                            Debug.WriteLine($"[SoundCalcs] Face-based rendering failed: {faceEx.Message}");
                        }
                    }

                    // Strategy C: Fallback to XYZ markers
                    if (rendered == 0)
                    {
                        Debug.WriteLine("[SoundCalcs] Using XYZ marker fallback.");
                        rendered = RenderXyzPoints(sfm, schemaIndex, output);
                        usedFaces = false;
                    }

                    if (rendered == 0)
                    {
                        tx.RollBack();
                        throw new InvalidOperationException("No valid data points to render.");
                    }

                    // --- Step 4: Display style (MUST match rendering mode) ---
                    try
                    {
                        ElementId styleId = GetOrCreateDisplayStyle(doc, usedFaces);
                        if (styleId != ElementId.InvalidElementId)
                            view.AnalysisDisplayStyleId = styleId;
                    }
                    catch (Exception styleEx)
                    {
                        Debug.WriteLine($"[SoundCalcs] Display style warning: {styleEx.Message}");
                    }

                    tx.Commit();
                    Debug.WriteLine($"[SoundCalcs] Heatmap complete: {rendered} pts, " +
                        $"mode={( usedFaces ? "SURFACE" : "MARKER" )}, " +
                        $"SPL {output.MinSplDb:F1}–{output.MaxSplDb:F1} dB");
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

        /// <summary>
        /// Attempt face-based rendering on horizontal floor faces in the HOST document only.
        /// Linked model face references are unstable and cannot be used with SpatialFieldManager.
        /// </summary>
        private int RenderOnHostFloors(Document doc, SpatialFieldManager sfm,
            int schemaIndex, AcousticJobOutput output)
        {
            var geomOptions = new Options
            {
                ComputeReferences = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            int totalRendered = 0;

            using (var collector = new FilteredElementCollector(doc))
            {
                var floors = collector
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .ToElements();

                Debug.WriteLine($"[SoundCalcs] Found {floors.Count} floor elements in host.");

                foreach (Element floorElem in floors)
                {
                    GeometryElement geomElem = floorElem.get_Geometry(geomOptions);
                    if (geomElem == null) continue;

                    foreach (GeometryObject geomObj in geomElem)
                    {
                        Solid solid = geomObj as Solid;
                        if (solid == null || solid.Faces.Size == 0) continue;

                        foreach (Face face in solid.Faces)
                        {
                            // Only use upward-facing horizontal faces (top of floor)
                            if (!IsUpwardHorizontalFace(face)) continue;

                            Reference faceRef = face.Reference;
                            if (faceRef == null) continue;

                            int count = RenderPointsOnFace(sfm, schemaIndex, face, faceRef, output);
                            totalRendered += count;
                        }
                    }
                }
            }

            return totalRendered;
        }

        /// <summary>
        /// Project receiver results onto a face and create a face-based primitive.
        /// </summary>
        private int RenderPointsOnFace(SpatialFieldManager sfm, int schemaIndex,
            Face face, Reference faceRef, AcousticJobOutput output)
        {
            var uvPoints = new List<UV>();
            var values = new List<ValueAtPoint>();

            // Debug: Check face bounds
            // Debug: Check face bounds
            BoundingBoxUV faceBox = face.GetBoundingBox();
            UV faceCenter = (faceBox.Min + faceBox.Max) * 0.5;
            IO.FileLogger.Log($"[SoundCalcs] Rendering on Face. UV Bounds: {faceBox.Min} to {faceBox.Max} (Center {faceCenter})");

            int projectedCount = 0;
            foreach (ReceiverResult result in output.Results)
            {
                double spl = result.SplDb;
                if (double.IsNaN(spl) || double.IsInfinity(spl)) continue;

                XYZ worldPt = new XYZ(
                    result.Position.X * UnitConversion.MetersToFeet,
                    result.Position.Y * UnitConversion.MetersToFeet,
                    result.Position.Z * UnitConversion.MetersToFeet);

                IntersectionResult ir = face.Project(worldPt);
                
                // Debug first few points
                if (uvPoints.Count < 3)
                {
                    string status = (ir == null) ? "FAIL" : $"OK (Dist={ir.Distance:F2})";
                    IO.FileLogger.Log($"[SoundCalcs] Pt: {worldPt} -> Project: {status}");
                }

                if (ir == null) continue;
                if (ir.Distance > 5.0) continue; // ~1.5m tolerance

                uvPoints.Add(ir.UVPoint);
                values.Add(new ValueAtPoint(new List<double> { spl }));
                projectedCount++;
            }
            IO.FileLogger.Log($"[SoundCalcs] Projected {projectedCount} / {output.Results.Count} points onto face.");

            if (uvPoints.Count < 3) return 0; // Need at least a few points

            int primitiveId = sfm.AddSpatialFieldPrimitive(faceRef);
            FieldDomainPointsByUV domainPoints = new FieldDomainPointsByUV(uvPoints);
            FieldValues fieldValues = new FieldValues(values);
            sfm.UpdateSpatialFieldPrimitive(primitiveId, domainPoints, fieldValues, schemaIndex);

            return uvPoints.Count;
        }

        /// <summary>
        /// Reliable fallback: one XYZ primitive per point, renders as colored markers.
        /// This approach always works regardless of model geometry.
        /// </summary>
        private int RenderXyzPoints(SpatialFieldManager sfm, int schemaIndex, AcousticJobOutput output)
        {
            int rendered = 0;

            foreach (ReceiverResult result in output.Results)
            {
                double spl = result.SplDb;
                if (double.IsNaN(spl) || double.IsInfinity(spl)) continue;

                double xFt = result.Position.X * UnitConversion.MetersToFeet;
                double yFt = result.Position.Y * UnitConversion.MetersToFeet;
                double zFt = result.Position.Z * UnitConversion.MetersToFeet;

                if (rendered < 5)
                {
                    IO.FileLogger.Log($"[SoundCalcs] Marker {rendered}: ({xFt:F3}, {yFt:F3}, {zFt:F3})");
                }

                if (double.IsNaN(xFt) || double.IsNaN(yFt) || double.IsNaN(zFt)) continue;

                int primitiveId = sfm.AddSpatialFieldPrimitive();
                var pointList = new List<XYZ> { new XYZ(xFt, yFt, zFt) };
                FieldDomainPointsByXYZ domainPoints = new FieldDomainPointsByXYZ(pointList);
                FieldValues fieldValues = new FieldValues(
                    new List<ValueAtPoint> { new ValueAtPoint(new List<double> { spl }) });

                sfm.UpdateSpatialFieldPrimitive(primitiveId, domainPoints, fieldValues, schemaIndex);
                rendered++;
            }

            return rendered;
        }

        /// <summary>
        /// Check if a face is approximately horizontal and facing upward (floor top surface).
        /// </summary>
        private bool IsUpwardHorizontalFace(Face face)
        {
            try
            {
                BoundingBoxUV bbUv = face.GetBoundingBox();
                UV midUv = new UV(
                    (bbUv.Min.U + bbUv.Max.U) / 2.0,
                    (bbUv.Min.V + bbUv.Max.V) / 2.0);

                XYZ normal = face.ComputeNormal(midUv);
                return normal.Z > 0.8; // Upward-facing
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Remove all SoundCalcs heatmap data from the view.
        /// </summary>
        public void Clear(Document doc, View view)
        {
            using (Transaction tx = new Transaction(doc, "SoundCalcs: Clear Heatmap"))
            {
                tx.Start();
                try
                {
                    SpatialFieldManager sfm = SpatialFieldManager.GetSpatialFieldManager(view);
                    if (sfm != null)
                        sfm.Clear();

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

        /// <summary>
        /// Create transient DirectShape elements (Generic Models) to represent the room floors,
        /// then render AVF data on their top faces.
        /// </summary>
        private int RenderOnTransientSurfaces(Document doc, SpatialFieldManager sfm,
            int schemaIndex, AcousticJobOutput output)
        {
            // 1. Clear old analysis surfaces
            ClearTransientSurfaces(doc);

            int totalRendered = 0;
            var solids = new Dictionary<ElementId, Solid>();

            // 2. Create new surfaces for each room
            // We must create the DirectShapes first, then define AVF on them.
            // Note: DirectShape creation requires a Transaction, but we are already in one.
            foreach (RoomPolygon room in output.Rooms)
            {
                DirectShape ds = CreateAnalysisSurface(doc, room);
                if (ds != null)
                {
                    // Get the solid from the new element
                    Options opt = new Options { DetailLevel = ViewDetailLevel.Fine };
                    GeometryElement geom = ds.get_Geometry(opt);
                    foreach (GeometryObject obj in geom)
                    {
                        if (obj is Solid s && s.Volume > 0)
                        {
                            solids[ds.Id] = s;
                            break;
                        }
                    }
                }
            }

            // 3. Render on the new surfaces
            // We iterate the results and identify which surface they belong to?
            // Actually, RenderPointsOnFace takes a face and a set of results.
            // It filters results that project onto the face.
            // So we can just iterate our new solids and call RenderPointsOnFace.
            
            foreach (var kvp in solids)
            {
                ElementId elemId = kvp.Key;
                Solid solid = kvp.Value;
                
                // Find top face
                Face topFace = null;
                foreach (Face f in solid.Faces)
                {
                    if (IsUpwardHorizontalFace(f))
                    {
                        topFace = f;
                        break;
                    }
                }

                if (topFace != null)
                {
                    int count = RenderPointsOnFace(sfm, schemaIndex, topFace, topFace.Reference, output);
                    totalRendered += count;
                }
            }

            return totalRendered;
        }

        private void ClearTransientSurfaces(Document doc)
        {
            // Delete elements with our specific name prefix
            var collector = new FilteredElementCollector(doc);
            var toDelete = collector
                .OfClass(typeof(DirectShape))
                .WhereElementIsNotElementType()
                .Where(e => e.Name.StartsWith("SoundCalcs Analysis Plane"))
                .Select(e => e.Id)
                .ToList();

            if (toDelete.Count > 0)
            {
                doc.Delete(toDelete);
                Debug.WriteLine($"[SoundCalcs] Cleared {toDelete.Count} old analysis surfaces.");
            }
        }

        private DirectShape CreateAnalysisSurface(Document doc, RoomPolygon room)
        {
            try
            {
                // Create solid extrusion
                Solid solid = CreateRoomSolid(room);
                if (solid == null) return null;

                DirectShape ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                ds.SetShape(new List<GeometryObject> { solid });
                ds.Name = $"SoundCalcs Analysis Plane - {room.Name}";
                return ds;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoundCalcs] Failed to create analysis surface for {room.Name}: {ex.Message}");
                return null;
            }
        }

        private Solid CreateRoomSolid(RoomPolygon room)
        {
            // Convert vertices to loop
            var loop = new List<Curve>();
            for (int i = 0; i < room.Vertices.Count; i++)
            {
                Vec2 v1 = room.Vertices[i];
                Vec2 v2 = room.Vertices[(i + 1) % room.Vertices.Count];

                XYZ p1 = new XYZ(UnitConversion.MetersToFeet * v1.X, UnitConversion.MetersToFeet * v1.Y, 0);
                XYZ p2 = new XYZ(UnitConversion.MetersToFeet * v2.X, UnitConversion.MetersToFeet * v2.Y, 0);

                if (i == 0) IO.FileLogger.Log($"[SoundCalcs] Render Point 0: v1.X={v1.X:F3}m -> p1.X={p1.X:F3}ft (Factor={UnitConversion.MetersToFeet})");

                if (p1.IsAlmostEqualTo(p2)) continue;
                loop.Add(Line.CreateBound(p1, p2));
            }

            if (loop.Count < 3) return null;

            CurveLoop curveLoop = CurveLoop.Create(loop);
            
            // Validate loop is closed and planar (it is by definition Z=0)
            if (curveLoop.IsOpen()) return null;

            double heightFt = 0.1;

            Solid extrusion = GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { curveLoop },
                XYZ.BasisZ,
                heightFt);

            // Calculate expected center
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var v in room.Vertices)
            {
                minX = Math.Min(minX, v.X);
                maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y);
                maxY = Math.Max(maxY, v.Y);
            }
            XYZ expectedCenterMin = new XYZ(minX * UnitConversion.MetersToFeet, minY * UnitConversion.MetersToFeet, 0);
            XYZ expectedCenterMax = new XYZ(maxX * UnitConversion.MetersToFeet, maxY * UnitConversion.MetersToFeet, 0);
            XYZ expectedCenter = (expectedCenterMin + expectedCenterMax) * 0.5;

            // Calculate actual solid center
            BoundingBoxXYZ bb = extrusion.GetBoundingBox();
            XYZ actualCenter = (bb.Min + bb.Max) * 0.5;

            // Check for shift (e.g. if API centered the geometry)
            XYZ shift = expectedCenter - actualCenter;
            
            // Adjust Z independently (floor elevation)
            double offsetZ = room.FloorElevationM * UnitConversion.MetersToFeet;
            shift += new XYZ(0, 0, offsetZ - shift.Z);

            if (!shift.IsZeroLength())
            {
                IO.FileLogger.Log($"[SoundCalcs] Fixing Geometry Shift: {shift} (Expected center {expectedCenter} vs Actual {actualCenter})");
                Transform trans = Transform.CreateTranslation(shift);
                extrusion = SolidUtils.CreateTransformed(extrusion, trans);
            }

            return extrusion;
        }

        /// <summary>
        /// Create display style matching the rendering mode.
        /// Surface style for face-based data, marker style for XYZ data.
        /// </summary>
        private ElementId GetOrCreateDisplayStyle(Document doc, bool useSurfaceStyle)
        {
            // Delete stale style
            using (var collector = new FilteredElementCollector(doc))
            {
                Element existing = collector
                    .OfClass(typeof(AnalysisDisplayStyle))
                    .FirstOrDefault(e => e.Name == StyleName);

                if (existing != null)
                    doc.Delete(existing.Id);
            }

            // Shared color settings
            AnalysisDisplayColorSettings colorSettings = new AnalysisDisplayColorSettings();
            colorSettings.MinColor = new Color(0, 0, 255);   // Blue = quiet
            colorSettings.MaxColor = new Color(255, 0, 0);    // Red = loud

            AnalysisDisplayLegendSettings legendSettings = new AnalysisDisplayLegendSettings();
            legendSettings.ShowLegend = true;
            legendSettings.ShowUnits = true;
            legendSettings.ShowDataDescription = true;

            AnalysisDisplayStyle style;

            if (useSurfaceStyle)
            {
                var surfaceSettings = new AnalysisDisplayColoredSurfaceSettings();
                surfaceSettings.ShowGridLines = false; // Cleaner look
                style = AnalysisDisplayStyle.CreateAnalysisDisplayStyle(
                    doc, StyleName, surfaceSettings, colorSettings, legendSettings);
                Debug.WriteLine("[SoundCalcs] Display style: SURFACE");
            }
            else
            {
                var markerSettings = new AnalysisDisplayMarkersAndTextSettings();
                markerSettings.MarkerSize = 12;
                markerSettings.MarkerType = AnalysisDisplayStyleMarkerType.Square;

                style = AnalysisDisplayStyle.CreateAnalysisDisplayStyle(
                    doc, StyleName, markerSettings, colorSettings, legendSettings);
                Debug.WriteLine("[SoundCalcs] Display style: MARKER");
            }

            return style.Id;
        }
    }
}
