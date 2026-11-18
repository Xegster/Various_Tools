using System.Collections.Concurrent;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

namespace PlexManager.Core.Jobs;

public enum JobState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One long operation running in the background, with its log kept so a screen can poll for new lines and a
/// Cancel button can stop it.
/// </summary>
public sealed class Job : IOperationLog
{
    private readonly List<JobLine> _lines = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _logFile;

    internal Job(string id, string operation, string description, string? logFile)
    {
        Id = id;
        Operation = operation;
        Description = description;
        StartedAt = DateTimeOffset.Now;
        _logFile = logFile;
    }

    public string Id { get; }

    public string Operation { get; }

    public string Description { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public JobState State { get; private set; } = JobState.Running;

    public string? Error { get; private set; }

    /// <summary>The run the operation recorded, once it has finished.</summary>
    public RunRecord? Run { get; private set; }

    public int? Percent { get; private set; }

    public CancellationToken Token => _cts.Token;

    public void Report(OperationEvent e)
    {
        lock (_gate)
        {
            _lines.Add(new JobLine(_lines.Count, e.Level.ToString().ToLowerInvariant(), e.Message, e.When));
            if (e.Percent.HasValue)
            {
                Percent = e.Percent;
            }
        }

        if (_logFile != null)
        {
            try
            {
                File.AppendAllText(_logFile, $"{e.When:HH:mm:ss} [{e.Level}] {e.Message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // A log file that cannot be written must not stop the operation.
            }
        }
    }

    /// <summary>The lines from <paramref name="from"/> onwards, for polling without re-sending the whole log.</summary>
    public List<JobLine> LinesFrom(int from)
    {
        lock (_gate)
        {
            return from <= 0 ? [.. _lines] : _lines.Where(l => l.Index >= from).ToList();
        }
    }

    public int LineCount
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    public void Cancel() => _cts.Cancel();

    internal void Finish(JobState state, string? error, RunRecord? run)
    {
        State = state;
        Error = error;
        Run = run;
        FinishedAt = DateTimeOffset.Now;
    }

    internal void Dispose() => _cts.Dispose();
}

public sealed record JobLine(int Index, string Level, string Message, DateTimeOffset At);

/// <summary>
/// Starts operations in the background and keeps the recent ones so the UI can show a live log. One write
/// operation runs at a time, because they all talk to the same three servers.
/// </summary>
public sealed class JobRunner
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly SettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly RunStore _runs;
    private int _counter;

    public JobRunner(SettingsStore settings, AppPaths paths, RunStore runs)
    {
        _settings = settings;
        _paths = paths;
        _runs = runs;
    }

    /// <summary>The job that is running now, if any.</summary>
    public Job? Active => _jobs.Values
        .Where(j => j.State == JobState.Running)
        .OrderBy(j => j.StartedAt)
        .FirstOrDefault();

    public Job? Get(string id) => _jobs.GetValueOrDefault(id);

    public List<Job> Recent(int limit = 20) => _jobs.Values
        .OrderByDescending(j => j.StartedAt)
        .Take(limit)
        .ToList();

    /// <summary>
    /// Starts <paramref name="work"/> on a background task. The returned job carries the log; the operation's run
    /// record is attached when it finishes.
    /// </summary>
    public Job Start(string operation, string description, Func<OperationContext, CancellationToken, Task<RunRecord?>> work)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _counter)}";
        Directory.CreateDirectory(_paths.Logs);
        var job = new Job(id, operation, description, Path.Combine(_paths.Logs, $"{id}-{operation}.log"));
        _jobs[id] = job;

        _ = Task.Run(async () =>
        {
            await _oneAtATime.WaitAsync(job.Token);
            try
            {
                using var context = new OperationContext(_settings, _paths, _runs, job);
                var run = await work(context, job.Token);
                job.Finish(JobState.Succeeded, null, run);
            }
            catch (OperationCanceledException)
            {
                job.Report(new OperationEvent(OperationLevel.Warning, "Cancelled."));
                job.Finish(JobState.Cancelled, "Cancelled.", null);
            }
            catch (Exception ex)
            {
                job.Report(new OperationEvent(OperationLevel.Error, ex.Message));
                job.Finish(JobState.Failed, ex.Message, null);
            }
            finally
            {
                _oneAtATime.Release();
                Prune();
            }
        });

        return job;
    }

    /// <summary>Keeps the 40 most recent jobs; older finished ones are dropped.</summary>
    private void Prune()
    {
        foreach (var old in _jobs.Values
                     .Where(j => j.State != JobState.Running)
                     .OrderByDescending(j => j.StartedAt)
                     .Skip(40)
                     .ToList())
        {
            if (_jobs.TryRemove(old.Id, out var removed))
            {
                removed.Dispose();
            }
        }
    }
}
