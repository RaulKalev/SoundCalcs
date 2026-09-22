using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Speaker frequency-response presets (dB per octave band, 125 Hz – 8 kHz, relative).
    /// Generic approximations of common speaker classes; enter manufacturer data as
    /// "Custom" when available. Only the shape matters (the broadband level is the SPL column).
    /// </summary>
    public static class SpeakerResponsePresets
    {
        public const string CustomName = "Custom";

        public static readonly List<(string Name, double[] ResponseDb)> All = new List<(string, double[])>
        {
            ("Flat",                           new double[] {   0,  0,  0, 0, 0,  0,  0 }),
            ("Ceiling speaker 8\" (typical)",  new double[] {  -6, -1,  0, 0, 0, -1, -4 }),
            ("Ceiling speaker 4–6\" (typical)", new double[] { -12, -5, -1, 0, 0, -1, -4 }),
            ("Column / line array (typical)",  new double[] { -10, -4,  0, 0, 0, -1, -3 }),
            ("Horn / paging (typical)",        new double[] { -20,-12, -3, 0, 0, -3, -12 }),
        };

        public static IEnumerable<string> Names => All.Select(p => p.Name).Concat(new[] { CustomName });

        /// <summary>Preset values by name, or null for Custom / unknown.</summary>
        public static double[] Find(string name) =>
            All.Where(p => p.Name == name).Select(p => (double[])p.ResponseDb.Clone()).FirstOrDefault();

        /// <summary>Name of the preset matching <paramref name="responseDb"/> (null = Flat), else Custom.</summary>
        public static string NameFor(double[] responseDb)
        {
            double[] r = responseDb ?? new double[OctaveBands.Count];
            foreach (var p in All)
                if (p.ResponseDb.Length == r.Length && p.ResponseDb.Zip(r, (a, b) => Math.Abs(a - b) < 1e-9).All(x => x))
                    return p.Name;
            return CustomName;
        }

        /// <summary>"0 -1 0 0 0 -2 -6" (invariant culture).</summary>
        public static string Format(double[] responseDb) =>
            string.Join(" ", (responseDb ?? new double[OctaveBands.Count]).Select(v => v.ToString("0.#", CultureInfo.InvariantCulture)));

        /// <summary>Parse 7 response values (dB, −60…+20) separated by spaces or semicolons.</summary>
        public static bool TryParse(string text, out double[] responseDb) =>
            TryParseBands(text, -60, 20, out responseDb);

        /// <summary>Parse 7 per-band numbers within [min, max], separated by spaces or semicolons.</summary>
        public static bool TryParseBands(string text, double min, double max, out double[] values)
        {
            values = null;
            if (text == null) return false;
            string[] parts = text.Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != OctaveBands.Count) return false;
            var v = new double[OctaveBands.Count];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim(',');
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) ||
                    v[i] < min || v[i] > max)
                    return false;
            }
            values = v;
            return true;
        }
    }
}
