using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;

namespace SoundCalcs.Harness
{
    /// <summary>A detail line drawn in the plan view, assigned a wall type (STC).</summary>
    public class WallSpec
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public int Stc { get; set; } = 50;

        /// <summary>SelectBoundary assigns 0.1 m to every picked detail line.</summary>
        public double ThicknessM { get; set; } = 0.1;
    }

    /// <summary>A placed speaker family instance.</summary>
    public class SpeakerSpec
    {
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>Height above the level (ElevationFromLevelM). Also drives ceiling height.</summary>
        public double HeightM { get; set; } = 3.0;

        /// <summary>Horizontal aim for wall-mounted speakers (per-instance drag line).</summary>
        public double FacingX { get; set; } = 1.0;
        public double FacingY { get; set; }

        public SpeakerProfileMapping Profile { get; set; } = new SpeakerProfileMapping();
    }

    /// <summary>Expected value range at a probe location (JSON scenarios).</summary>
    public class ProbeSpec
    {
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double? MinSplDb { get; set; }
        public double? MaxSplDb { get; set; }
        public double? MinSti { get; set; }
        public double? MaxSti { get; set; }
    }

    /// <summary>
    /// Everything a user sets up in the plugin UI before pressing Run, in plain data.
    /// Built-in scenarios are C#; custom ones can be loaded from JSON.
    /// </summary>
    public class ScenarioSpec
    {
        public string Name { get; set; } = "scenario";
        public string Description { get; set; } = "";

        public List<WallSpec> Walls { get; set; } = new List<WallSpec>();
        public List<SpeakerSpec> Speakers { get; set; } = new List<SpeakerSpec>();

        /// <summary>
        /// Analysis boundary. Null = convex hull of all wall endpoints, exactly as
        /// the plugin's "Select Boundary" builds it.
        /// </summary>
        public List<Vec2> Boundary { get; set; }

        public double LevelElevationM { get; set; }
        public double GridSpacingM { get; set; } = 0.5;
        public double ReceiverHeightM { get; set; } = 1.2;
        public double BoundaryOffsetM { get; set; } = 0.3;
        public CalculationQuality Quality { get; set; } = CalculationQuality.Full;
        public EnvironmentSettings Environment { get; set; } = new EnvironmentSettings();

        public List<ProbeSpec> Probes { get; set; } = new List<ProbeSpec>();

        /// <summary>
        /// Test-only knob: give every wall absorption 1.0 so no reflections are
        /// produced (Draft still computes first-order wall reflections), and — as a
        /// fully absorbing room has no diffuse field — enclosure ratio 0. Isolates
        /// direct sound + transmission loss. The plugin itself never sets this.
        /// </summary>
        public bool AnechoicWalls { get; set; }

        public ScenarioSpec Clone(string nameSuffix = "")
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(this);
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<ScenarioSpec>(json);
            copy.Name = Name + nameSuffix;
            return copy;
        }

        /// <summary>Walls as the plugin's WallSegment2D (before end extension).</summary>
        public List<WallSegment2D> WallSegments() => Walls.Select(w => new WallSegment2D
        {
            Start = new Vec2(w.X1, w.Y1),
            End = new Vec2(w.X2, w.Y2),
            BaseElevationM = LevelElevationM,
            HeightM = 3.0,
            ThicknessM = w.ThicknessM
        }).ToList();

        /// <summary>
        /// Build the job input with the same steps and order as
        /// MainViewModel.SelectBoundary + RunAnalysis.
        /// </summary>
        public AcousticJobInput BuildInput()
        {
            var segments = WallSegments();

            // --- Speakers (MainViewModel: "Build sources") ---
            var instances = Speakers.Select((s, i) => new SpeakerInstance
            {
                ElementId = 1000 + i,
                TypeKey = s.Profile.TypeKey,
                Position = new Vec3(s.X, s.Y, LevelElevationM + s.HeightM),
                FacingDirection = new Vec3(s.FacingX, s.FacingY, 0),
                LevelName = "Level 1",
                LevelElevationM = LevelElevationM,
                ElevationFromLevelM = s.HeightM
            }).ToList();

            var sources = new List<ComputeSource>();
            for (int i = 0; i < Speakers.Count; i++)
            {
                sources.Add(new ComputeSource
                {
                    Position = instances[i].Position,
                    FacingDirection = JobInputBuilder.ResolveFacing(
                        Speakers[i].Profile.ProfileSource, instances[i].FacingDirection),
                    Profile = Speakers[i].Profile
                });
            }

            // --- Boundary (MainViewModel.SelectBoundary) ---
            List<Vec2> boundary = Boundary;
            if (boundary == null)
            {
                var pts = new List<Vec2>();
                foreach (var seg in segments) { pts.Add(seg.Start); pts.Add(seg.End); }
                boundary = JobInputBuilder.ConvexHull(pts);
            }
            if (boundary.Count < 3)
                throw new InvalidOperationException(
                    $"Scenario '{Name}' has no usable boundary (need ≥3 wall endpoints or an explicit Boundary).");

            var rooms = new List<RoomPolygon>
            {
                new RoomPolygon
                {
                    Vertices = boundary.ToList(),
                    FloorElevationM = LevelElevationM,
                    Name = "Boundary"
                }
            };

            // --- Receivers, enclosure, ceiling height (MainViewModel.RunAnalysis) ---
            RoomDetector.ComputeEnclosureRatios(rooms, segments);

            var settings = new AnalysisSettings
            {
                GridSpacingM = GridSpacingM,
                ReceiverHeightM = ReceiverHeightM,
                BoundaryOffsetM = BoundaryOffsetM
            };
            var receivers = new List<ReceiverPoint>();
            int globalIndex = 0;
            for (int roomIdx = 0; roomIdx < rooms.Count; roomIdx++)
            {
                var pts = ReceiverGrid.GenerateForPolygon(rooms[roomIdx], settings, globalIndex, roomIdx);
                globalIndex += pts.Count;
                receivers.AddRange(pts);
            }

            JobInputBuilder.ApplyCeilingHeights(rooms, instances);

            var walls = new List<ComputeWall>();
            for (int i = 0; i < segments.Count; i++)
                walls.Add(JobInputBuilder.ToComputeWall(segments[i], Walls[i].Stc));
            if (AnechoicWalls)
                foreach (var w in walls)
                    w.AbsorptionByBand = Enumerable.Repeat(1.0, OctaveBands.Count).ToArray();
            if (AnechoicWalls)
                foreach (var room in rooms)
                    room.EnclosureRatio = 0;

            return new AcousticJobInput
            {
                JobId = "harness_" + Name,
                Sources = sources,
                Receivers = receivers,
                Rooms = rooms,
                Walls = walls,
                Quality = Quality,
                Environment = Environment
            };
        }

        // ---------------------------------------------------------------
        // Convenience builders for scenarios
        // ---------------------------------------------------------------

        public static List<WallSpec> RectangleWalls(double x0, double y0, double x1, double y1, int stc)
        {
            return new List<WallSpec>
            {
                new WallSpec { X1 = x0, Y1 = y0, X2 = x1, Y2 = y0, Stc = stc },
                new WallSpec { X1 = x1, Y1 = y0, X2 = x1, Y2 = y1, Stc = stc },
                new WallSpec { X1 = x1, Y1 = y1, X2 = x0, Y2 = y1, Stc = stc },
                new WallSpec { X1 = x0, Y1 = y1, X2 = x0, Y2 = y0, Stc = stc },
            };
        }

        public static List<Vec2> Rectangle(double x0, double y0, double x1, double y1) =>
            new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };

        public static SpeakerProfileMapping Omni(double splDb = 90) => new SpeakerProfileMapping
        {
            TypeKey = "Test : Omni",
            ProfileSource = ProfileSourceType.SimpleOmni,
            OnAxisSplDb = splDb
        };

        public static SpeakerProfileMapping Cone(double splDb = 90, double halfAngleDeg = 60, double offAxisDb = -12) =>
            new SpeakerProfileMapping
            {
                TypeKey = "Test : Cone",
                ProfileSource = ProfileSourceType.SimpleConical,
                OnAxisSplDb = splDb,
                ConeHalfAngleDeg = halfAngleDeg,
                OffAxisAttenuationDb = offAxisDb
            };

        public static SpeakerProfileMapping WallMount(double splDb = 90, double halfAngleDeg = 60, double offAxisDb = -12) =>
            new SpeakerProfileMapping
            {
                TypeKey = "Test : Wall",
                ProfileSource = ProfileSourceType.WallMounted,
                OnAxisSplDb = splDb,
                ConeHalfAngleDeg = halfAngleDeg,
                OffAxisAttenuationDb = offAxisDb
            };
    }
}
