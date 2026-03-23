using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        /// <summary>Wall line groups selected by the user (detail lines or auto-detected).</summary>
        public List<WallLineGroup> WallGroups { get; set; } = new List<WallLineGroup>();

        /// <summary>Boundary polygon produced by SelectBoundary.</summary>
        public List<RoomPolygon> BoundaryRooms { get; set; } = new List<RoomPolygon>();

        /// <summary>Speaker type groups (instances + mappings) last picked by the user.</summary>
        public List<SpeakerTypeGroup> SavedSpeakerGroups { get; set; } = new List<SpeakerTypeGroup>();
    }

    /// <summary>
    /// Handles Vec2's readonly fields so Newtonsoft.Json can round-trip them.
    /// </summary>
    class Vec2Converter : JsonConverter<Vec2>
    {
        public override Vec2 ReadJson(JsonReader reader, Type objectType, Vec2 existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            var jo = JObject.Load(reader);
            return new Vec2(jo["X"]?.Value<double>() ?? 0, jo["Y"]?.Value<double>() ?? 0);
        }

        public override void WriteJson(JsonWriter writer, Vec2 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("X"); writer.WriteValue(value.X);
            writer.WritePropertyName("Y"); writer.WriteValue(value.Y);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Handles Vec3's readonly fields so Newtonsoft.Json can round-trip them.
    /// </summary>
    class Vec3Converter : JsonConverter<Vec3>
    {
        public override Vec3 ReadJson(JsonReader reader, Type objectType, Vec3 existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            var jo = JObject.Load(reader);
            return new Vec3(
                jo["X"]?.Value<double>() ?? 0,
                jo["Y"]?.Value<double>() ?? 0,
                jo["Z"]?.Value<double>() ?? 0);
        }

        public override void WriteJson(JsonWriter writer, Vec3 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("X"); writer.WriteValue(value.X);
            writer.WritePropertyName("Y"); writer.WriteValue(value.Y);
            writer.WritePropertyName("Z"); writer.WriteValue(value.Z);
            writer.WriteEndObject();
        }
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
            DefaultValueHandling = DefaultValueHandling.Include,
            Converters = { new Vec2Converter(), new Vec3Converter() }
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
