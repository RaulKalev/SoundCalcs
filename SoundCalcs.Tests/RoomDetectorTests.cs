using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using Xunit;

namespace SoundCalcs.Tests
{
    public class RoomDetectorTests
    {
        static WallSegment2D W(double x1, double y1, double x2, double y2) =>
            new WallSegment2D { Start = new Vec2(x1, y1), End = new Vec2(x2, y2), HeightM = 3, ThicknessM = 0.1 };

        static List<WallSegment2D> Rect(double x0, double y0, double x1, double y1) => new List<WallSegment2D>
        {
            W(x0, y0, x1, y0), W(x1, y0, x1, y1), W(x1, y1, x0, y1), W(x0, y1, x0, y0)
        };

        // 20 × 10 m split at x = 12 into 120 m² and 80 m²
        static List<WallSegment2D> TwoRooms()
        {
            var w = Rect(0, 0, 20, 10);
            w.Add(W(12, 0, 12, 10));
            return w;
        }

        static List<WallSegment2D> Transform(List<WallSegment2D> walls, double angleRad, double dx, double dy) =>
            walls.Select(w => new WallSegment2D
            {
                Start = Rot(w.Start, angleRad, dx, dy), End = Rot(w.End, angleRad, dx, dy), HeightM = w.HeightM, ThicknessM = w.ThicknessM
            }).ToList();

        static Vec2 Rot(Vec2 p, double a, double dx, double dy) =>
            new Vec2(Math.Cos(a) * p.X - Math.Sin(a) * p.Y + dx, Math.Sin(a) * p.X + Math.Cos(a) * p.Y + dy);

        static double[] Areas(List<RoomPolygon> rooms) => rooms.Select(r => Math.Round(r.Area, 1)).OrderBy(a => a).ToArray();

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.05)]
        [InlineData(0.3)]
        [InlineData(0.785)]
        [InlineData(2.0)]
        [InlineData(-1.1)]
        public void RotatedLayout_FindsBothRooms(double angle)
        {
            // Any wall direction, far from the origin (project coordinates)
            var rooms = RoomDetector.DetectRooms(Transform(TwoRooms(), angle, 5000.123, -1234.567), 0);
            Assert.Equal(new[] { 80.0, 120.0 }, Areas(rooms));
        }

        [Fact]
        public void PartitionEndSlightlySkewed_StillTwoRooms()
        {
            var w = Rect(0, 0, 20, 10);
            w.Add(W(12, 0, 12.005, 10));
            Assert.Equal(2, RoomDetector.DetectRooms(w, 0).Count);
        }

        [Theory]
        [InlineData(0.3)]
        [InlineData(0.6)]
        [InlineData(0.7)]
        [InlineData(0.9)]
        [InlineData(1.2)]
        public void DoorGapInThePartition_MergesTheRooms(double gap)
        {
            // Partition with a door: two collinear pieces whose ends face each other across the gap
            var w = Rect(0, 0, 20, 10);
            w.Add(W(12, 0, 12, 4));
            w.Add(W(12, 4 + gap, 12, 10));
            var rooms = RoomDetector.DetectRooms(w, 0);
            Assert.Equal(new[] { 200.0 }, Areas(rooms));
        }

        [Theory]
        [InlineData(0.01)]
        [InlineData(0.05)]
        [InlineData(0.1)]
        public void JointBetweenCollinearPanels_DoesNotOpenTheRoom(double joint)
        {
            // The facade of the 80 m² room is two glass panels with a joint between them (an IFC curtain wall)
            var w = Rect(0, 0, 20, 10);
            w.RemoveAt(2);                                   // north side
            w.Add(W(20, 10, 13 + joint, 10));
            w.Add(W(13, 10, 0, 10));
            w.Add(W(12, 0, 12, 10));
            Assert.Equal(new[] { 80.0, 120.0 }, Areas(RoomDetector.DetectRooms(w, 0)));
        }

        [Fact]
        public void CornersThatDoNotQuiteMeet_AreClosed()
        {
            // Detail lines: 10 cm short at one corner, 15 cm overshoot at another, partition 20 cm short of a wall
            var w = new List<WallSegment2D>
            {
                W(0, 0, 19.9, 0), W(20, 0.1, 20, 10.15), W(20, 10, -0.15, 10), W(0, 10, 0, 0),
                W(12, 0.2, 12, 10)
            };
            Assert.Equal(new[] { 80.0, 120.0 }, Areas(RoomDetector.DetectRooms(w, 0)));
        }

        [Fact]
        public void FreeStandingCloset_IsAHoleInTheRoomAroundIt()
        {
            var w = Rect(0, 0, 20, 10);
            w.AddRange(Rect(5, 3, 8, 6));
            var rooms = RoomDetector.DetectRooms(w, 0);
            Assert.Equal(new[] { 9.0, 191.0 }, Areas(rooms));
            RoomPolygon closet = rooms[0], room = rooms[1];
            Assert.True(closet.ContainsPoint(new Vec2(6, 4)));
            Assert.False(room.ContainsPoint(new Vec2(6, 4)));
            Assert.True(room.ContainsPoint(new Vec2(2, 2)));
        }

        [Fact]
        public void GridDrawnAsSeparateRectangles_SharedWallsDuplicated_NineRooms()
        {
            var w = new List<WallSegment2D>();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    w.AddRange(Rect(i * 6, j * 5, i * 6 + 6, j * 5 + 5));
            var rooms = RoomDetector.DetectRooms(w, 0);
            Assert.Equal(9, rooms.Count);
            Assert.All(rooms, r => Assert.Equal(30, r.Area, 6));
        }

        [Fact]
        public void DoubleStudWall_DrawnAsTwoLines_KeepsBothRooms()
        {
            var w = Rect(0, 0, 20, 10);
            w.Add(W(10, 0, 10, 10));
            w.Add(W(10.12, 0, 10.12, 10));
            var rooms = RoomDetector.DetectRooms(w, 0);
            Assert.Equal(new[] { 98.8, 100.0 }, Areas(rooms));   // the 12 cm gap between the lines is no room
        }

        [Fact]
        public void LShapedRoom_IsOneConcaveRoom()
        {
            var w = new List<WallSegment2D>
            {
                W(0, 0, 16, 0), W(16, 0, 16, 8), W(16, 8, 8, 8), W(8, 8, 8, 16), W(8, 16, 0, 16), W(0, 16, 0, 0)
            };
            Assert.Equal(new[] { 192.0 }, Areas(RoomDetector.DetectRooms(w, 0)));
        }

        [Fact]
        public void Boundary_OfAnLShapedBuilding_IsItsOutline_NotTheHull()
        {
            var w = new List<WallSegment2D>
            {
                W(0, 0, 16, 0), W(16, 0, 16, 8), W(16, 8, 8, 8), W(8, 8, 8, 16), W(8, 16, 0, 16), W(0, 16, 0, 0),
                W(0, 8, 8, 8)
            };
            var boundary = new RoomPolygon { Vertices = JobInputBuilder.BoundaryFromWalls(w) };
            Assert.Equal(192, boundary.Area, 6);
            Assert.False(boundary.ContainsPoint(new Vec2(12, 12)));   // the notch is outside
        }

        [Fact]
        public void Boundary_OfAnOpenLayout_IsTheHull()
        {
            // Three sides only: no closed cycle
            var w = new List<WallSegment2D> { W(0, 0, 10, 0), W(10, 0, 10, 8), W(0, 8, 0, 0) };
            Assert.Equal(80, new RoomPolygon { Vertices = JobInputBuilder.BoundaryFromWalls(w) }.Area, 6);
        }

        [Fact]
        public void BoundaryFromWallGroups_IsTheWallsOutline_OnTheirFloor_IgnoringOpenings()
        {
            var walls = new WallLineGroup { LineStyleName = "Concrete" };
            walls.Segments.AddRange(Rect(0, 0, 20, 10));
            // An "Open (No Wall)" line sticking out of the building doesn't widen the boundary
            var open = new WallLineGroup { LineStyleName = "Door", WallType = WallTypeCatalog.FindByKey("open") };
            open.Segments.Add(W(20, 5, 30, 5));

            RoomPolygon b = JobInputBuilder.BoundaryFromWallGroups(new[] { walls, open }, 31.2);
            Assert.Equal(200, b.Area, 6);
            Assert.Equal(31.2, b.FloorElevationM);
            Assert.Null(JobInputBuilder.BoundaryFromWallGroups(new[] { open }, 0));
        }

        [Theory]
        [InlineData(0.0, 3.0, 1.2, true)]     // a wall of the floor
        [InlineData(3.0, 6.0, 1.2, false)]    // the floor above
        [InlineData(-3.0, 0.0, 1.2, false)]   // the floor below
        [InlineData(0.0, 30.0, 16.2, true)]   // a curtain wall through every storey
        [InlineData(1.2, 3.0, 1.2, true)]     // starting right at the cut plane
        public void StandsAt_KeepsTheWallsTheCutPlanePassesThrough(double baseM, double topM, double cutM, bool expected)
        {
            Assert.Equal(expected, JobInputBuilder.StandsAt(baseM, topM, cutM));
        }

        [Fact]
        public void OpenArea_HasTheRoomsCutOut()
        {
            // 20 × 10 boundary, one closed 5 × 4 room in a corner, the rest open
            var walls = new List<WallSegment2D> { W(0, 4, 5, 4), W(5, 4, 5, 0), W(0, 0, 5, 0), W(0, 0, 0, 4) };
            var boundary = new RoomPolygon { Vertices = { new Vec2(0, 0), new Vec2(20, 0), new Vec2(20, 10), new Vec2(0, 10) } };
            var built = JobInputBuilder.BuildRoomsAndReceivers(new List<RoomPolygon> { boundary }, walls,
                new AnalysisSettings { GridSpacingM = 1, BoundaryOffsetM = 0.3, ReceiverHeightM = 1.2 });
            RoomPolygon open = built.Rooms.Single(r => r.Name.Contains("Open"));
            Assert.Equal(180, open.Area, 6);
            Assert.False(open.ContainsPoint(new Vec2(2, 2)));
            Assert.All(built.Receivers.Where(r => r.Position.X < 5 && r.Position.Y < 4),
                r => Assert.Equal(built.Rooms.FindIndex(x => !x.Name.Contains("Open")), r.RoomIndex));
        }

        [Fact]
        public void Receivers_KeepTheOffsetFromSlantedEdges()
        {
            // A triangle: its bounding box is far from the slanted edge
            var tri = new RoomPolygon { Vertices = { new Vec2(0, 0), new Vec2(20, 0), new Vec2(0, 20) } };
            var pts = ReceiverGrid.GenerateForPolygon(tri, new AnalysisSettings { GridSpacingM = 0.5, BoundaryOffsetM = 0.5, ReceiverHeightM = 1.2 });
            Assert.NotEmpty(pts);
            Assert.All(pts, p => Assert.True((20 - p.Position.X - p.Position.Y) / Math.Sqrt(2) >= 0.5 - 1e-9, p.Position.ToString()));
        }

        [Theory]
        [InlineData("A", "A", true)]
        [InlineData(" a ", "A", true)]
        [InlineData("A-line", "A", true)]
        [InlineData("Line B", "B", true)]
        [InlineData("B", "A", false)]
        [InlineData("AB", "A", false)]
        [InlineData("Bar", "B", false)]
        [InlineData("", "A", false)]
        public void AbLineValues(string value, string line, bool expected) =>
            Assert.Equal(expected, JobInputBuilder.IsOnLine(value, line));

        [Fact]
        public void FreeStandingWallInsideARoom_DoesNotSplitIt()
        {
            var w = Rect(0, 0, 20, 10);
            w.Add(W(5, 5, 15, 5));   // touches nothing
            w.Add(W(0, 7, 4, 7));    // stub off the outer wall
            Assert.Equal(new[] { 200.0 }, Areas(RoomDetector.DetectRooms(w, 0)));
        }
    }
}
