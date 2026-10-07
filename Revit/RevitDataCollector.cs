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
        /// Read the given speakers from the host model as they are now: position, type, level, A/B line and
        /// the aim correction stored by the viewer. Ids that are deleted or not point-placed family instances
        /// are left out. When <paramref name="abLineParameterName"/> is non-empty, the value of that instance
        /// parameter is stored in <see cref="SpeakerInstance.AbLine"/>.
        /// </summary>
        public List<SpeakerInstance> CollectSpeakersById(IEnumerable<int> elementIds, string abLineParameterName = "")
        {
            var speakers = new List<SpeakerInstance>();

            List<Level> levels;
            using (var levelCollector = new FilteredElementCollector(_doc))
                levels = levelCollector.OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.ProjectElevation).ToList();

            foreach (int id in elementIds.Distinct())
            {
                FamilyInstance fi = _doc.GetElement(RevitCompat.ToElementId(id)) as FamilyInstance;
                if (fi == null) continue;
                SpeakerInstance speaker = ReadSpeaker(fi, levels, abLineParameterName);
                if (speaker != null) speakers.Add(speaker);
            }

            Debug.WriteLine($"[SoundCalcs] Read {speakers.Count} speakers by id");
            return speakers;
        }

        private SpeakerInstance ReadSpeaker(FamilyInstance fi, List<Level> levels, string abLineParameterName)
        {
            LocationPoint locPt = fi.Location as LocationPoint;
            if (locPt == null) return null;

            XYZ position = locPt.Point;
            Vec3 posM = UnitConversion.XyzToVec3(position);
            Vec3 facing = UnitConversion.DirectionToVec3(fi.FacingOrientation);

            // Determine level name and elevation. ProjectElevation is in the same
            // internal coordinates as the location point; Level.Elevation is relative
            // to the level type's Elevation Base (project base / survey point).
            // Face-hosted families (e.g. on a ceiling or a linked host) often have no
            // LevelId: use the reference-level parameter, else the level just below.
            Level level = ResolveLevel(fi, position.Z, levels);
            string levelName = level?.Name ?? "";
            double levelElevM = level != null ? UnitConversion.FtToM(level.ProjectElevation) : 0;

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

            var speaker = new SpeakerInstance
            {
                ElementId = RevitCompat.GetIdValue(fi.Id),
                TypeKey = $"{familyName} : {typeName}",
                Position = posM,
                FacingDirection = facing,
                LevelName = levelName,
                LevelElevationM = levelElevM,
                ElevationFromLevelM = posM.Z - levelElevM,
                AbLine = abLine,
                ModelAimDeg = SpeakerAim.HorizontalAngleDeg(facing)
            };

            // Turn by any aim correction the user made in the viewer
            if (SpeakerRotationStorage.TryReadOffset(fi, speaker.ModelAimDeg, out double offsetDeg))
            {
                speaker.AimOffsetDeg = offsetDeg;
                SpeakerAim.ApplyOffset(speaker);
            }
            return speaker;
        }

        /// <summary>
        /// The level a family instance belongs to: its LevelId, else its schedule /
        /// reference level parameter, else the highest level at or below its elevation.
        /// </summary>
        private Level ResolveLevel(FamilyInstance fi, double zFt, List<Level> levelsByElevation)
        {
            if (fi.LevelId != null && fi.LevelId != ElementId.InvalidElementId &&
                _doc.GetElement(fi.LevelId) is Level direct)
                return direct;

            foreach (BuiltInParameter bip in new[]
                     { BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM })
            {
                Parameter p = fi.get_Parameter(bip);
                ElementId id = p?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId && _doc.GetElement(id) is Level fromParam)
                    return fromParam;
            }

            const double toleranceFt = 0.01;
            Level below = null;
            foreach (Level l in levelsByElevation)
            {
                if (l.ProjectElevation <= zFt + toleranceFt) below = l;
                else break;
            }
            return below ?? levelsByElevation.FirstOrDefault();
        }

        // -----------------------------------------------------------------
        // Boundary lines
        // -----------------------------------------------------------------

        /// <summary>
        /// Read the given detail / model lines as wall segments in meters, one group per line style.
        /// Ids that are deleted or not curve elements are left out of <paramref name="foundIds"/>.
        /// <paramref name="lineZ"/> is the elevation of the lines (0 when none were read).
        /// </summary>
        public List<WallLineGroup> ReadBoundaryLines(IEnumerable<int> elementIds, out List<int> foundIds, out double lineZ)
        {
            var groups = new Dictionary<string, WallLineGroup>();
            foundIds = new List<int>();
            lineZ = 0;

            foreach (int id in elementIds.Distinct())
            {
                CurveElement curveElem = _doc.GetElement(RevitCompat.ToElementId(id)) as CurveElement;
                Curve curve = curveElem?.GeometryCurve;
                if (curve == null) continue;
                foundIds.Add(id);

                string styleName = "Unknown";
                try
                {
                    if (curveElem.LineStyle is GraphicsStyle gs) styleName = gs.Name;
                }
                catch { }

                if (!groups.TryGetValue(styleName, out WallLineGroup grp))
                    groups[styleName] = grp = new WallLineGroup { LineStyleName = styleName };

                IList<XYZ> pts = curve.Tessellate();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    XYZ p0 = pts[i];
                    XYZ p1 = pts[i + 1];
                    lineZ = UnitConversion.FtToM(p0.Z);

                    var seg = new WallSegment2D
                    {
                        Start = new Vec2(UnitConversion.FtToM(p0.X), UnitConversion.FtToM(p0.Y)),
                        End = new Vec2(UnitConversion.FtToM(p1.X), UnitConversion.FtToM(p1.Y)),
                        BaseElevationM = lineZ,
                        HeightM = 3.0,
                        ThicknessM = 0.1
                    };
                    grp.Segments.Add(seg);
                    grp.SegmentCount++;
                    grp.TotalLengthM += seg.Length;
                }
            }

            return groups.Values.ToList();
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

                    // An IFC link is a converted "<file>.ifc.RVT" copy; the original .ifc sits next to it
                    string linkDocPath = "";
                    try { linkDocPath = linkInst.GetLinkDocument()?.PathName ?? ""; } catch { }
                    string ifcPath;
                    bool isIfc = LinkSelection.DetectIfc(filePath, out ifcPath) ||
                                 LinkSelection.DetectIfc(linkDocPath, out ifcPath) ||
                                 LinkSelection.DetectIfc(name, out _);

                    links.Add(new LinkSelection
                    {
                        LinkInstanceId = RevitCompat.GetIdValue(linkInst.Id),
                        LinkName = name,
                        FilePath = filePath,
                        IsIfc = isIfc,
                        IfcFilePath = ifcPath ?? ""
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
        // Walls of a linked IFC (shapes only: Revit rebuilds IFC walls as DirectShapes)
        // -----------------------------------------------------------------

        static readonly string[] IfcEntityParams = { "IfcExportAs", "Export to IFC As", "IfcEntity", "IFC Entity", "IfcType" };
        static readonly string[] ObjectTypeParams = { "IfcObjectType", "ObjectType", "Object Type" };

        /// <summary>
        /// Read the walls of a linked IFC: each wall's top face outlines and vertices in host coordinates
        /// (metres), its IfcGUID, type name, AcousticRating and materials. Walls are the Walls category, plus
        /// Generic Models exported as IfcWall* or whose IFC object type names a wall (IfcBuildingElementProxy).
        /// </summary>
        public List<IfcLinkWall> ReadIfcLinkWalls(int linkInstanceId)
        {
            var result = new List<IfcLinkWall>();
            RevitLinkInstance linkInstance = _doc.GetElement(RevitCompat.ToElementId(linkInstanceId)) as RevitLinkInstance;
            Document linkDoc = linkInstance?.GetLinkDocument();
            if (linkDoc == null) return result;
            Transform xform = linkInstance.GetTotalTransform();

            var elements = new List<Element>();
            using (var walls = new FilteredElementCollector(linkDoc))
                elements.AddRange(walls.OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToElements());
            using (var generic = new FilteredElementCollector(linkDoc))
                elements.AddRange(generic.OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType()
                    .ToElements().Where(IsIfcWallProxy));

            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            foreach (Element elem in elements)
            {
                try
                {
                    var wall = new IfcLinkWall
                    {
                        ElementId = RevitCompat.GetIdValue(elem.Id),
                        IfcGuid = IfcGuid(elem),
                        GroupName = IfcGroupName(linkDoc, elem),
                        AcousticRating = ParamStartingWith(elem, "AcousticRating")
                            ?? ParamStartingWith(linkDoc.GetElement(elem.GetTypeId()), "AcousticRating")
                    };
                    foreach (ElementId mid in elem.GetMaterialIds(false))
                        if (linkDoc.GetElement(mid) is Material m && !wall.Materials.Contains(m.Name)) wall.Materials.Add(m.Name);

                    var geom = new IfcGeometry();
                    GeometryElement ge = elem.get_Geometry(options);
                    if (ge != null) CollectIfcGeometry(ge, xform, geom);
                    if (geom.Points.Count < 3) continue;

                    wall.Points = geom.Points;
                    wall.BaseZ = geom.MinZ;
                    wall.TopZ = geom.MaxZ;
                    wall.Outlines = geom.Outlines();
                    result.Add(wall);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SoundCalcs] IFC wall {elem.Id} skipped: {ex.Message}");
                }
            }
            return result;
        }

        private static bool IsIfcWallProxy(Element elem)
        {
            foreach (string name in IfcEntityParams)
            {
                string v = ParamString(elem, name);
                if (v.StartsWith("IfcWall", StringComparison.OrdinalIgnoreCase) ||
                    v.StartsWith("IfcCurtainWall", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (string name in ObjectTypeParams)
                if (ParamString(elem, name).IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string IfcGuid(Element elem)
        {
            string guid = "";
            try { guid = elem.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString() ?? ""; } catch { }
            return guid.Length > 0 ? guid : ParamString(elem, "IfcGUID");
        }

        /// <summary>The walls-table row name: the IFC type, else object type, else the IFC name without its id.</summary>
        private static string IfcGroupName(Document linkDoc, Element elem)
        {
            string typeName = linkDoc.GetElement(elem.GetTypeId())?.Name ?? "";
            if (typeName.Length > 0 && !typeName.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase)) return typeName;
            foreach (string name in ObjectTypeParams)
            {
                string v = ParamString(elem, name);
                if (v.Length > 0) return v;
            }
            string ifcName = ParamString(elem, "IfcName");
            if (ifcName.Length == 0) ifcName = elem.Name ?? "";
            // Revit-exported IFC names end with the element id: "Basic Wall:Generic - 200mm:123456"
            int colon = ifcName.LastIndexOf(':');
            if (colon > 0 && ifcName.Substring(colon + 1).All(char.IsDigit)) ifcName = ifcName.Substring(0, colon);
            return ifcName.Length > 0 ? ifcName : (elem.Category?.Name ?? "Wall");
        }

        private static string ParamString(Element elem, string name)
        {
            Parameter p = elem?.LookupParameter(name);
            if (p == null || !p.HasValue) return "";
            return (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) ?? "";
        }

        /// <summary>A parameter whose name starts with <paramref name="prefix"/> ("AcousticRating(Pset_WallCommon)").</summary>
        private static string ParamStartingWith(Element elem, string prefix)
        {
            if (elem == null) return null;
            foreach (Parameter p in elem.Parameters)
            {
                if (p?.Definition == null || !p.HasValue) continue;
                if (!p.Definition.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string v = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            return null;
        }

        /// <summary>Plan geometry of one wall: vertices, and its top faces per solid / mesh.</summary>
        private class IfcGeometry
        {
            public readonly List<Vec2> Points = new List<Vec2>();
            public double MinZ = double.MaxValue, MaxZ = double.MinValue;
            // Per solid or mesh: upward faces (height, outline) and upward triangles (height, triangle)
            public readonly List<List<(double Z, List<Vec2> Loop)>> SolidTops = new List<List<(double, List<Vec2>)>>();
            public readonly List<List<(double Z, Vec2 A, Vec2 B, Vec2 C)>> MeshTops = new List<List<(double, Vec2, Vec2, Vec2)>>();

            public void Add(XYZ p)
            {
                Points.Add(new Vec2(UnitConversion.FtToM(p.X), UnitConversion.FtToM(p.Y)));
                double z = UnitConversion.FtToM(p.Z);
                MinZ = Math.Min(MinZ, z);
                MaxZ = Math.Max(MaxZ, z);
            }

            /// <summary>The highest upward faces of each solid / mesh, as outlines.</summary>
            public List<List<Vec2>> Outlines()
            {
                var result = new List<List<Vec2>>();
                foreach (var tops in SolidTops.Where(t => t.Count > 0))
                {
                    double top = tops.Max(t => t.Z);
                    result.AddRange(tops.Where(t => t.Z > top - 0.001).Select(t => t.Loop));
                }
                foreach (var tris in MeshTops.Where(t => t.Count > 0))
                {
                    double top = tris.Max(t => t.Z);
                    List<Vec2> loop = WallFootprint.OutlineFromTriangles(tris.Where(t => t.Z > top - 0.001).Select(t => (t.A, t.B, t.C)));
                    if (loop.Count >= 3) result.Add(loop);
                }
                return result;
            }
        }

        private static void CollectIfcGeometry(GeometryElement ge, Transform xform, IfcGeometry geom)
        {
            foreach (GeometryObject obj in ge)
            {
                if (obj is Solid solid && solid.Faces.Size > 0)
                {
                    var tops = new List<(double, List<Vec2>)>();
                    foreach (Face face in solid.Faces)
                    {
                        foreach (EdgeArray loop in face.EdgeLoops)
                            foreach (Edge edge in loop)
                                foreach (XYZ p in edge.Tessellate()) geom.Add(xform.OfPoint(p));

                        if (!(face is PlanarFace pf) || xform.OfVector(pf.FaceNormal).Z < 0.99) continue;
                        List<Vec2> outer = null;
                        double outerArea = 0, z = 0;
                        foreach (CurveLoop cl in pf.GetEdgesAsCurveLoops())
                        {
                            var pts = new List<Vec2>();
                            foreach (Curve c in cl)
                            {
                                IList<XYZ> tess = c.Tessellate();
                                for (int i = 0; i < tess.Count - 1; i++)
                                {
                                    XYZ q = xform.OfPoint(tess[i]);
                                    pts.Add(new Vec2(UnitConversion.FtToM(q.X), UnitConversion.FtToM(q.Y)));
                                    z = UnitConversion.FtToM(q.Z);
                                }
                            }
                            double area = Math.Abs(SignedArea(pts));
                            if (area > outerArea) { outerArea = area; outer = pts; }
                        }
                        if (outer != null && outer.Count >= 3) tops.Add((z, outer));
                    }
                    geom.SolidTops.Add(tops);
                }
                else if (obj is Mesh mesh)
                {
                    var tris = new List<(double, Vec2, Vec2, Vec2)>();
                    foreach (XYZ v in mesh.Vertices) geom.Add(xform.OfPoint(v));
                    for (int i = 0; i < mesh.NumTriangles; i++)
                    {
                        MeshTriangle t = mesh.get_Triangle(i);
                        XYZ a = xform.OfPoint(t.get_Vertex(0)), b = xform.OfPoint(t.get_Vertex(1)), c = xform.OfPoint(t.get_Vertex(2));
                        XYZ n = (b - a).CrossProduct(c - a);
                        if (n.GetLength() < 1e-12 || Math.Abs(n.Normalize().Z) < 0.99) continue;
                        // Upward in either winding: meshes are not reliably oriented
                        Vec2 A = new Vec2(UnitConversion.FtToM(a.X), UnitConversion.FtToM(a.Y));
                        Vec2 B = new Vec2(UnitConversion.FtToM(b.X), UnitConversion.FtToM(b.Y));
                        Vec2 C = new Vec2(UnitConversion.FtToM(c.X), UnitConversion.FtToM(c.Y));
                        tris.Add((UnitConversion.FtToM((a.Z + b.Z + c.Z) / 3), A, B, C));
                    }
                    // Horizontal triangles at the very top only (the bottom is horizontal too)
                    geom.MeshTops.Add(tris);
                }
                else if (obj is GeometryInstance gi)
                {
                    GeometryElement inst = gi.GetInstanceGeometry(xform);
                    if (inst != null) CollectIfcGeometry(inst, Transform.Identity, geom);
                }
            }
        }

        private static double SignedArea(List<Vec2> pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vec2 p = pts[i], q = pts[(i + 1) % pts.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
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
                        grp = new Domain.WallLineGroup
                        {
                            LineStyleName = typeName,
                            WallType = WallTypeEstimator.Estimate(typeName, thicknessM)
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
    }

}
