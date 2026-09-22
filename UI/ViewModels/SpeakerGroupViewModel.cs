using System.Collections.Generic;
using System.ComponentModel;
using SoundCalcs.Domain;

namespace SoundCalcs.UI.ViewModels
{
    /// <summary>
    /// ViewModel for a single speaker type group row in the Speakers tab.
    /// </summary>
    public class SpeakerGroupViewModel : INotifyPropertyChanged
    {
        private readonly SpeakerTypeGroup _group;

        public SpeakerGroupViewModel(SpeakerTypeGroup group)
        {
            _group = group;
            Response = new BandFields(
                () => _group.Mapping.SpectrumShapeByBand ?? new double[OctaveBands.Count],
                v => { _group.Mapping.SpectrumShapeByBand = v; OnPropertyChanged(nameof(ResponsePreset)); OnPropertyChanged(nameof(ResponseText)); },
                -40, 20, "dB",
                () => new double[OctaveBands.Count]);
            Coverage = new BandFields(
                () => _group.Mapping.CoverageAngleByBandDeg,
                v => { _group.Mapping.CoverageAngleByBandDeg = v; OnPropertyChanged(nameof(CoverageText)); OnPropertyChanged(nameof(CoverageSummary)); },
                5, 360, "°",
                () =>
                {
                    // First datasheet value entered: start every band from the cone's full angle.
                    double full = System.Math.Min(360, 2 * _group.Mapping.ConeHalfAngleDeg);
                    var v = new double[OctaveBands.Count];
                    for (int i = 0; i < v.Length; i++) v[i] = full;
                    return v;
                });
        }

        /// <summary>Per-band response in dB, one field per band (Speakers page detail card).</summary>
        public BandFields Response { get; }

        /// <summary>Per-band datasheet coverage angle, one field per band; empty = cone model.</summary>
        public BandFields Coverage { get; }

        /// <summary>Header of the detail card.</summary>
        public string DisplayName => $"{FamilyName} : {TypeName}";

        /// <summary>Short description of the coverage model for the table.</summary>
        public string CoverageSummary => _group.Mapping.CoverageAngleByBandDeg != null
            ? "Datasheet"
            : $"Cone {_group.Mapping.ConeHalfAngleDeg:0.#}°";

        /// <summary>Placeholder of empty coverage fields: the full angle the cone model uses.</summary>
        public string ConeFullAngleText => System.Math.Min(360, 2 * _group.Mapping.ConeHalfAngleDeg).ToString("0.#");

        /// <summary>Back to the cone model (clears the datasheet angles).</summary>
        public void ClearCoverage()
        {
            _group.Mapping.CoverageAngleByBandDeg = null;
            Coverage.Refresh();
            OnPropertyChanged(nameof(CoverageText));
            OnPropertyChanged(nameof(CoverageSummary));
        }

        public string TypeKey => _group.TypeKey;
        public string FamilyName => _group.FamilyName;
        public string TypeName => _group.TypeName;
        public int Count => _group.Count;
        public int SampleElementId => _group.SampleElementId;

        public string LevelName
        {
            get
            {
                if (_group.Instances.Count == 0) return "";
                return _group.Instances[0].LevelName;
            }
        }

        // --- Profile mapping ---

        public ProfileSourceType ProfileSource
        {
            get => _group.Mapping.ProfileSource;
            set
            {
                if (_group.Mapping.ProfileSource != value)
                {
                    _group.Mapping.ProfileSource = value;
                    OnPropertyChanged(nameof(ProfileSource));
                    OnPropertyChanged(nameof(IsGllFile));
                    OnPropertyChanged(nameof(IsSimpleConical));
                    OnPropertyChanged(nameof(IsWallMounted));
                }
            }
        }

        public bool IsGllFile => ProfileSource == ProfileSourceType.GllFile;
        public bool IsSimpleConical => ProfileSource == ProfileSourceType.SimpleConical;
        public bool IsWallMounted => ProfileSource == ProfileSourceType.WallMounted;

        public string GllFilePath
        {
            get => _group.Mapping.GllFilePath;
            set
            {
                _group.Mapping.GllFilePath = value;
                OnPropertyChanged(nameof(GllFilePath));
            }
        }

        public double OnAxisSplDb
        {
            get => _group.Mapping.OnAxisSplDb;
            set
            {
                _group.Mapping.OnAxisSplDb = value;
                OnPropertyChanged(nameof(OnAxisSplDb));
            }
        }

        // --- Frequency response (spectrum shape) ---
        public IEnumerable<string> AvailableResponsePresets => SpeakerResponsePresets.Names;

        /// <summary>Response preset name; choosing one fills <see cref="ResponseText"/>.</summary>
        public string ResponsePreset
        {
            get => SpeakerResponsePresets.NameFor(_group.Mapping.SpectrumShapeByBand);
            set
            {
                double[] values = SpeakerResponsePresets.Find(value);
                if (values == null) return; // "Custom": keep current values, edit the text
                _group.Mapping.SpectrumShapeByBand = values;
                OnPropertyChanged(nameof(ResponsePreset));
                OnPropertyChanged(nameof(ResponseText));
                Response.Refresh();
            }
        }

        /// <summary>Per-band response in dB, "125 250 500 1k 2k 4k 8k" separated by spaces.</summary>
        public string ResponseText
        {
            get => SpeakerResponsePresets.Format(_group.Mapping.SpectrumShapeByBand);
            set
            {
                if (!SpeakerResponsePresets.TryParse(value, out double[] values)) return; // ignore invalid input
                _group.Mapping.SpectrumShapeByBand = values;
                OnPropertyChanged(nameof(ResponseText));
                OnPropertyChanged(nameof(ResponsePreset));
            }
        }

        /// <summary>
        /// Datasheet −6 dB coverage angles (full, degrees) per band, "125 250 500 1k 2k 4k 8k".
        /// Empty = cone model from the Cone Angle column.
        /// </summary>
        public string CoverageText
        {
            get => _group.Mapping.CoverageAngleByBandDeg == null ? ""
                : SpeakerResponsePresets.Format(_group.Mapping.CoverageAngleByBandDeg);
            set
            {
                if (string.IsNullOrWhiteSpace(value)) _group.Mapping.CoverageAngleByBandDeg = null;
                else if (SpeakerResponsePresets.TryParseBands(value, 5, 360, out double[] v)) _group.Mapping.CoverageAngleByBandDeg = v;
                else return; // ignore invalid input
                OnPropertyChanged(nameof(CoverageText));
            }
        }

        /// <summary>Polar table CSV replacing the cone model (conical / wall-mounted profiles).</summary>
        public string DirectivityFilePath
        {
            get => _group.Mapping.DirectivityFilePath ?? "";
            set
            {
                _group.Mapping.DirectivityFilePath = value?.Trim().Trim('"') ?? "";
                OnPropertyChanged(nameof(DirectivityFilePath));
            }
        }

        public double ConeHalfAngleDeg
        {
            get => _group.Mapping.ConeHalfAngleDeg;
            set
            {
                _group.Mapping.ConeHalfAngleDeg = value;
                OnPropertyChanged(nameof(ConeHalfAngleDeg));
                OnPropertyChanged(nameof(CoverageSummary));
                OnPropertyChanged(nameof(ConeFullAngleText));
            }
        }

        public double OffAxisAttenuationDb
        {
            get => _group.Mapping.OffAxisAttenuationDb;
            set
            {
                _group.Mapping.OffAxisAttenuationDb = value;
                OnPropertyChanged(nameof(OffAxisAttenuationDb));
            }
        }

        /// <summary>
        /// Export mapping for persistence.
        /// </summary>
        public SpeakerProfileMapping GetMapping()
        {
            _group.Mapping.TypeKey = _group.TypeKey;
            return _group.Mapping;
        }

        /// <summary>
        /// Apply a saved mapping.
        /// </summary>
        public void ApplyMapping(SpeakerProfileMapping mapping)
        {
            if (mapping == null) return;
            _group.Mapping = mapping;
            OnPropertyChanged(nameof(ProfileSource));
            OnPropertyChanged(nameof(IsGllFile));
            OnPropertyChanged(nameof(IsSimpleConical));
            OnPropertyChanged(nameof(GllFilePath));
            OnPropertyChanged(nameof(OnAxisSplDb));
            OnPropertyChanged(nameof(ConeHalfAngleDeg));
            OnPropertyChanged(nameof(OffAxisAttenuationDb));
            OnPropertyChanged(nameof(IsWallMounted));
            OnPropertyChanged(nameof(ResponsePreset));
            OnPropertyChanged(nameof(ResponseText));
            OnPropertyChanged(nameof(CoverageText));
            OnPropertyChanged(nameof(CoverageSummary));
            OnPropertyChanged(nameof(ConeFullAngleText));
            OnPropertyChanged(nameof(DirectivityFilePath));
            Response.Refresh();
            Coverage.Refresh();
        }

        /// <summary>
        /// Get the underlying domain group.
        /// </summary>
        public SpeakerTypeGroup GetGroup() => _group;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
