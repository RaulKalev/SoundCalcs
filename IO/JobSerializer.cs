using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        /// <summary>Whether both files of the job are on disk.</summary>
        public static bool Exists(string jobId) =>
            !string.IsNullOrEmpty(jobId) && File.Exists(GetInputPath(jobId)) && File.Exists(GetOutputPath(jobId));

        /// <summary>The job's input and output, or nulls when either file is missing or unreadable.</summary>
        public static (AcousticJobInput Input, AcousticJobOutput Output) TryLoad(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) return (null, null);
            try
            {
                if (!File.Exists(GetInputPath(jobId)) || !File.Exists(GetOutputPath(jobId))) return (null, null);
                return (LoadInput(jobId), LoadOutput(jobId));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Job {jobId} could not be read: {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>
        /// The most recent output written for <paramref name="areaId"/>, or null. Only the start of each file is read
        /// to find its area, newest first.
        /// </summary>
        public static AcousticJobOutput LoadLatestOutputForArea(string areaId)
        {
            if (string.IsNullOrEmpty(areaId) || !Directory.Exists(JobsDir)) return null;
            var files = new DirectoryInfo(JobsDir).GetFiles("*_output.json").OrderByDescending(f => f.LastWriteTimeUtc);
            string needle = "\"AreaId\": \"" + areaId + "\"";
            foreach (FileInfo f in files)
            {
                try
                {
                    char[] head = new char[4096];
                    int n;
                    using (var r = new StreamReader(f.FullName)) n = r.ReadBlock(head, 0, head.Length);
                    if (new string(head, 0, n).IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                    var output = JsonConvert.DeserializeObject<AcousticJobOutput>(File.ReadAllText(f.FullName), JsonSettings);
                    if (output != null && !output.WasCanceled && output.Error == null && output.Results.Count > 0) return output;
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"Job output {f.Name} could not be read: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Generate a new unique job ID based on timestamp.
        /// </summary>
        public static string NewJobId() =>
            $"job_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
    }
}
