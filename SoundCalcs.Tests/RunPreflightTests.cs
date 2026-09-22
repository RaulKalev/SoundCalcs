using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using Xunit;

namespace SoundCalcs.Tests
{
    public class RunPreflightTests
    {
        static RoomPolygon Box(double x0, double y0, double x1, double y1, double floor = 0)
            => new RoomPolygon
            {
                Vertices = new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) },
                FloorElevationM = floor,
                Name = "Boundary"
            };

        static SpeakerInstance Speaker(double x, double y, double z, string level = "Level 1", double levelElev = 0, string line = "")
            => new SpeakerInstance { Position = new Vec3(x, y, z), LevelName = level, LevelElevationM = levelElev, AbLine = line };

        static PreflightInput Valid()
        {
            var speakers = new List<SpeakerInstance> { Speaker(2, 2, 3), Speaker(8, 6, 3) };
            return new PreflightInput
            {
                Boundary = new List<RoomPolygon> { Box(0, 0, 10, 8) },
                WallLineStyleCount = 2,
                Speakers = speakers,
                IncludedSpeakers = speakers,
                ProjectKey = @"C:\p\a.rvt",
                LayoutProjectKey = @"C:\p\a.rvt",
                SpeakersProjectKey = @"C:\p\a.rvt"
            };
        }

        [Fact]
        public void ValidSetup_OnlyOkItems_AndCanRun()
        {
            var items = RunPreflight.Check(Valid());
            Assert.All(items, i => Assert.Equal(PreflightSeverity.Ok, i.Severity));
            Assert.True(RunPreflight.CanRun(items));
            Assert.Null(RunPreflight.BlockReason(items));
        }

        [Fact]
        public void MissingBoundaryAndSpeakers_Block_WithReason()
        {
            var items = RunPreflight.Check(new PreflightInput());
            Assert.False(RunPreflight.CanRun(items));
            Assert.Contains(items, i => i.IsBlocker && i.FixPage == "Model");
            Assert.Contains(items, i => i.IsBlocker && i.FixPage == "Speakers");
            Assert.StartsWith("No room boundary", RunPreflight.BlockReason(items));
        }

        [Fact]
        public void LineFilterWithNoSpeakers_Blocks()
        {
            var input = Valid();
            input.IncludedSpeakers = new List<SpeakerInstance>();
            input.LineFilterName = "B line";
            var items = RunPreflight.Check(input);
            Assert.Contains(items, i => i.IsBlocker && i.Title.Contains("B line"));
        }

        // The 2026-09-22 job: a boundary saved at a 42.5 m floor with speakers on a level at 45.85 m.
        [Fact]
        public void SpeakersOnAnotherLevel_Warn_WithTheLevelAndOffset()
        {
            var input = Valid();
            input.Boundary = new List<RoomPolygon> { Box(0, 0, 10, 8, floor: 42.5) };
            var s = new List<SpeakerInstance> { Speaker(2, 2, 49.93, "3. korrus", 45.85), Speaker(8, 6, 49.93, "3. korrus", 45.85) };
            input.Speakers = s;
            input.IncludedSpeakers = s;
            var items = RunPreflight.Check(input);

            PreflightItem w = Assert.Single(items, i => i.IsWarning && i.Title.Contains("another level"));
            Assert.Contains("3. korrus", w.Detail);
            Assert.Matches(@"\+3[.,]35 m", w.Detail); // shown in the user's number format
            Assert.True(RunPreflight.CanRun(items)); // a warning, not a blocker
        }

        [Fact]
        public void BoundaryFromAnotherProject_Warns_UnknownOriginDoesNot()
        {
            var input = Valid();
            input.LayoutProjectKey = @"C:\p\other.rvt";
            input.LayoutProjectName = "other";
            Assert.Contains(RunPreflight.Check(input), i => i.IsWarning && i.Title == "Boundary is from another project");

            input.LayoutProjectKey = null; // settings saved before projects were recorded
            Assert.DoesNotContain(RunPreflight.Check(input), i => i.Title == "Boundary is from another project");
        }

        [Fact]
        public void SpeakersOutsideBoundary_AllVsSome()
        {
            var input = Valid();
            var far = new List<SpeakerInstance> { Speaker(200, 200, 3), Speaker(210, 200, 3) };
            input.Speakers = far;
            input.IncludedSpeakers = far;
            Assert.Contains(RunPreflight.Check(input), i => i.Title == "No speaker is inside the boundary");

            var mixed = new List<SpeakerInstance> { Speaker(2, 2, 3), Speaker(200, 200, 3) };
            input.Speakers = mixed;
            input.IncludedSpeakers = mixed;
            Assert.Contains(RunPreflight.Check(input), i => i.Title == "1 of 2 speakers are outside the boundary");
        }

        [Fact]
        public void WallMountedSpeakerOnTheOutline_CountsAsInside()
        {
            var boundary = new List<RoomPolygon> { Box(0, 0, 10, 8) };
            Assert.True(RunPreflight.IsInsideOrNear(boundary, new Vec3(10.3, 4, 2.2)));
            Assert.False(RunPreflight.IsInsideOrNear(boundary, new Vec3(11.0, 4, 2.2)));
        }

        [Fact]
        public void InvalidFields_Block()
        {
            var input = Valid();
            input.InvalidFieldCount = 2;
            var items = RunPreflight.Check(input);
            Assert.False(RunPreflight.CanRun(items));
            Assert.Contains(items, i => i.IsBlocker && i.FixPage == "Room" && i.Detail.StartsWith("2 fields"));
        }
    }
}
