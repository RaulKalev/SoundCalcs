using System;
using System.ComponentModel;
using System.Globalization;
using SoundCalcs.Domain;

namespace SoundCalcs.UI.ViewModels
{
    /// <summary>
    /// Seven octave-band values (125 Hz … 8 kHz) edited as one field per band, bound as <c>Response[0]</c> …
    /// <c>Response[6]</c>. An out-of-range or non-numeric entry throws, so the field shows the error
    /// (bindings use <c>ValidatesOnExceptions=True</c>) and the stored values stay valid.
    /// </summary>
    public class BandFields : INotifyPropertyChanged
    {
        private readonly Func<double[]> _get;
        private readonly Action<double[]> _set;
        private readonly Func<double[]> _whenEmpty;
        private readonly double _min, _max;
        private readonly string _unit;

        /// <param name="get">Current values, or null when the quantity is not set (e.g. no coverage data).</param>
        /// <param name="set">Stores new values.</param>
        /// <param name="whenEmpty">Starting values when the first band is entered while the quantity is not set.</param>
        public BandFields(Func<double[]> get, Action<double[]> set, double min, double max, string unit, Func<double[]> whenEmpty)
        {
            _get = get;
            _set = set;
            _whenEmpty = whenEmpty;
            _min = min;
            _max = max;
            _unit = unit;
        }

        public string this[int band]
        {
            get
            {
                double[] v = _get();
                return v == null || band < 0 || band >= v.Length ? "" : v[band].ToString("0.#", CultureInfo.InvariantCulture);
            }
            set
            {
                string text = (value ?? "").Trim().Replace(',', '.');
                double d;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new ArgumentException("Enter a number.");
                if (d < _min || d > _max)
                    throw new ArgumentException($"Enter a value from {Fmt(_min)} to {Fmt(_max)} {_unit}.");

                double[] current = _get() ?? _whenEmpty();
                var next = (double[])current.Clone();
                next[band] = d;
                _set(next);
                Refresh();
            }
        }

        public bool HasValues => _get() != null;

        /// <summary>Re-reads every band (after a preset or the underlying mapping changed).</summary>
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasValues)));
        }

        static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Band labels for column headers: 125, 250, 500, 1k, 2k, 4k, 8k.</summary>
        public static string[] Labels => OctaveBands.Labels;
    }
}
