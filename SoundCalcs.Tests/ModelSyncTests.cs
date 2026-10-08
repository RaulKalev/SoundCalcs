using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using Xunit;

namespace SoundCalcs.Tests
{
    public class ModelSyncTests
    {
        const string Wall = "Wall Spk : 10W";
        const string Ceiling = "Ceiling Spk : 6W";

        // A speaker as the collector reads it: facing the family's direction, plus any stored aim correction.
        static SpeakerInstance Read(int id, double x, double y, string type = Wall, double modelAimDeg = 0, double? offset = null)
        {
            var s = new SpeakerInstance
            {
                ElementId = id,
                TypeKey = type,
                Position = new Vec3(x, y, 2.5),
                LevelName = "Level 1",
                FacingDirection = SpeakerAim.WithHorizontalAim(new Vec3(1, 0, 0), modelAimDeg),
                ModelAimDeg = modelAimDeg,
                AimOffsetDeg = offset
            };
            SpeakerAim.ApplyOffset(s);
            return s;
        }

        static SpeakerTypeGroup Group(string type, ProfileSourceType profile, params SpeakerInstance[] speakers)
            => ModelSync.GroupByType(speakers, k => new SpeakerProfileMapping { TypeKey = k, ProfileSource = profile, OnAxisSplDb = 91 }).Single();

        static double AimDeg(SpeakerInstance s) => SpeakerAim.HorizontalAngleDeg(s.FacingDirection).Value;

        [Fact]
        public void MovedSpeaker_TakesNewPosition_KeepsTypeSettings()
        {
            var current = new[] { Group(Wall, ProfileSourceType.WallMounted, Read(1, 0, 0), Read(2, 5, 0)) };
            var fresh = new[] { Read(1, 3, 4), Read(2, 5, 0) };

            var r = ModelSync.MergeSpeakers(current, fresh, new[] { 1, 2 }, null);

            var g = Assert.Single(r.Groups);
            Assert.Same(current[0].Mapping, g.Mapping);
            Assert.Equal(ProfileSourceType.WallMounted, g.Mapping.ProfileSource);
            Assert.Equal(new Vec3(3, 4, 2.5), g.Instances.Single(i => i.ElementId == 1).Position);
            Assert.Equal(1, r.Moved);
            Assert.Equal(0, r.Added);
            Assert.Empty(r.MissingIds);
        }

        [Fact]
        public void AimCorrection_NotInModel_IsKeptFromList_RelativeToNewFacing()
        {
            // Aimed 30° off the family's facing (East) in the viewer, but the model has no stored aim.
            var aimed = Read(1, 0, 0, modelAimDeg: 0, offset: 30);
            var current = new[] { Group(Wall, ProfileSourceType.WallMounted, aimed) };

            // Moved to the opposite wall: the family now faces West.
            var fresh = new[] { Read(1, 10, 0, modelAimDeg: 180) };
            var r = ModelSync.MergeSpeakers(current, fresh, new[] { 1 }, null);

            var s = r.Groups[0].Instances[0];
            Assert.Equal(30, s.AimOffsetDeg);
            Assert.Equal(-150, AimDeg(s), 6);
            Assert.Equal(1, r.AimsKept);
        }

        [Fact]
        public void AimCorrection_StoredInModel_Wins()
        {
            var current = new[] { Group(Wall, ProfileSourceType.WallMounted, Read(1, 0, 0, offset: 30)) };
            var fresh = new[] { Read(1, 0, 0, modelAimDeg: 90, offset: -45) };

            var s = ModelSync.MergeSpeakers(current, fresh, new[] { 1 }, null).Groups[0].Instances[0];

            Assert.Equal(-45, s.AimOffsetDeg);
            Assert.Equal(45, AimDeg(s), 6);
        }

        [Fact]
        public void SpeakerWithoutCorrection_FollowsFamily()
        {
            var current = new[] { Group(Wall, ProfileSourceType.WallMounted, Read(1, 0, 0, modelAimDeg: 0)) };
            var fresh = new[] { Read(1, 0, 0, modelAimDeg: 90) };

            var s = ModelSync.MergeSpeakers(current, fresh, new[] { 1 }, null).Groups[0].Instances[0];

            Assert.Null(s.AimOffsetDeg);
            Assert.Equal(90, AimDeg(s), 6);
        }

        [Fact]
        public void DeletedSpeaker_IsReportedMissing_AndEmptyTypeDropped()
        {
            var current = new[]
            {
                Group(Wall, ProfileSourceType.WallMounted, Read(1, 0, 0)),
                Group(Ceiling, ProfileSourceType.SimpleConical, Read(2, 5, 5, Ceiling))
            };
            var r = ModelSync.MergeSpeakers(current, new[] { Read(1, 0, 0) }, new[] { 1, 2 }, null);

            Assert.Equal(new[] { 2 }, r.MissingIds);
            Assert.Equal(new[] { Wall }, r.Groups.Select(g => g.TypeKey));
        }

        [Fact]
        public void ChangedType_MovesToThatTypesGroup_WithItsSettings()
        {
            var current = new[]
            {
                Group(Wall, ProfileSourceType.WallMounted, Read(1, 0, 0), Read(2, 1, 0)),
                Group(Ceiling, ProfileSourceType.SimpleConical, Read(3, 5, 5, Ceiling))
            };
            var fresh = new[] { Read(1, 0, 0), Read(2, 1, 0, Ceiling), Read(3, 5, 5, Ceiling) };

            var r = ModelSync.MergeSpeakers(current, fresh, new[] { 1, 2, 3 }, null);

            Assert.Equal(1, r.Retyped);
            var ceiling = r.Groups.Single(g => g.TypeKey == Ceiling);
            Assert.Same(current[1].Mapping, ceiling.Mapping);
            Assert.Equal(new[] { 2, 3 }, ceiling.Instances.Select(i => i.ElementId).OrderBy(i => i));
        }

        [Fact]
        public void NewType_UsesSavedMapping_ElseDefaults()
        {
            var saved = new[] { new SpeakerProfileMapping { TypeKey = Ceiling, ProfileSource = ProfileSourceType.SimpleConical, OnAxisSplDb = 88 } };
            var fresh = new[] { Read(1, 0, 0, Ceiling), Read(2, 0, 0, "Other : X") };

            var r = ModelSync.MergeSpeakers(null, fresh, new[] { 1, 2 }, saved);

            Assert.Same(saved[0], r.Groups.Single(g => g.TypeKey == Ceiling).Mapping);
            var other = r.Groups.Single(g => g.TypeKey == "Other : X");
            Assert.Equal("Other", other.FamilyName);
            Assert.Equal("X", other.TypeName);
            Assert.Equal("Other : X", other.Mapping.TypeKey);
            Assert.Equal(2, r.Added);
        }

        [Fact]
        public void OffsetFor_RoundTripsThroughApplyOffset()
        {
            var s = Read(1, 0, 0, modelAimDeg: 170);
            s.AimOffsetDeg = SpeakerAim.OffsetFor(-170, s.ModelAimDeg);
            Assert.Equal(20, s.AimOffsetDeg.Value, 6);   // the short way round
            SpeakerAim.ApplyOffset(s);
            Assert.Equal(-170, AimDeg(s), 6);
        }

        [Fact]
        public void WallSettings_GuessesAreReplaced_UserChoicesKept()
        {
            var glass = WallTypeCatalog.FindByKey("glass_single");
            var concrete = WallTypeCatalog.FindByKey("concrete_200");
            var previous = new[]
            {
                new WallLineGroup { LineStyleName = "Guess", WallType = glass, UserEdited = false },
                new WallLineGroup { LineStyleName = "Chosen", WallType = glass, UserEdited = true }
            };
            var fresh = new List<WallLineGroup>
            {
                new WallLineGroup { LineStyleName = "Guess", WallType = concrete, UserEdited = false },
                new WallLineGroup { LineStyleName = "Chosen", WallType = concrete, UserEdited = false }
            };
            Assert.Equal(1, ModelSync.KeepWallSettings(previous, fresh));
            Assert.Same(concrete, fresh[0].WallType);
            Assert.False(fresh[0].UserEdited);
            Assert.Same(glass, fresh[1].WallType);
            Assert.True(fresh[1].UserEdited);
        }

        [Fact]
        public void WallSettings_KeptByLineStyle_NewStylesUntouched()
        {
            var concrete = WallTypeCatalog.FindByKey("concrete_200");
            var glass = WallTypeCatalog.FindByKey("glass_single");
            var previous = new[]
            {
                new WallLineGroup { LineStyleName = "Wide Lines", WallType = glass, HeightM = 1.5 },
                new WallLineGroup { LineStyleName = "Gone", WallType = glass }
            };
            var fresh = new List<WallLineGroup>
            {
                new WallLineGroup { LineStyleName = "Wide Lines", WallType = concrete },
                new WallLineGroup { LineStyleName = "Thin Lines", WallType = concrete }
            };

            int kept = ModelSync.KeepWallSettings(previous, fresh);

            Assert.Equal(1, kept);
            Assert.Same(glass, fresh[0].WallType);
            Assert.Equal(1.5, fresh[0].HeightM);
            Assert.Same(concrete, fresh[1].WallType);
            Assert.Equal(0, fresh[1].HeightM);
        }
    }
}
