using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundCalcs.Domain
{
    public enum PreflightSeverity { Ok, Warning, Blocker }

    /// <summary>One line of the Run page checklist.</summary>
    public class PreflightItem
    {
        public PreflightSeverity Severity { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }

        /// <summary>Page that fixes it ("Model", "Speakers", "Room"), or null when it is fixed where it is shown.</summary>
        public string FixPage { get; set; }

        public bool IsOk => Severity == PreflightSeverity.Ok;
        public bool IsWarning => Severity == PreflightSeverity.Warning;
        public bool IsBlocker => Severity == PreflightSeverity.Blocker;
    }

    /// <summary>What the checklist looks at. Project keys identify a Revit document (its path, else its title).</summary>
    public class PreflightInput
    {
        public List<RoomPolygon> Boundary { get; set; } = new List<RoomPolygon>();
        public int WallLineStyleCount { get; set; }

        /// <summary>All picked speakers.</summary>
        public List<SpeakerInstance> Speakers { get; set; } = new List<SpeakerInstance>();

        /// <summary>The speakers the run will use (after the A/B line filter).</summary>
        public List<SpeakerInstance> IncludedSpeakers { get; set; } = new List<SpeakerInstance>();

        /// <summary>"A line" / "B line" when a line filter is on, else null.</summary>
        public string LineFilterName { get; set; }

        public string ProjectKey { get; set; }
        public string LayoutProjectKey { get; set; }
        public string LayoutProjectName { get; set; }
        public string SpeakersProjectKey { get; set; }
        public string SpeakersProjectName { get; set; }

        /// <summary>Settings fields that currently hold a value out of range.</summary>
        public int InvalidFieldCount { get; set; }
    }

    /// <summary>
    /// Checks a run's inputs before it starts, so a wrong setup is reported instead of computed:
    /// missing boundary or speakers (blockers), and speakers that do not belong to the boundary –
    /// outside it, on another level, or picked in another project (warnings).
    /// </summary>
    public static class RunPreflight
    {
        /// <summary>A speaker's level may differ from the boundary floor by this much before it is reported.</summary>
        public const double LevelToleranceM = 0.5;

        /// <summary>Speakers this close to the boundary outline count as inside (wall-mounted speakers sit on it).</summary>
        public const double EdgeToleranceM = 0.5;

        public static List<PreflightItem> Check(PreflightInput input)
        {
            var items = new List<PreflightItem>();
            bool hasBoundary = input.Boundary != null && input.Boundary.Any(r => r.Vertices.Count >= 3);

            // ── Boundary ──
            if (!hasBoundary)
            {
                items.Add(Blocker("No room boundary", "Select the detail lines that outline the room.", "Model"));
            }
            else
            {
                double area = input.Boundary.Sum(r => r.Area);
                if (input.WallLineStyleCount == 0)
                    items.Add(Warning("No walls",
                        $"The boundary ({area:F0} m²) has no walls, so it is treated as one open area.", "Model"));
                else
                    items.Add(Ok("Room boundary",
                        $"{area:F0} m², {input.WallLineStyleCount} wall line style{Plural(input.WallLineStyleCount)}"));

                if (Differs(input.LayoutProjectKey, input.ProjectKey))
                    items.Add(Warning("Boundary is from another project",
                        $"It was selected in {Name(input.LayoutProjectName)}. Select it again in this project.", "Model"));
            }

            // ── Speakers ──
            int total = input.Speakers?.Count ?? 0;
            var included = input.IncludedSpeakers ?? new List<SpeakerInstance>();
            if (total == 0)
            {
                items.Add(Blocker("No speakers", "Pick the speakers in the model.", "Speakers"));
            }
            else if (included.Count == 0)
            {
                items.Add(Blocker($"No speakers on the {input.LineFilterName ?? "selected line"}",
                    "Choose All below, or check the A/B line parameter on the Room page.", null));
            }
            else
            {
                string detail = input.LineFilterName != null
                    ? $"{included.Count} of {total} ({input.LineFilterName})"
                    : $"{included.Count} speaker{Plural(included.Count)}";
                items.Add(Ok("Speakers", detail));

                if (Differs(input.SpeakersProjectKey, input.ProjectKey))
                    items.Add(Warning("Speakers are from another project",
                        $"They were picked in {Name(input.SpeakersProjectName)}. Pick them again in this project.", "Speakers"));
            }

            // ── Do the speakers belong to the boundary? ──
            if (hasBoundary && included.Count > 0)
            {
                int outside = included.Count(s => !IsInsideOrNear(input.Boundary, s.Position));
                if (outside == included.Count)
                    items.Add(Warning("No speaker is inside the boundary",
                        "The boundary and the speakers do not overlap, so they may come from different areas or projects. Select the boundary again.",
                        "Model"));
                else if (outside > 0)
                    items.Add(Warning($"{outside} of {included.Count} speakers are outside the boundary",
                        "They still contribute, but levels are only calculated inside the boundary.", null));

                double floor = input.Boundary[0].FloorElevationM;
                var otherLevel = included
                    .Where(s => !string.IsNullOrEmpty(s.LevelName) && Math.Abs(s.LevelElevationM - floor) > LevelToleranceM)
                    .ToList();
                if (otherLevel.Count > 0)
                {
                    var top = otherLevel.GroupBy(s => s.LevelName).OrderByDescending(g => g.Count()).First();
                    double delta = top.First().LevelElevationM - floor;
                    items.Add(Warning(
                        $"{otherLevel.Count} speaker{Plural(otherLevel.Count)} on another level",
                        $"Level '{top.Key}' is {delta:+0.00;-0.00} m from the boundary floor. Listeners are placed above the " +
                        "boundary floor, so distances would be wrong. Select the boundary in a plan of the speakers' level.",
                        "Model"));
                }

                int below = included.Count(s => s.Position.Z < floor - 0.1);
                if (below > 0)
                    items.Add(Warning($"{below} speaker{Plural(below)} below the floor",
                        "They are lower than the boundary floor. Check the boundary's level.", "Model"));
            }

            if (input.InvalidFieldCount > 0)
                items.Add(Blocker("Some values are not valid",
                    $"{input.InvalidFieldCount} field{Plural(input.InvalidFieldCount)} on the Room page " +
                    $"need{(input.InvalidFieldCount == 1 ? "s" : "")} a value in range.", "Room"));

            return items;
        }

        public static bool CanRun(IEnumerable<PreflightItem> items) => !items.Any(i => i.IsBlocker);

        /// <summary>Why Run is disabled (the first blocker), or null when it can run.</summary>
        public static string BlockReason(IEnumerable<PreflightItem> items)
        {
            PreflightItem b = items.FirstOrDefault(i => i.IsBlocker);
            return b == null ? null : $"{b.Title}. {b.Detail}";
        }

        /// <summary>True when the point lies inside a boundary polygon or within <see cref="EdgeToleranceM"/> of its outline.</summary>
        public static bool IsInsideOrNear(IEnumerable<RoomPolygon> boundary, Vec3 position)
        {
            var p = new Vec2(position.X, position.Y);
            foreach (RoomPolygon room in boundary)
            {
                if (room.Vertices.Count < 3) continue;
                if (room.ContainsPoint(p) || DistanceToOutline(room.Vertices, p) <= EdgeToleranceM) return true;
            }
            return false;
        }

        static double DistanceToOutline(List<Vec2> vertices, Vec2 p)
        {
            double best = double.MaxValue;
            for (int i = 0; i < vertices.Count; i++)
            {
                Vec2 a = vertices[i];
                Vec2 b = vertices[(i + 1) % vertices.Count];
                double abx = b.X - a.X, aby = b.Y - a.Y;
                double len2 = abx * abx + aby * aby;
                double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
                double dx = a.X + t * abx - p.X, dy = a.Y + t * aby - p.Y;
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
            }
            return best;
        }

        // Unknown origin (settings from before projects were recorded) is not reported.
        static bool Differs(string stored, string current)
            => !string.IsNullOrEmpty(stored) && !string.IsNullOrEmpty(current)
               && !string.Equals(stored, current, StringComparison.OrdinalIgnoreCase);

        static string Name(string projectName) => string.IsNullOrEmpty(projectName) ? "another project" : $"'{projectName}'";
        static string Plural(int n) => n == 1 ? "" : "s";

        static PreflightItem Ok(string title, string detail)
            => new PreflightItem { Severity = PreflightSeverity.Ok, Title = title, Detail = detail };
        static PreflightItem Warning(string title, string detail, string fixPage)
            => new PreflightItem { Severity = PreflightSeverity.Warning, Title = title, Detail = detail, FixPage = fixPage };
        static PreflightItem Blocker(string title, string detail, string fixPage)
            => new PreflightItem { Severity = PreflightSeverity.Blocker, Title = title, Detail = detail, FixPage = fixPage };
    }
}
