using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SoundCalcs.Domain;

namespace SoundCalcs.Revit
{
    /// <summary>
    /// Collects speakers, linked model info, rooms, and geometry from the Revit model.
    /// All methods must be called on the Revit API thread (inside ExternalEvent or command context).
    /// </summary>
    public class RevitDataCollector
    {
        private readonly Document _doc;

        public RevitDataCollector(Document doc)
        {
            _doc = doc;
        }

        // -----------------------------------------------------------------
        // Speakers
        // -----------------------------------------------------------------

        /// <summary>
        /// Collect all family instances of the given built-in category from the host model.
        /// When <paramref name="abLineParameterName"/> is non-empty, the value of that
        /// instance parameter is stored in <see cref="SpeakerInstance.AbLine"/>.
        /// </summary>
        public List<SpeakerInstance> CollectSpeakers(BuiltInCategory category, string abLineParameterName = "")
        {
            var speakers = new List<SpeakerInstance>();

            using (var collector = new FilteredElementCollector(_doc))
            {
                var elements = collector
                    .OfCategory(category)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .ToElements();

                foreach (Element elem in elements)
                {
                    FamilyInstance fi = elem as FamilyInstance;
                    if (fi == null) continue;

                    LocationPoint locPt = fi.Location as LocationPoint;
                    if (locPt == null) continue;

                    XYZ position = locPt.Point;
                    XYZ facing = fi.FacingOrientation;

                    // Determine level name and elevation
                    string levelName = "";
                    double levelElevM = 0;
                    if (fi.LevelId != null && fi.LevelId != ElementId.InvalidElementId)
                    {
                        Level level = _doc.GetElement(fi.LevelId) as Level;
                        if (level != null)
                        {
                            levelName = level.Name;
                            levelElevM = UnitConversion.FtToM(level.Elevation);
                        }
                    }

                    Vec3 posM = UnitConversion.XyzToVec3(position);

                    string familyName = fi.Symbol?.Family?.Name ?? "Unknown";
                    string typeName = fi.Symbol?.Name ?? "Unknown";

                    // Read A/B line designation from the configured parameter, if any
                    string abLine = "";
                    if (!string.IsNullOrEmpty(abLineParameterName))
                    {
                        Parameter abParam = fi.LookupParameter(abLineParameterName);
                        if (abParam != null)
                            abLine = abParam.AsString() ?? abParam.AsValueString() ?? "";
                    }

                    speakers.Add(new SpeakerInstance
                    {
                        ElementId = RevitCompat.GetIdValue(fi.Id),
                        TypeKey = $"{familyName} : {typeName}",
                        Position = posM,
                        FacingDirection = UnitConversion.DirectionToVec3(facing),
                        LevelName = levelName,
                        LevelElevationM = levelElevM,
                        ElevationFromLevelM = posM.Z - levelElevM,
                        AbLine = abLine
                    });

                    // Override horizontal aim with any rotation stored by the user
                    if (SpeakerRotationStorage.TryRead(fi, out double aimDeg))
                    {
                        double rad  = aimDeg * Math.PI / 180.0;
                        double fz   = speakers[speakers.Count - 1].FacingDirection.Z;
                        double hLen = Math.Sqrt(Math.Max(0.0, 1.0 - fz * fz));
                        if (hLen < 1e-6) hLen = 1.0;
                        speakers[speakers.Count - 1].FacingDirection =
                            new Domain.Vec3(Math.Cos(rad) * hLen, Math.Sin(rad) * hLen, fz);
                    }
                }
            }

            Debug.WriteLine($"[SoundCalcs] Collected {speakers.Count} speakers from category {category}");
            return speakers;
        }

        /// <summary>
        /// Group speakers by TypeKey.
        /// </summary>
        public List<SpeakerTypeGroup> GroupSpeakers(List<SpeakerInstance> speakers)
        {
            return speakers
                .GroupBy(s => s.TypeKey)
                .Select(g =>
                {
                    string[] parts = g.Key.Split(new[] { " : " }, StringSplitOptions.None);
                    return new SpeakerTypeGroup
                    {
                        TypeKey = g.Key,
                        FamilyName = parts.Length > 0 ? parts[0] : g.Key,
                        TypeName = parts.Length > 1 ? parts[1] : "",
                        Instances = g.ToList()
                    };
                })
                .OrderBy(g => g.TypeKey)
                .ToList();
        }

        // -----------------------------------------------------------------
        // Linked Models
        // -----------------------------------------------------------------

        /// <summary>
        /// Get all RevitLinkInstances in the host model.
        /// Returns (ElementId, display name, file path).
        /// </summary>
        public List<LinkSelection> GetAvailableLinks()
        {
            var links = new List<LinkSelection>();

            using (var collector = new FilteredElementCollector(_doc))
            {
                var linkInstances = collector
                    .OfClass(typeof(RevitLinkInstance))
                    .ToElements();

                foreach (Element elem in linkInstances)
                {
                    RevitLinkInstance linkInst = elem as RevitLinkInstance;
                    if (linkInst == null) continue;

                    RevitLinkType linkType = _doc.GetElement(linkInst.GetTypeId()) as RevitLinkType;
                    string name = linkType?.Name ?? linkInst.Name;

                    // Try to get file path from ExternalFileReference
                    string filePath = "";
                    if (linkType != null)
                    {
                        try
                        {
                            ExternalFileReference extRef = linkType.GetExternalFileReference();
                            if (extRef != null)
                            {
                                ModelPath modelPath = extRef.GetAbsolutePath();
                                if (modelPath != null)
                                    filePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                            }
                        }
                        catch { /* May not be available */ }
                    }

                    links.Add(new LinkSelection
                    {
                        LinkInstanceId = RevitCompat.GetIdValue(linkInst.Id),
                        LinkName = name,
                        FilePath = filePath
                    });
                }
            }

            Debug.WriteLine($"[SoundCalcs] Found {links.Count} linked models");
            return links;
        }

        // -----------------------------------------------------------------
        // Geometry from Linked Model
        // -----------------------------------------------------------------

        /// <summary>
        /// Extract simplified surfaces (walls, floors, ceilings) from a linked model
        /// as coarse polygons in meters.
        /// </summary>
        public List<Domain.Polygon> ExtractSurfacesFromLink(int linkInstanceId)
        {
            var surfaces = new List<Domain.Polygon>();

            Element linkElem = _doc.GetElement(RevitCompat.ToElementId(linkInstanceId));
            RevitLinkInstance linkInstance = linkElem as RevitLinkInstance;
            if (linkInstance == null)
            {
                Debug.WriteLine("[SoundCalcs] Link instance not found");
                return surfaces;
            }

            Document linkDoc = linkInstance.GetLinkDocument();
            if (linkDoc == null)
            {
                Debug.WriteLine("[SoundCalcs] Link document not loaded");
                return surfaces;
            }

            Transform linkTransform = linkInstance.GetTotalTransform();

            // Collect walls, floors, ceilings
            var categories = new[]
            {
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Ceilings
            };

            foreach (BuiltInCategory cat in categories)
            {
                using (var collector = new FilteredElementCollector(linkDoc))
                {
                    var elements = collector
                        .OfCategory(cat)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    foreach (Element elem in elements)
                    {
                        ExtractElementFaces(elem, linkTransform, surfaces);
                    }
                }
            }

            Debug.WriteLine($"[SoundCalcs] Extracted {surfaces.Count} surfaces from link");
            return surfaces;
        }

        private void ExtractElementFaces(Element elem, Transform transform, List<Domain.Polygon> output)
        {
            try
            {
                Options geomOptions = new Options
                {
                    ComputeReferences = false,
                    DetailLevel = ViewDetailLevel.Coarse
                };

                GeometryElement geomElem = elem.get_Geometry(geomOptions);
                if (geomElem == null) return;

                ExtractFacesFromGeometry(geomElem, transform, output);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoundCalcs] Geometry extraction failed for {elem.Id}: {ex.Message}");
            }
        }

        private void ExtractFacesFromGeometry(GeometryElement geomElem, Transform transform, List<Domain.Polygon> output)
        {
            foreach (GeometryObject geomObj in geomElem)
            {
                if (geomObj is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planar)
                        {
                            ExtractPlanarFace(planar, transform, output);
                        }
                    }
                }
                else if (geomObj is GeometryInstance geomInst)
                {
                    GeometryElement instGeom = geomInst.GetInstanceGeometry(transform);
                    if (instGeom != null)
                    {
                        // Pass Identity because GetInstanceGeometry already applied the transform
                        ExtractFacesFromGeometry(instGeom, Transform.Identity, output);
                    }
                }
            }
        }

        private void ExtractPlanarFace(PlanarFace face, Transform transform, List<Domain.Polygon> output)
        {
            // Use the outer edge loop
            IList<CurveLoop> loops = face.GetEdgesAsCurveLoops();
            if (loops.Count == 0) return;

            CurveLoop outerLoop = loops[0];
            var polygon = new Domain.Polygon();

            foreach (Curve curve in outerLoop)
            {
                XYZ point = transform.OfPoint(curve.GetEndPoint(0));
                polygon.Vertices.Add(UnitConversion.XyzToVec3(point));
            }

            if (polygon.Vertices.Count >= 3)
            {
                output.Add(polygon);
            }
        }

        /// <summary>
        /// Parse a BuiltInCategory name string to the enum value.
        /// </summary>
        public static BuiltInCategory ParseCategory(string categoryName)
        {
            if (Enum.TryParse(categoryName, out BuiltInCategory cat))
                return cat;

            return BuiltInCategory.OST_DataDevices;
        }

        /// <summary>
        /// Extract wall centerline segments from a linked model as 2D segments in meters.
        /// Used for virtual room detection when the host model has no Room elements.
        /// </summary>
        public List<Domain.WallSegment2D> GetWallSegmentsFromLink(int linkInstanceId)
        {
            var segments = new List<Domain.WallSegment2D>();

            Element linkElem = _doc.GetElement(RevitCompat.ToElementId(linkInstanceId));
            RevitLinkInstance linkInstance = linkElem as RevitLinkInstance;
            if (linkInstance == null)
            {
                Debug.WriteLine("[SoundCalcs] Link instance not found for wall extraction.");
                return segments;
            }

            Document linkDoc = linkInstance.GetLinkDocument();
            if (linkDoc == null)
            {
                Debug.WriteLine("[SoundCalcs] Link document not loaded.");
                return segments;
            }

            Debug.WriteLine($"[SoundCalcs] Extracting walls from linked doc: '{linkDoc.Title}'");
            Transform linkTransform = linkInstance.GetTotalTransform();

            using (var collector = new FilteredElementCollector(linkDoc))
            {
                var walls = collector
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .ToElements();

                Debug.WriteLine($"[SoundCalcs] Found {walls.Count} wall elements in link.");

                int skippedLocation = 0;
                int skippedCurve = 0;
                int valid = 0;

                foreach (Element elem in walls)
                {
                    Wall wall = elem as Wall;
                    if (wall == null) continue;

                    // Some walls (like curtain walls or stacked walls) might not have a simple LocationCurve
                    LocationCurve locCurve = wall.Location as LocationCurve;
                    if (locCurve == null) 
                    {
                        skippedLocation++;
                        continue;
                    }

                    Curve curve = locCurve.Curve;
                    if (curve == null) 
                    {
                        skippedCurve++;
                        continue;
                    }

                    // Get start/end in host coords
                    XYZ startPt = linkTransform.OfPoint(curve.GetEndPoint(0));
                    XYZ endPt = linkTransform.OfPoint(curve.GetEndPoint(1));

                    // Convert to meters and project to 2D
                    double startX = UnitConversion.FtToM(startPt.X);
                    double startY = UnitConversion.FtToM(startPt.Y);
                    double endX = UnitConversion.FtToM(endPt.X);
                    double endY = UnitConversion.FtToM(endPt.Y);

                    // Get wall properties
                    double baseElev = UnitConversion.FtToM(startPt.Z);
                    
                    // Try get height parameter, fallback to 3m
                    double height = 3.0;
                    Parameter heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    if (heightParam != null && heightParam.HasValue)
                    {
                        height = UnitConversion.FtToM(heightParam.AsDouble());
                    }
                    else
                    {
                        // Fallback logic for unconnected height
                        BoundingBoxXYZ bb = wall.get_BoundingBox(null);
                        if (bb != null)
                            height = UnitConversion.FtToM(bb.Max.Z - bb.Min.Z);
                    }

                    double thickness = UnitConversion.FtToM(wall.Width);

                    segments.Add(new Domain.WallSegment2D
                    {
                        Start = new Domain.Vec2(startX, startY),
                        End = new Domain.Vec2(endX, endY),
                        BaseElevationM = baseElev,
                        HeightM = height,
                        ThicknessM = thickness
                    });
                    valid++;
                }
                
                Debug.WriteLine($"[SoundCalcs] Extracted {valid} segments. Skipped: {skippedLocation} (no loc), {skippedCurve} (no curve).");
            }

            return segments;
        }

        // -----------------------------------------------------------------
        // Auto wall detection from Revit wall elements
        // -----------------------------------------------------------------

        /// <summary>
        /// Collect all Wall elements from the host document, grouped by wall type name.
        /// Each group gets an STC rating estimated from the wall type name and thickness.
        /// </summary>
        public List<Domain.WallLineGroup> GetHostWallGroups()
        {
            return BuildWallGroupsFromDoc(_doc, pt => pt);
        }

        /// <summary>
        /// Collect all Wall elements from the specified linked model, grouped by wall type name.
        /// </summary>
        public List<Domain.WallLineGroup> GetLinkWallGroups(int linkInstanceId)
        {
            Element linkElem = _doc.GetElement(RevitCompat.ToElementId(linkInstanceId));
            RevitLinkInstance linkInstance = linkElem as RevitLinkInstance;
            if (linkInstance == null) return new List<Domain.WallLineGroup>();

            Document linkDoc = linkInstance.GetLinkDocument();
            if (linkDoc == null) return new List<Domain.WallLineGroup>();

            Transform xform = linkInstance.GetTotalTransform();
            return BuildWallGroupsFromDoc(linkDoc, xform.OfPoint);
        }

        private static List<Domain.WallLineGroup> BuildWallGroupsFromDoc(
            Document doc, Func<XYZ, XYZ> transformPt)
        {
            var groups = new Dictionary<string, Domain.WallLineGroup>();

            using (var collector = new FilteredElementCollector(doc))
            {
                IList<Element> walls = collector
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .ToElements();

                foreach (Element elem in walls)
                {
                    Wall wall = elem as Wall;
                    if (wall == null) continue;

                    LocationCurve locCurve = wall.Location as LocationCurve;
                    if (locCurve?.Curve == null) continue;

                    Curve curve = locCurve.Curve;
                    XYZ s = transformPt(curve.GetEndPoint(0));
                    XYZ e = transformPt(curve.GetEndPoint(1));

                    string typeName = wall.WallType?.Name ?? "Unknown";
                    double thicknessM = UnitConversion.FtToM(wall.Width);

                    if (!groups.TryGetValue(typeName, out Domain.WallLineGroup grp))
                    {
                        int estStc = EstimateStcForWall(typeName, thicknessM);
                        grp = new Domain.WallLineGroup
                        {
                            LineStyleName = typeName,
                            WallType = Domain.WallTypeCatalog.FindClosestByStc(estStc)
                        };
                        groups[typeName] = grp;
                    }

                    var seg = new Domain.WallSegment2D
                    {
                        Start = new Domain.Vec2(UnitConversion.FtToM(s.X), UnitConversion.FtToM(s.Y)),
                        End   = new Domain.Vec2(UnitConversion.FtToM(e.X), UnitConversion.FtToM(e.Y)),
                        BaseElevationM = UnitConversion.FtToM(s.Z),
                        HeightM = 3.0,
                        ThicknessM = thicknessM
                    };
                    grp.Segments.Add(seg);
                    grp.SegmentCount++;
                    grp.TotalLengthM += seg.Length;
                }
            }

            return new List<Domain.WallLineGroup>(groups.Values);
        }

        /// <summary>
        /// Estimate STC from wall type name keywords and physical thickness.
        /// </summary>
        private static int EstimateStcForWall(string typeName, double thicknessM)
        {
            string n = typeName.ToLowerInvariant();

            bool isConcrete = n.Contains("concrete") || n.Contains("beton");
            bool isMasonry  = n.Contains("brick") || n.Contains("masonry") ||
                              n.Contains("cmu")   || n.Contains("block");
            bool isStud     = n.Contains("stud")  || n.Contains("timber") ||
                              n.Contains("gypsum")|| n.Contains("drywall") ||
                              n.Contains("plasterboard");
            bool isGlass    = n.Contains("glass") || n.Contains("glaz") ||
                              n.Contains("curtain");

            if (isGlass)    return 32;

            if (isConcrete)
            {
                if (thicknessM >= 0.25) return 57;
                if (thicknessM >= 0.18) return 55;
                if (thicknessM >= 0.13) return 50;
                return 45;
            }

            if (isMasonry)
            {
                if (thicknessM >= 0.25) return 55;
                if (thicknessM >= 0.18) return 50;
                return 45;
            }

            if (isStud)
            {
                if (thicknessM >= 0.15) return 45;
                if (thicknessM >= 0.10) return 40;
                return 35;
            }

            // Generic: thickness only
            if (thicknessM >= 0.30) return 55;
            if (thicknessM >= 0.20) return 50;
            if (thicknessM >= 0.15) return 45;
            if (thicknessM >= 0.10) return 40;
            if (thicknessM >= 0.07) return 35;
            return 30;
        }
    }

}
