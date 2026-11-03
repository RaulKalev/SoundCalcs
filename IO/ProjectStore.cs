using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SoundCalcs.Domain;

namespace SoundCalcs.IO
{
    /// <summary>
    /// Reads and writes one JSON file per Revit project under %AppData%\RK Tools\SoundCalcs\projects\, named after the
    /// document with a hash of its path (two models with the same name in different folders get different files).
    /// </summary>
    public static class ProjectStore
    {
        /// <summary>Folder of the project files; tests point it elsewhere.</summary>
        public static string Folder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RK Tools", "SoundCalcs", "projects");

        /// <summary>The file of the project with this key (document path, else title).</summary>
        public static string PathFor(string projectKey)
        {
            string key = (projectKey ?? "").Trim();
            // The document name without folder and extension (either separator: keys are Windows paths)
            string name = key.Substring(Math.Max(key.LastIndexOf('\\'), key.LastIndexOf('/')) + 1);
            int dot = name.LastIndexOf('.');
            string stem = name.Length == 0 ? "untitled" : dot > 0 ? name.Substring(0, dot) : name;
            var safe = new StringBuilder();
            foreach (char ch in stem)
                safe.Append(Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch);
            if (safe.Length > 60) safe.Length = 60;
            return Path.Combine(Folder, safe + "_" + Hash(key) + ".json");
        }

        /// <summary>Case-insensitive hash of a project key (Windows paths compare without case).</summary>
        static string Hash(string key)
        {
            using (SHA1 sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes((key ?? "").Trim().ToLowerInvariant())).Take(5).Select(b => b.ToString("x2")));
        }

        /// <summary>The existing file of a project: its own name, else any file with its hash (renamed in case).</summary>
        static string ExistingPath(string projectKey)
        {
            string path = PathFor(projectKey);
            if (File.Exists(path) || !Directory.Exists(Folder)) return path;
            return Directory.GetFiles(Folder, "*_" + Hash(projectKey) + ".json").FirstOrDefault() ?? path;
        }

        /// <summary>The stored project, or null when there is none (or it cannot be read; it is then kept as .bad.json).</summary>
        public static SoundCalcsProject Load(string projectKey)
        {
            string path = ExistingPath(projectKey);
            if (!File.Exists(path)) return null;
            try
            {
                SoundCalcsProject p = Deserialize(File.ReadAllText(path));
                if (p == null) return null;
                p.Normalise();
                return p;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Project file could not be read ({ex.Message}); kept as {Path.GetFileName(path)}.bad.json");
                try { File.Copy(path, path + ".bad.json", true); } catch { }
                return null;
            }
        }

        public static void Save(SoundCalcsProject project)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string path = ExistingPath(project.ProjectKey);
                string temp = path + ".tmp";
                File.WriteAllText(temp, Serialize(project));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Project could not be saved: {ex.Message}");
            }
        }

        public static string Serialize(SoundCalcsProject project) => JsonConvert.SerializeObject(project, SettingsStore.JsonSettings);

        public static SoundCalcsProject Deserialize(string json) => JsonConvert.DeserializeObject<SoundCalcsProject>(json, SettingsStore.JsonSettings);

        /// <summary>
        /// The project of this document: the stored one, else a new one. A new project takes over the layout kept in
        /// settings.json by earlier versions (boundary, walls, speakers, Room page values) as its first area when that
        /// layout was made in this document, or was not tagged with a document (then only the first project gets it).
        /// Returns whether <paramref name="legacy"/> was used (so the caller can mark it and save settings).
        /// </summary>
        public static SoundCalcsProject LoadOrCreate(string projectKey, string projectName, PluginSettings legacy, out bool usedLegacy)
        {
            usedLegacy = false;
            SoundCalcsProject project = Load(projectKey);
            if (project != null) return project;
            project = Create(projectKey, projectName, legacy, out usedLegacy);
            return project;
        }

        /// <summary>A new project (not saved); see <see cref="LoadOrCreate"/> for when the legacy layout is used.</summary>
        public static SoundCalcsProject Create(string projectKey, string projectName, PluginSettings legacy, out bool usedLegacy)
        {
            var project = new SoundCalcsProject { ProjectKey = projectKey ?? "", ProjectName = projectName ?? "" };
            project.ProjectInfo.ProjectName = projectName ?? "";
            var area = new AnalysisArea { Name = "Area 1" };
            usedLegacy = false;
            if (legacy != null)
            {
                // The Room page values always carry over as the starting point
                area.Settings = legacy.AnalysisSettings?.Clone() ?? new AnalysisSettings();
                area.LinkSelection = legacy.LinkSelection != null ? (LinkSelection)legacy.LinkSelection.Clone() : new LinkSelection();
                if (string.IsNullOrEmpty(area.LinkSelection.IfcFileOverride) && !string.IsNullOrEmpty(legacy.IfcFileOverride))
                    area.LinkSelection.IfcFileOverride = legacy.IfcFileOverride;

                bool mine = Same(legacy.LayoutProjectKey, projectKey) || Same(legacy.SpeakersProjectKey, projectKey);
                bool untagged = string.IsNullOrEmpty(legacy.LayoutProjectKey) && string.IsNullOrEmpty(legacy.SpeakersProjectKey);
                if (legacy.HasLegacyLayout && !legacy.LegacyLayoutMigrated && (mine || untagged))
                {
                    area.WallGroups = (legacy.WallGroups ?? new List<WallLineGroup>()).Select(g => g.Clone()).ToList();
                    area.BoundaryRooms = (legacy.BoundaryRooms ?? new List<RoomPolygon>()).Select(r => r.Clone()).ToList();
                    area.BoundaryLineIds = new List<int>(legacy.BoundaryLineIds ?? new List<int>());
                    area.WallsDetected = legacy.WallsDetected;
                    area.SpeakerGroups = (legacy.SavedSpeakerGroups ?? new List<SpeakerTypeGroup>()).Select(g => g.Clone()).ToList();
                    area.LayoutProjectKey = legacy.LayoutProjectKey;
                    area.LayoutProjectName = legacy.LayoutProjectName;
                    area.SpeakersProjectKey = legacy.SpeakersProjectKey;
                    area.SpeakersProjectName = legacy.SpeakersProjectName;
                    usedLegacy = true;
                }
            }
            project.Areas.Add(area);
            project.ActiveAreaId = area.Id;
            project.Normalise();
            return project;
        }

        static bool Same(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
