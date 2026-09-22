using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Rotationally symmetric loudspeaker directivity: attenuation in dB relative to on-axis
    /// for each octave band (125 Hz – 8 kHz) against off-axis angle (0° = on axis, 180° = behind).
    /// CSV layout (header row required, comma or semicolon separated, '#' comments allowed):
    /// <code>
    /// angle,125,250,500,1k,2k,4k,8k
    /// 0,0,0,0,0,0,0,0
    /// 30,0,-0.5,-1,-1.5,-2,-3,-4
    /// ...
    /// 180,-3,-6,-10,-15,-20,-25,-25
    /// </code>
    /// Angles must start at 0 and increase; values between rows are interpolated in dB.
    /// Beyond the last row the last values apply.
    /// </summary>
    public class PolarTable
    {
        public double[] AnglesDeg { get; }
        /// <summary>[band][angle index] attenuation in dB (≤ 0 typically).</summary>
        public double[][] AttenuationDb { get; }

        public PolarTable(double[] anglesDeg, double[][] attenuationDb)
        {
            AnglesDeg = anglesDeg;
            AttenuationDb = attenuationDb;
        }

        /// <summary>Attenuation in dB for band k at the given off-axis angle.</summary>
        public double AttenuationAt(int band, double angleDeg)
        {
            double[] a = AnglesDeg, v = AttenuationDb[band];
            if (angleDeg <= a[0]) return v[0];
            if (angleDeg >= a[a.Length - 1]) return v[v.Length - 1];
            int i = 1;
            while (a[i] < angleDeg) i++;
            double f = (angleDeg - a[i - 1]) / (a[i] - a[i - 1]);
            return v[i - 1] + (v[i] - v[i - 1]) * f;
        }

        public static PolarTable Load(string path) => Parse(File.ReadAllText(path));

        public static PolarTable Parse(string text)
        {
            var lines = text.Split('\n').Select(l => l.Trim().TrimEnd('\r'))
                .Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            if (lines.Count < 3)
                throw new FormatException("Polar table needs a header row and at least two angle rows.");

            char sep = lines[0].Contains(';') ? ';' : ',';
            string[] header = lines[0].Split(sep).Select(h => h.Trim().ToLowerInvariant()).ToArray();
            string[] bandNames = { "125", "250", "500", "1k", "2k", "4k", "8k" };
            string[] alt = { "125", "250", "500", "1000", "2000", "4000", "8000" };
            int angleCol = Array.FindIndex(header, h => h.StartsWith("angle") || h == "deg" || h == "degrees");
            if (angleCol < 0) throw new FormatException("Polar table header needs an 'angle' column.");
            int[] bandCols = new int[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                bandCols[k] = Array.FindIndex(header, h => h == bandNames[k] || h == alt[k] || h == bandNames[k] + "hz" || h == alt[k] + "hz");
                if (bandCols[k] < 0) throw new FormatException($"Polar table header is missing the {bandNames[k]} column.");
            }

            var angles = new List<double>();
            var values = Enumerable.Range(0, OctaveBands.Count).Select(_ => new List<double>()).ToArray();
            for (int i = 1; i < lines.Count; i++)
            {
                string[] c = lines[i].Split(sep);
                double Num(int col) => double.Parse(c[col].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                double angle = Num(angleCol);
                if (angles.Count > 0 && angle <= angles[angles.Count - 1])
                    throw new FormatException($"Polar table angles must increase (row {i + 1}: {angle}°).");
                if (angle < 0 || angle > 180)
                    throw new FormatException($"Polar table angle {angle}° is outside 0–180°.");
                angles.Add(angle);
                for (int k = 0; k < OctaveBands.Count; k++)
                    values[k].Add(Num(bandCols[k]));
            }
            if (Math.Abs(angles[0]) > 1e-9)
                throw new FormatException("Polar table must start at 0° (on axis).");

            return new PolarTable(angles.ToArray(), values.Select(v => v.ToArray()).ToArray());
        }
    }
}
