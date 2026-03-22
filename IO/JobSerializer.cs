using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using SoundCalcs.Domain;

namespace SoundCalcs.IO
{
    /// <summary>
    /// Serializes and deserializes acoustic job inputs/outputs to JSON files.
    /// Files are stored under %AppData%\RK Tools\SoundCalcs\jobs\
    /// </summary>
    public static class JobSerializer
    {
        private static readonly string JobsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RK Tools", "SoundCalcs", "jobs");

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        private static void EnsureDirectory()
        {
            if (!Directory.Exists(JobsDir))
                Directory.CreateDirectory(JobsDir);
        }

        public static string GetInputPath(string jobId) =>
            Path.Combine(JobsDir, $"{jobId}_input.json");

        public static string GetOutputPath(string jobId) =>
            Path.Combine(JobsDir, $"{jobId}_output.json");

        public static void SaveInput(AcousticJobInput input)
        {
            EnsureDirectory();
            string path = GetInputPath(input.JobId);
            string json = JsonConvert.SerializeObject(input, JsonSettings);
            File.WriteAllText(path, json);
            Debug.WriteLine($"[SoundCalcs] Job input saved: {path}");
        }

        public static void SaveOutput(AcousticJobOutput output)
        {
            EnsureDirectory();
            string path = GetOutputPath(output.JobId);
            string json = JsonConvert.SerializeObject(output, JsonSettings);
            File.WriteAllText(path, json);
            Debug.WriteLine($"[SoundCalcs] Job output saved: {path}");
        }

        public static AcousticJobInput LoadInput(string jobId)
        {
            string path = GetInputPath(jobId);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Job input not found: {path}");

            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<AcousticJobInput>(json, JsonSettings);
        }

        public static AcousticJobOutput LoadOutput(string jobId)
        {
            string path = GetOutputPath(jobId);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Job output not found: {path}");

            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<AcousticJobOutput>(json, JsonSettings);
        }

        /// <summary>
        /// Load the most recent output file in the jobs directory, if any.
        /// </summary>
        public static AcousticJobOutput LoadLatestOutput()
        {
            EnsureDirectory();
            string[] files = Directory.GetFiles(JobsDir, "*_output.json");
            if (files.Length == 0) return null;

            string latest = files[0];
            DateTime latestTime = File.GetLastWriteTimeUtc(latest);

            for (int i = 1; i < files.Length; i++)
            {
                DateTime t = File.GetLastWriteTimeUtc(files[i]);
                if (t > latestTime)
                {
                    latest = files[i];
                    latestTime = t;
                }
            }

            string json = File.ReadAllText(latest);
            return JsonConvert.DeserializeObject<AcousticJobOutput>(json, JsonSettings);
        }

        /// <summary>
        /// Generate a new unique job ID based on timestamp.
        /// </summary>
        public static string NewJobId() =>
            $"job_{DateTime.Now:yyyyMMdd_HHmmss}";
    }
}
