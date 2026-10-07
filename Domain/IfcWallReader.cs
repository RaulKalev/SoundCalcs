using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SoundCalcs.Domain.Ifc
{
    /// <summary>A wall as the original IFC file describes it: its axis, layer build-up and acoustic properties.</summary>
    public class IfcFileWall
    {
        public string GlobalId { get; set; } = "";
        /// <summary>IFC entity, e.g. IFCWALLSTANDARDCASE.</summary>
        public string Entity { get; set; } = "";
        public string Name { get; set; } = "";
        public string ObjectType { get; set; } = "";
        /// <summary>Name of the IfcWallType, if any.</summary>
        public string TypeName { get; set; } = "";

        /// <summary>
        /// Wall centerline in the file's world coordinates (metres, plan). The axis is moved to the middle of
        /// the layer set when the file says where the layers sit relative to it. Empty when the wall has no
        /// usable 'Axis' representation.
        /// </summary>
        public List<Vec2> Centerline { get; set; } = new List<Vec2>();

        /// <summary>Total layer thickness in metres; null without a layer set.</summary>
        public double? ThicknessM { get; set; }

        public List<string> Materials { get; set; } = new List<string>();

        /// <summary>Pset_WallCommon.AcousticRating (instance, else type); null when not set.</summary>
        public string AcousticRating { get; set; }

        public bool? IsExternal { get; set; }

        public override string ToString() => $"{Entity} {GlobalId} '{Name}' {Centerline.Count} pts t={ThicknessM}";
    }

    /// <summary>The walls read from an IFC file.</summary>
    public class IfcFileData
    {
        public string Path { get; set; } = "";
        public string Schema { get; set; } = "";
        /// <summary>Metres per file length unit.</summary>
        public double LengthUnitM { get; set; } = 1.0;
        public List<IfcFileWall> Walls { get; set; } = new List<IfcFileWall>();
        public List<string> Warnings { get; set; } = new List<string>();

        public IfcFileWall Find(string globalId) =>
            string.IsNullOrEmpty(globalId) ? null : Walls.FirstOrDefault(w => w.GlobalId == globalId);
    }

    /// <summary>
    /// Reads walls from an IFC-SPF file (IFC2x3 / IFC4): axis curves through the placement chain, material
    /// layer sets with their offset from the axis, and Pset_WallCommon. Geometry beyond the axis is not read:
    /// the wall shapes come from Revit's link of the same file.
    /// </summary>
    public static class IfcWallReader
    {
        static readonly HashSet<string> WallTypes = new HashSet<string>
            { "IFCWALL", "IFCWALLSTANDARDCASE", "IFCWALLELEMENTEDCASE" };

        static readonly HashSet<string> Kept = new HashSet<string>(WallTypes.Concat(new[]
        {
            "IFCLOCALPLACEMENT", "IFCAXIS2PLACEMENT3D", "IFCAXIS2PLACEMENT2D", "IFCCARTESIANPOINT", "IFCDIRECTION",
            "IFCPRODUCTDEFINITIONSHAPE", "IFCSHAPEREPRESENTATION", "IFCPOLYLINE", "IFCINDEXEDPOLYCURVE",
            "IFCCARTESIANPOINTLIST2D", "IFCCARTESIANPOINTLIST3D", "IFCTRIMMEDCURVE", "IFCLINE", "IFCVECTOR", "IFCCIRCLE",
            "IFCCOMPOSITECURVE", "IFCCOMPOSITECURVESEGMENT",
            "IFCRELASSOCIATESMATERIAL", "IFCMATERIALLAYERSETUSAGE", "IFCMATERIALLAYERSET", "IFCMATERIALLAYER",
            "IFCMATERIAL", "IFCMATERIALLIST",
            "IFCRELDEFINESBYPROPERTIES", "IFCPROPERTYSET", "IFCPROPERTYSINGLEVALUE",
            "IFCRELDEFINESBYTYPE", "IFCWALLTYPE",
            "IFCPROJECT", "IFCUNITASSIGNMENT", "IFCSIUNIT", "IFCCONVERSIONBASEDUNIT", "IFCMEASUREWITHUNIT"
        }));

        public static IfcFileData Read(string path)
        {
            using (var reader = new StreamReader(path, System.Text.Encoding.UTF8, true, 1 << 16))
            {
                IfcFileData data = Read(reader);
                data.Path = path;
                return data;
            }
        }

        public static IfcFileData Read(TextReader reader)
        {
            StepFile file = StepFile.Parse(reader, Kept.Contains);
            return new Context(file).Read();
        }

        // ------------------------------------------------------------------

        private struct Frame
        {
            public Vec3 O, X, Y, Z;

            public static Frame Identity => new Frame { O = Vec3.Zero, X = new Vec3(1, 0, 0), Y = new Vec3(0, 1, 0), Z = new Vec3(0, 0, 1) };

            public Vec3 Point(Vec3 p) => O + X * p.X + Y * p.Y + Z * p.Z;
            public Vec3 Dir(Vec3 d) => X * d.X + Y * d.Y + Z * d.Z;

            public Frame Then(Frame child) => new Frame { O = Point(child.O), X = Dir(child.X), Y = Dir(child.Y), Z = Dir(child.Z) };
        }

        private sealed class Context
        {
            private readonly StepFile _f;
            private readonly Dictionary<int, Frame> _placements = new Dictionary<int, Frame>();
            private double _len = 1.0, _angle = 1.0;

            public Context(StepFile f) { _f = f; }

            public IfcFileData Read()
            {
                var data = new IfcFileData { Schema = _f.Schema };
                ReadUnits();
                data.LengthUnitM = _len;

                // Relationships, by related object id
                var material = new Dictionary<int, StepEntity>();
                var typeOf = new Dictionary<int, StepEntity>();
                var psets = new Dictionary<int, List<StepEntity>>();
                foreach (StepEntity rel in _f.OfType("IFCRELASSOCIATESMATERIAL"))
                {
                    StepEntity m = _f.Get(rel.Arg(5));
                    if (m == null) continue;
                    foreach (int id in Refs(rel.Arg(4)))
                        if (!material.ContainsKey(id) || m.Type == "IFCMATERIALLAYERSETUSAGE") material[id] = m;
                }
                foreach (StepEntity rel in _f.OfType("IFCRELDEFINESBYTYPE"))
                {
                    StepEntity t = _f.Get(rel.Arg(5));
                    if (t == null) continue;
                    foreach (int id in Refs(rel.Arg(4))) typeOf[id] = t;
                }
                foreach (StepEntity rel in _f.OfType("IFCRELDEFINESBYPROPERTIES"))
                {
                    StepEntity p = _f.Get(rel.Arg(5));
                    if (p == null || p.Type != "IFCPROPERTYSET") continue;
                    foreach (int id in Refs(rel.Arg(4)))
                    {
                        if (!psets.TryGetValue(id, out var list)) psets[id] = list = new List<StepEntity>();
                        list.Add(p);
                    }
                }

                int noAxis = 0;
                foreach (StepEntity w in _f.Entities.Values.Where(e => WallTypes.Contains(e.Type)).OrderBy(e => e.Id))
                {
                    var wall = new IfcFileWall
                    {
                        GlobalId = Str(w.Arg(0)),
                        Entity = w.Type,
                        Name = Str(w.Arg(2)),
                        ObjectType = Str(w.Arg(4))
                    };
                    typeOf.TryGetValue(w.Id, out StepEntity wallType);
                    if (wallType != null) wall.TypeName = Str(wallType.Arg(2));

                    // Materials: the occurrence's own (layer set usage), else the type's
                    StepEntity mat = null;
                    if (!material.TryGetValue(w.Id, out mat) && wallType != null) material.TryGetValue(wallType.Id, out mat);
                    double centerShift = ReadMaterial(mat, wall);

                    // Properties: occurrence first, then type
                    var sets = new List<StepEntity>();
                    if (psets.TryGetValue(w.Id, out var own)) sets.AddRange(own);
                    if (wallType != null)
                    {
                        sets.AddRange(Refs(wallType.Arg(5)).Select(_f.Get).Where(e => e != null && e.Type == "IFCPROPERTYSET"));
                        if (psets.TryGetValue(wallType.Id, out var typeSets)) sets.AddRange(typeSets);
                    }
                    ReadProperties(sets, wall);

                    try
                    {
                        Frame frame = Placement(w.Arg(5), 0);
                        List<Vec3> axis = AxisCurve(w.Arg(6));
                        if (axis.Count >= 2)
                        {
                            List<Vec2> local = axis.Select(p => new Vec2(p.X, p.Y)).ToList();
                            if (Math.Abs(centerShift) > 1e-9) local = OffsetLeft(local, centerShift);
                            wall.Centerline = local.Select(p => frame.Point(new Vec3(p.X, p.Y, 0)))
                                .Select(p => new Vec2(p.X, p.Y)).ToList();
                        }
                        else noAxis++;
                    }
                    catch (Exception ex)
                    {
                        noAxis++;
                        data.Warnings.Add($"#{w.Id} {wall.GlobalId}: {ex.Message}");
                    }
                    data.Walls.Add(wall);
                }
                if (noAxis > 0) data.Warnings.Add($"{noAxis} wall(s) without a readable axis");
                return data;
            }

            // ---- units ----

            private void ReadUnits()
            {
                foreach (StepEntity ua in _f.OfType("IFCUNITASSIGNMENT"))
                    foreach (int id in Refs(ua.Arg(0)))
                    {
                        StepEntity u = _f.Get(id);
                        if (u == null) continue;
                        string kind = (u.Arg(1) as StepEnum)?.Name;
                        if (kind == "LENGTHUNIT") _len = UnitScale(u, 0);
                        else if (kind == "PLANEANGLEUNIT") _angle = UnitScale(u, 0);
                    }
            }

            private double UnitScale(StepEntity u, int depth)
            {
                if (u == null || depth > 4) return 1.0;
                if (u.Type == "IFCSIUNIT")
                {
                    switch ((u.Arg(2) as StepEnum)?.Name)
                    {
                        case "MILLI": return 0.001;
                        case "CENTI": return 0.01;
                        case "DECI": return 0.1;
                        case "KILO": return 1000;
                        default: return 1.0;
                    }
                }
                if (u.Type == "IFCCONVERSIONBASEDUNIT")
                {
                    string name = Str(u.Arg(2)).ToUpperInvariant();
                    if (name.Contains("FOOT") || name == "FT") return 0.3048;
                    if (name.Contains("INCH")) return 0.0254;
                    if (name.Contains("DEGREE")) return Math.PI / 180;
                    StepEntity m = _f.Get(u.Arg(3));
                    if (m != null && m.Type == "IFCMEASUREWITHUNIT")
                        return Num(m.Arg(0)) * UnitScale(_f.Get(m.Arg(1)), depth + 1);
                }
                return 1.0;
            }

            // ---- materials and properties ----

            /// <summary>Fills thickness and materials; returns how far the layer middle is left of the axis.</summary>
            private double ReadMaterial(StepEntity mat, IfcFileWall wall)
            {
                if (mat == null) return 0;
                double shift = 0;
                StepEntity set = mat;
                if (mat.Type == "IFCMATERIALLAYERSETUSAGE")
                {
                    set = _f.Get(mat.Arg(0));
                    string dir = (mat.Arg(1) as StepEnum)?.Name ?? "AXIS2";
                    double sense = (mat.Arg(2) as StepEnum)?.Name == "NEGATIVE" ? -1 : 1;
                    double offset = Num(mat.Arg(3)) * _len;
                    double total = LayerTotal(set);
                    if (dir == "AXIS2" && total > 0) shift = offset + sense * total / 2;
                }
                if (set == null) return shift;
                if (set.Type == "IFCMATERIALLAYERSET")
                {
                    double total = LayerTotal(set);
                    if (total > 0) wall.ThicknessM = total;
                    foreach (StepEntity layer in Refs(set.Arg(0)).Select(_f.Get).Where(l => l != null))
                    {
                        string name = Str(_f.Get(layer.Arg(0))?.Arg(0));
                        if (name.Length > 0 && !wall.Materials.Contains(name)) wall.Materials.Add(name);
                    }
                }
                else if (set.Type == "IFCMATERIALLIST")
                {
                    foreach (StepEntity m in Refs(set.Arg(0)).Select(_f.Get).Where(m => m != null))
                        wall.Materials.Add(Str(m.Arg(0)));
                }
                else if (set.Type == "IFCMATERIAL")
                    wall.Materials.Add(Str(set.Arg(0)));
                return shift;
            }

            private double LayerTotal(StepEntity set)
            {
                if (set == null || set.Type != "IFCMATERIALLAYERSET") return 0;
                return Refs(set.Arg(0)).Select(_f.Get).Where(l => l != null).Sum(l => Num(l.Arg(1))) * _len;
            }

            private void ReadProperties(List<StepEntity> sets, IfcFileWall wall)
            {
                foreach (StepEntity set in sets)
                {
                    if (!Str(set.Arg(2)).EndsWith("WallCommon", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (StepEntity p in Refs(set.Arg(4)).Select(_f.Get).Where(p => p != null && p.Type == "IFCPROPERTYSINGLEVALUE"))
                    {
                        string name = Str(p.Arg(0));
                        object value = (p.Arg(2) as StepTyped)?.Value;
                        if (name.Equals("AcousticRating", StringComparison.OrdinalIgnoreCase) && wall.AcousticRating == null)
                        {
                            string v = value is string s ? s : value is double d ? d.ToString(CultureInfo.InvariantCulture)
                                : value?.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) wall.AcousticRating = v.Trim();
                        }
                        else if (name.Equals("IsExternal", StringComparison.OrdinalIgnoreCase) && wall.IsExternal == null)
                        {
                            string v = (value as StepEnum)?.Name;
                            if (v == "T") wall.IsExternal = true;
                            else if (v == "F") wall.IsExternal = false;
                        }
                    }
                }
            }

            // ---- placement ----

            private Frame Placement(object reference, int depth)
            {
                StepEntity e = _f.Get(reference);
                if (e == null || depth > 64) return Frame.Identity;
                if (_placements.TryGetValue(e.Id, out Frame cached)) return cached;

                Frame result;
                if (e.Type == "IFCLOCALPLACEMENT")
                    result = Placement(e.Arg(0), depth + 1).Then(Axis2(_f.Get(e.Arg(1))));
                else
                    result = Axis2(e);
                _placements[e.Id] = result;
                return result;
            }

            private Frame Axis2(StepEntity a)
            {
                if (a == null) return Frame.Identity;
                Vec3 o = Point(a.Arg(0));
                if (a.Type == "IFCAXIS2PLACEMENT2D")
                {
                    Vec3 x2 = Direction(a.Arg(1), new Vec3(1, 0, 0));
                    x2 = new Vec3(x2.X, x2.Y, 0).Normalized();
                    return new Frame { O = o, X = x2, Y = new Vec3(-x2.Y, x2.X, 0), Z = new Vec3(0, 0, 1) };
                }
                Vec3 z = Direction(a.Arg(1), new Vec3(0, 0, 1)).Normalized();
                Vec3 x = Direction(a.Arg(2), Math.Abs(z.X) > 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0));
                x = (x - z * Vec3.Dot(x, z)).Normalized();
                if (x.LengthSquared < 0.5) x = Math.Abs(z.X) > 0.9 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
                return new Frame { O = o, X = x, Y = Vec3.Cross(z, x), Z = z };
            }

            // ---- axis curve ----

            private List<Vec3> AxisCurve(object representation)
            {
                StepEntity shape = _f.Get(representation);
                if (shape == null) return new List<Vec3>();
                foreach (StepEntity rep in Refs(shape.Arg(2)).Select(_f.Get).Where(r => r != null))
                {
                    if (!Str(rep.Arg(1)).Equals("Axis", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (StepEntity item in Refs(rep.Arg(3)).Select(_f.Get))
                    {
                        List<Vec3> pts = Curve(item, 0);
                        if (pts.Count >= 2) return pts;
                    }
                }
                return new List<Vec3>();
            }

            private List<Vec3> Curve(StepEntity c, int depth)
            {
                var pts = new List<Vec3>();
                if (c == null || depth > 8) return pts;
                switch (c.Type)
                {
                    case "IFCPOLYLINE":
                        pts.AddRange(Refs(c.Arg(0)).Select(id => Point(new StepRef(id))));
                        break;
                    case "IFCINDEXEDPOLYCURVE":
                        pts.AddRange(IndexedPolyCurve(c));
                        break;
                    case "IFCTRIMMEDCURVE":
                        pts.AddRange(TrimmedCurve(c));
                        break;
                    case "IFCCOMPOSITECURVE":
                        foreach (StepEntity seg in Refs(c.Arg(0)).Select(_f.Get).Where(s => s != null))
                        {
                            List<Vec3> part = Curve(_f.Get(seg.Arg(2)), depth + 1);
                            if ((seg.Arg(1) as StepEnum)?.Name == "F") part.Reverse();
                            foreach (Vec3 p in part)
                                if (pts.Count == 0 || Vec3.Distance(pts[pts.Count - 1], p) > 1e-9) pts.Add(p);
                        }
                        break;
                }
                return pts;
            }

            private List<Vec3> IndexedPolyCurve(StepEntity c)
            {
                var result = new List<Vec3>();
                StepEntity list = _f.Get(c.Arg(0));
                if (list == null || !(list.Arg(0) is List<object> coords)) return result;
                var pts = coords.OfType<List<object>>().Select(Coord).ToList();
                if (!(c.Arg(1) is List<object> segments) || segments.Count == 0) return pts;

                foreach (StepTyped seg in segments.OfType<StepTyped>())
                {
                    var idx = (seg.Value as List<object> ?? new List<object> { seg.Value })
                        .Select(v => (int)Convert.ToInt64(v, CultureInfo.InvariantCulture) - 1)
                        .Where(i => i >= 0 && i < pts.Count).ToList();
                    List<Vec3> part = seg.Type == "IFCARCINDEX" && idx.Count == 3
                        ? Arc3(pts[idx[0]], pts[idx[1]], pts[idx[2]])
                        : idx.Select(i => pts[i]).ToList();
                    foreach (Vec3 p in part)
                        if (result.Count == 0 || Vec3.Distance(result[result.Count - 1], p) > 1e-9) result.Add(p);
                }
                return result;
            }

            private List<Vec3> TrimmedCurve(StepEntity c)
            {
                var result = new List<Vec3>();
                StepEntity basis = _f.Get(c.Arg(0));
                if (basis == null) return result;
                bool sense = (c.Arg(3) as StepEnum)?.Name != "F";
                var trim1 = c.Arg(1) as List<object> ?? new List<object>();
                var trim2 = c.Arg(2) as List<object> ?? new List<object>();

                if (basis.Type == "IFCLINE")
                {
                    Vec3 p0 = Point(basis.Arg(0));
                    StepEntity vec = _f.Get(basis.Arg(1));
                    Vec3 dir = Direction(vec?.Arg(0), new Vec3(1, 0, 0)).Normalized() * (Num(vec?.Arg(1)) * _len);
                    Vec3 At(List<object> trim)
                    {
                        StepRef pt = trim.OfType<StepRef>().FirstOrDefault();
                        if (pt != null) return Point(pt);
                        StepTyped par = trim.OfType<StepTyped>().FirstOrDefault(t => t.Type == "IFCPARAMETERVALUE");
                        return p0 + dir * Num(par?.Value);
                    }
                    result.Add(At(trim1));
                    result.Add(At(trim2));
                    if (!sense) result.Reverse();
                    return result;
                }

                if (basis.Type == "IFCCIRCLE")
                {
                    Frame f = Axis2(_f.Get(basis.Arg(0)));
                    double r = Num(basis.Arg(1)) * _len;
                    double Angle(List<object> trim)
                    {
                        StepTyped par = trim.OfType<StepTyped>().FirstOrDefault(t => t.Type == "IFCPARAMETERVALUE");
                        if (par != null) return Num(par.Value) * _angle;
                        StepRef pt = trim.OfType<StepRef>().FirstOrDefault();
                        Vec3 p = pt != null ? Point(pt) - f.O : f.X;
                        return Math.Atan2(Vec3.Dot(p, f.Y), Vec3.Dot(p, f.X));
                    }
                    double a0 = Angle(trim1), a1 = Angle(trim2);
                    if (sense) { while (a1 <= a0) a1 += 2 * Math.PI; }
                    else { while (a1 >= a0) a1 -= 2 * Math.PI; }
                    int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(a1 - a0) / (5 * Math.PI / 180)));
                    for (int i = 0; i <= n; i++)
                    {
                        double a = a0 + (a1 - a0) * i / n;
                        result.Add(f.Point(new Vec3(r * Math.Cos(a), r * Math.Sin(a), 0)));
                    }
                }
                return result;
            }

            /// <summary>Circular arc through three points, tessellated in ≤ 5° steps.</summary>
            private static List<Vec3> Arc3(Vec3 a, Vec3 b, Vec3 c)
            {
                double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cx = c.X, cy = c.Y;
                double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
                if (Math.Abs(d) < 1e-12) return new List<Vec3> { a, c };
                double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
                double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;
                double r = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));
                double t0 = Math.Atan2(ay - uy, ax - ux), t1 = Math.Atan2(by - uy, bx - ux), t2 = Math.Atan2(cy - uy, cx - ux);
                bool ccw = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax) > 0;
                double Sweep(double from, double to)
                {
                    double s = to - from;
                    if (ccw) { while (s <= 0) s += 2 * Math.PI; } else { while (s >= 0) s -= 2 * Math.PI; }
                    return s;
                }
                double sweep = Sweep(t0, t2);
                int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (5 * Math.PI / 180)));
                var pts = new List<Vec3>();
                for (int i = 0; i <= n; i++)
                {
                    double t = t0 + sweep * i / n;
                    pts.Add(new Vec3(ux + r * Math.Cos(t), uy + r * Math.Sin(t), a.Z));
                }
                return pts;
            }

            // ---- values ----

            private Vec3 Point(object reference)
            {
                StepEntity p = _f.Get(reference);
                if (p == null || !(p.Arg(0) is List<object> coords)) return Vec3.Zero;
                return Coord(coords);
            }

            private Vec3 Coord(List<object> coords)
            {
                double x = coords.Count > 0 ? Num(coords[0]) : 0, y = coords.Count > 1 ? Num(coords[1]) : 0, z = coords.Count > 2 ? Num(coords[2]) : 0;
                return new Vec3(x * _len, y * _len, z * _len);
            }

            private Vec3 Direction(object reference, Vec3 fallback)
            {
                StepEntity d = _f.Get(reference);
                if (d == null || !(d.Arg(0) is List<object> r) || r.Count < 2) return fallback;
                var v = new Vec3(Num(r[0]), Num(r[1]), r.Count > 2 ? Num(r[2]) : 0);
                return v.LengthSquared < 1e-18 ? fallback : v;
            }

            private static IEnumerable<int> Refs(object list)
            {
                if (list is StepRef single) { yield return single.Id; yield break; }
                if (!(list is List<object> items)) yield break;
                foreach (object o in items)
                    if (o is StepRef r) yield return r.Id;
            }

            private static string Str(object v) => v as string ?? (v as StepTyped)?.Value as string ?? "";

            private static double Num(object v)
            {
                if (v is double d) return d;
                if (v is long l) return l;
                if (v is StepTyped t) return Num(t.Value);
                return 0;
            }
        }

        /// <summary>Moves a polyline sideways by <paramref name="distance"/> (positive = left of its direction).</summary>
        public static List<Vec2> OffsetLeft(List<Vec2> pts, double distance)
        {
            if (pts.Count < 2) return pts.ToList();
            var normals = new List<Vec2>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vec2 d = (pts[i + 1] - pts[i]).Normalized();
                normals.Add(new Vec2(-d.Y, d.X));
            }
            var result = new List<Vec2> { pts[0] + normals[0] * distance };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                // Mitre: along the bisector, so both offset sides stay parallel to the original ones
                Vec2 n = (normals[i - 1] + normals[i]).Normalized();
                double cos = Vec2.Dot(n, normals[i]);
                result.Add(pts[i] + n * (distance / Math.Max(0.2, cos)));
            }
            result.Add(pts[pts.Count - 1] + normals[normals.Count - 1] * distance);
            return result;
        }
    }
}
