using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Horizontal aim of a speaker: the family's facing in Revit plus the user's correction from the viewer.
    /// </summary>
    public static class SpeakerAim
    {
        /// <summary>Horizontal angle of <paramref name="direction"/> in degrees (0 = East, CCW), or null when it is vertical.</summary>
        public static double? HorizontalAngleDeg(Vec3 direction)
        {
            if (Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y) < 1e-6) return null;
            return Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
        }

        /// <summary>Angle in (−180, 180].</summary>
        public static double Normalize(double deg)
        {
            deg %= 360.0;
            if (deg <= -180.0) deg += 360.0;
            else if (deg > 180.0) deg -= 360.0;
            return deg;
        }

        /// <summary><paramref name="facing"/> turned to <paramref name="aimDeg"/> in plan, keeping its vertical component.</summary>
        public static Vec3 WithHorizontalAim(Vec3 facing, double aimDeg)
        {
            double rad = aimDeg * Math.PI / 180.0;
            double fz = facing.Z;
            double hLen = Math.Sqrt(Math.Max(0.0, 1.0 - fz * fz));
            if (hLen < 1e-6) hLen = 1.0;
            return new Vec3(Math.Cos(rad) * hLen, Math.Sin(rad) * hLen, fz);
        }

        /// <summary>The correction that aims a speaker whose family faces <paramref name="modelAimDeg"/> at <paramref name="aimDeg"/>.</summary>
        public static double OffsetFor(double aimDeg, double? modelAimDeg)
            => Normalize(aimDeg - (modelAimDeg ?? 0.0));

        /// <summary>Turns the speaker by its stored correction, if it has one.</summary>
        public static void ApplyOffset(SpeakerInstance speaker)
        {
            if (speaker.AimOffsetDeg is double offset)
                speaker.FacingDirection = WithHorizontalAim(speaker.FacingDirection,
                    Normalize((speaker.ModelAimDeg ?? 0.0) + offset));
        }
    }

    /// <summary>What a speaker refresh changed.</summary>
    public class SpeakerSyncResult
    {
        /// <summary>The new groups, one per type, ordered by type.</summary>
        public List<SpeakerTypeGroup> Groups { get; set; } = new List<SpeakerTypeGroup>();

        /// <summary>Speakers already in the list whose position, height or level changed.</summary>
        public int Moved { get; set; }

        /// <summary>Speakers already in the list whose family or type changed.</summary>
        public int Retyped { get; set; }

        /// <summary>Speakers added that were not in the list before.</summary>
        public int Added { get; set; }

        /// <summary>Speakers in the list that are no longer in the model.</summary>
        public List<int> MissingIds { get; set; } = new List<int>();

        /// <summary>Speakers that keep an aim correction made in the viewer.</summary>
        public int AimsKept { get; set; }
    }

    /// <summary>
    /// Brings the picked speakers and the traced walls up to date with the model without losing what the
    /// user set in SoundCalcs: speaker type settings, aim corrections, wall types and heights.
    /// </summary>
    public static class ModelSync
    {
        /// <summary>Distance below which a speaker counts as not moved (metres).</summary>
        public const double MoveToleranceM = 0.001;

        /// <summary>
        /// Rebuilds the speaker groups from <paramref name="fresh"/> (the speakers as read from the model now).
        /// Each type keeps the settings of its current group, else of <paramref name="savedMappings"/>, else
        /// defaults. A speaker the model has no aim correction for keeps the one it had in the list.
        /// <paramref name="wantedIds"/> are the speakers that should be there; those not in
        /// <paramref name="fresh"/> are reported missing.
        /// </summary>
        public static SpeakerSyncResult MergeSpeakers(IEnumerable<SpeakerTypeGroup> current, IEnumerable<SpeakerInstance> fresh,
            IEnumerable<int> wantedIds, IEnumerable<SpeakerProfileMapping> savedMappings)
        {
            var currentGroups = (current ?? Enumerable.Empty<SpeakerTypeGroup>()).ToList();
            var oldById = new Dictionary<int, SpeakerInstance>();
            foreach (var g in currentGroups)
                foreach (var inst in g.Instances)
                    oldById[inst.ElementId] = inst;

            var result = new SpeakerSyncResult();
            var seen = new HashSet<int>();
            var instances = new List<SpeakerInstance>();
            foreach (SpeakerInstance inst in fresh ?? Enumerable.Empty<SpeakerInstance>())
            {
                if (inst == null || !seen.Add(inst.ElementId)) continue;
                instances.Add(inst);

                if (!oldById.TryGetValue(inst.ElementId, out SpeakerInstance old))
                {
                    result.Added++;
                    if (inst.AimOffsetDeg != null) result.AimsKept++;
                    continue;
                }

                if (Vec3.Distance(inst.Position, old.Position) > MoveToleranceM || inst.LevelName != old.LevelName)
                    result.Moved++;
                if (inst.TypeKey != old.TypeKey)
                    result.Retyped++;

                // The model's stored correction wins; otherwise keep the one from the list
                // (e.g. it could not be written to the model).
                if (inst.AimOffsetDeg == null && old.AimOffsetDeg != null)
                {
                    inst.AimOffsetDeg = old.AimOffsetDeg;
                    SpeakerAim.ApplyOffset(inst);
                }
                if (inst.AimOffsetDeg != null) result.AimsKept++;
            }

            foreach (int id in wantedIds ?? Enumerable.Empty<int>())
                if (!seen.Contains(id) && !result.MissingIds.Contains(id))
                    result.MissingIds.Add(id);

            var mappings = (savedMappings ?? Enumerable.Empty<SpeakerProfileMapping>()).ToList();
            result.Groups = GroupByType(instances, typeKey =>
                currentGroups.FirstOrDefault(g => g.TypeKey == typeKey)?.Mapping
                ?? mappings.FirstOrDefault(m => m.TypeKey == typeKey)
                ?? new SpeakerProfileMapping { TypeKey = typeKey });
            return result;
        }

        /// <summary>Groups speakers by "Family : Type", ordered by type key.</summary>
        public static List<SpeakerTypeGroup> GroupByType(IEnumerable<SpeakerInstance> speakers,
            Func<string, SpeakerProfileMapping> mappingFor)
        {
            return speakers
                .GroupBy(s => s.TypeKey ?? "")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    string[] parts = g.Key.Split(new[] { " : " }, StringSplitOptions.None);
                    return new SpeakerTypeGroup
                    {
                        TypeKey = g.Key,
                        FamilyName = parts.Length > 0 ? parts[0] : g.Key,
                        TypeName = parts.Length > 1 ? parts[1] : "",
                        Instances = g.ToList(),
                        Mapping = mappingFor(g.Key) ?? new SpeakerProfileMapping { TypeKey = g.Key }
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Gives each re-read wall group the wall type and height the user set for the group of the same
        /// line style (or wall type) name. Returns how many groups kept their settings.
        /// </summary>
        public static int KeepWallSettings(IEnumerable<WallLineGroup> previous, IEnumerable<WallLineGroup> fresh)
        {
            var byName = new Dictionary<string, WallLineGroup>();
            foreach (var g in previous ?? Enumerable.Empty<WallLineGroup>())
                if (g?.LineStyleName != null && !byName.ContainsKey(g.LineStyleName))
                    byName[g.LineStyleName] = g;

            int kept = 0;
            foreach (var g in fresh ?? Enumerable.Empty<WallLineGroup>())
            {
                if (g?.LineStyleName == null || !byName.TryGetValue(g.LineStyleName, out WallLineGroup old)) continue;
                g.WallType = old.WallType ?? g.WallType;
                g.HeightM = old.HeightM;
                kept++;
            }
            return kept;
        }
    }
}
