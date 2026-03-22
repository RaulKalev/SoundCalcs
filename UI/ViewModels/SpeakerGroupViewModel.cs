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
                }
            }
        }

        public bool IsGllFile => ProfileSource == ProfileSourceType.GllFile;
        public bool IsSimpleConical => ProfileSource == ProfileSourceType.SimpleConical;

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

        public double ConeHalfAngleDeg
        {
            get => _group.Mapping.ConeHalfAngleDeg;
            set
            {
                _group.Mapping.ConeHalfAngleDeg = value;
                OnPropertyChanged(nameof(ConeHalfAngleDeg));
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
