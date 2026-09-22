using System;
using System.Collections.Generic;
using System.Globalization;

namespace SoundCalcs.Harness
{
    public enum CheckSeverity
    {
        /// <summary>A broken invariant: the harness exits non-zero.</summary>
        Fail,

        /// <summary>Deviation from a reference model worth a human look; does not fail the run.</summary>
        Warn
    }

    public class CheckResult
    {
        public string Name { get; set; }
        public bool Passed { get; set; }
        public CheckSeverity Severity { get; set; }
        public string Detail { get; set; }

        public string Status => Passed ? "PASS" : Severity == CheckSeverity.Fail ? "FAIL" : "WARN";
    }

    /// <summary>Collects check outcomes for one scenario.</summary>
    public class CheckContext
    {
        public List<CheckResult> Results { get; } = new List<CheckResult>();

        public bool Assert(string name, bool ok, string detail, CheckSeverity severity = CheckSeverity.Fail)
        {
            Results.Add(new CheckResult { Name = name, Passed = ok, Detail = detail, Severity = severity });
            return ok;
        }

        public bool Near(string name, double actual, double expected, double tolerance,
            string unit = "", CheckSeverity severity = CheckSeverity.Fail)
        {
            double diff = actual - expected;
            bool ok = !double.IsNaN(actual) && Math.Abs(diff) <= tolerance;
            return Assert(name, ok,
                $"actual {F(actual)}{unit}, expected {F(expected)}{unit} ±{F(tolerance)} (diff {F(diff)})",
                severity);
        }

        public bool InRange(string name, double actual, double lo, double hi,
            string unit = "", CheckSeverity severity = CheckSeverity.Fail)
        {
            bool ok = !double.IsNaN(actual) && actual >= lo && actual <= hi;
            return Assert(name, ok, $"actual {F(actual)}{unit}, expected in [{F(lo)}, {F(hi)}]", severity);
        }

        public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
