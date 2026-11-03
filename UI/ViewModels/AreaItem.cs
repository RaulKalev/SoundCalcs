using System.ComponentModel;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.Report;

namespace SoundCalcs.UI.ViewModels
{
    /// <summary>One row of the Areas list: the area's name and the state of its results.</summary>
    public class AreaItem : INotifyPropertyChanged
    {
        public AreaItem(string id) { Id = id; }

        public string Id { get; }

        public string Name { get; private set; } = "";

        public AreaResultStatus Status { get; private set; } = AreaResultStatus.NotCalculated;

        /// <summary>"Calculated 3 Nov 14:05, Full", "Changed since calculated", "Not calculated".</summary>
        public string StatusText { get; private set; } = "";

        /// <summary>"6 speakers · 120 m²".</summary>
        public string Summary { get; private set; } = "";

        public bool IsOutdated => Status == AreaResultStatus.Outdated;
        public bool IsCurrent => Status == AreaResultStatus.Current;

        /// <param name="resultsOnDisk">False when the area's job files are gone (it then counts as not calculated).</param>
        public void Update(AnalysisArea a, bool resultsOnDisk = true)
        {
            if (a == null) return;
            Name = a.Name;
            Status = resultsOnDisk ? a.ResultStatus : AreaResultStatus.NotCalculated;
            switch (Status)
            {
                case AreaResultStatus.Current:
                    StatusText = "Calculated " + (a.LastRunUtc?.ToLocalTime().ToString("d MMM HH:mm") ?? "") +
                                 (a.LastRunQuality.HasValue ? ", " + a.LastRunQuality.Value : "");
                    break;
                case AreaResultStatus.Outdated:
                    StatusText = "Changed since calculated";
                    break;
                default:
                    StatusText = "Not calculated";
                    break;
            }
            int speakers = a.SpeakerGroups.Sum(g => g.Instances.Count);
            double m2 = a.BoundaryRooms.Sum(r => r.Area);
            Summary = $"{speakers} speaker{(speakers == 1 ? "" : "s")}" + (m2 > 0 ? $" · {m2:F0} m²" : "");
            foreach (string p in new[] { nameof(Name), nameof(Status), nameof(StatusText), nameof(Summary), nameof(IsOutdated), nameof(IsCurrent) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }

        public override string ToString() => Name;

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
