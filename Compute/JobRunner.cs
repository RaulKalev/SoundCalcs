using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoundCalcs.Domain;
using SoundCalcs.IO;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Runs an acoustic computation job on a background thread.
    /// Provides cancellation and progress reporting.
    /// Writes results to disk via JobSerializer.
    /// </summary>
    public class JobRunner
    {
        private CancellationTokenSource _cts;
        private Task _runningTask;

        public bool IsRunning => _runningTask != null && !_runningTask.IsCompleted;

        /// <summary>
        /// Fired on the thread pool when the job completes (success or failure).
        /// UI must marshal to dispatcher.
        /// </summary>
        public event Action<AcousticJobOutput> JobCompleted;

        /// <summary>
        /// Start the compute job on a background thread.
        /// </summary>
        /// <param name="input">Fully populated job input.</param>
        /// <param name="progress">Progress reporter (0.0 to 1.0).</param>
        public void Start(AcousticJobInput input, IProgress<double> progress)
        {
            if (IsRunning)
            {
                Debug.WriteLine("[SoundCalcs] Job already running. Cancel first.");
                return;
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            _runningTask = Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                AcousticJobOutput output = null;

                try
                {
                    Debug.WriteLine($"[SoundCalcs] Job {input.JobId} started. " +
                        $"Sources={input.Sources.Count}, Receivers={input.Receivers.Count}");

                    if (input.Rooms != null && input.Rooms.Count > 0)
                    {
                        IO.FileLogger.Log($"[SoundCalcs] Job Input Room[0] vertices:");
                        foreach (var v in input.Rooms[0].Vertices)
                            IO.FileLogger.Log($"  ({v.X:F3}, {v.Y:F3})");
                    }

                    // Save input for debugging / reload
                    JobSerializer.SaveInput(input);

                    var calculator = new SPLCalculator();
                    var (results, bandData) = calculator.Calculate(input, token, progress);

                    // Full IEC 60268-16 MTF-based STI computation
                    STICalculator.Calculate(
                        results, bandData,
                        input.Environment.BackgroundNoiseByBand,
                        input.Environment.RT60ByBand,
                        input.Environment.SpeechWeightType);

                    stopwatch.Stop();

                    output = new AcousticJobOutput
                    {
                        JobId = input.JobId,
                        Timestamp = DateTime.UtcNow,
                        ComputeTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                        SourceCount = input.Sources.Count,
                        ReceiverCount = results.Count,
                        Results = results,
                        Rooms = input.Rooms,
                        WasCanceled = false,
                        Quality = input.Quality
                    };

                    if (results.Count > 0)
                    {
                        output.MinSplDb = results.Min(r => r.SplDb);
                        output.MaxSplDb = results.Max(r => r.SplDb);
                        output.MinSti = results.Min(r => r.Sti);
                        output.MaxSti = results.Max(r => r.Sti);

                        // Per-band min/max for per-band heatmap visualization
                        int numBands = OctaveBands.Count;
                        output.MinSplDbByBand = new double[numBands];
                        output.MaxSplDbByBand = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                        {
                            int band = k;
                            output.MinSplDbByBand[k] = results.Min(r =>
                                r.SplDbByBand != null && r.SplDbByBand.Length > band
                                    ? r.SplDbByBand[band] : 0);
                            output.MaxSplDbByBand[k] = results.Max(r =>
                                r.SplDbByBand != null && r.SplDbByBand.Length > band
                                    ? r.SplDbByBand[band] : 0);
                        }
                    }

                    JobSerializer.SaveOutput(output);

                    Debug.WriteLine($"[SoundCalcs] Job {input.JobId} completed in " +
                        $"{output.ComputeTimeSeconds:F2}s. SPL range: " +
                        $"{output.MinSplDb:F1} - {output.MaxSplDb:F1} dB, " +
                        $"STI range: {output.MinSti:F2} - {output.MaxSti:F2}");
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    Debug.WriteLine($"[SoundCalcs] Job {input.JobId} canceled.");

                    output = new AcousticJobOutput
                    {
                        JobId = input.JobId,
                        Timestamp = DateTime.UtcNow,
                        ComputeTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                        WasCanceled = true
                    };
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    Debug.WriteLine($"[SoundCalcs] Job {input.JobId} failed: {ex.Message}");

                    output = new AcousticJobOutput
                    {
                        JobId = input.JobId,
                        Timestamp = DateTime.UtcNow,
                        ComputeTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                        WasCanceled = false
                    };
                }

                JobCompleted?.Invoke(output);
            }, token);
        }

        /// <summary>
        /// Request cancellation of the running job.
        /// </summary>
        public void Cancel()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                Debug.WriteLine("[SoundCalcs] Cancellation requested.");
            }
        }
    }
}
