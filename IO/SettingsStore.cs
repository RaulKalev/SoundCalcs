using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using SoundCalcs.Domain;

namespace SoundCalcs.IO
{
    /// <summary>
    /// Root settings object persisted to JSON.
    /// </summary>
    public class PluginSettings
    {
        public LinkSelection LinkSelection { get; set; } = new LinkSelection();
        public AnalysisSettings AnalysisSettings { get; set; } = new AnalysisSettings();
        public List<SpeakerProfileMapping> SpeakerMappings { get; set; } = new List<SpeakerProfileMapping>();
    }

    /// <summary>
    /// Reads and writes plugin settings to a JSON file under %AppData%.
    /// Path: %AppData%\RK Tools\SoundCalcs\settings.json
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RK Tools", "SoundCalcs");

        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Include
        };

        public static PluginSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new PluginSettings();

                string json = File.ReadAllText(SettingsPath);
                PluginSettings settings = JsonConvert.DeserializeObject<PluginSettings>(json, JsonSettings);
                return settings ?? new PluginSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoundCalcs] Failed to load settings: {ex.Message}");
                return new PluginSettings();
            }
        }

        public static void Save(PluginSettings settings)
        {
            try
            {
                if (!Directory.Exists(SettingsDir))
                    Directory.CreateDirectory(SettingsDir);

                string json = JsonConvert.SerializeObject(settings, JsonSettings);
                File.WriteAllText(SettingsPath, json);
                Debug.WriteLine($"[SoundCalcs] Settings saved to {SettingsPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoundCalcs] Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Return the directory used for settings, for display in UI.
        /// </summary>
        public static string GetSettingsDirectory() => SettingsDir;
    }
}
