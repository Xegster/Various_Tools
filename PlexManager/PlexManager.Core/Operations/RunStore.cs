using System.Text.Json;
using System.Text.Json.Serialization;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// The store behind the review screens (the roadmap's "rewrite the CSVs as HTML"): every run of an operation is
/// recorded with its rows, so a screen can show them, let the user edit the one editable column and apply the result.
/// The CSV is still written to Reports in exactly the same place and shape as before, so the CLI and the old reports keep working.
/// </summary>
public sealed class RunStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppPaths _paths;

    public RunStore(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(paths.Runs);
    }

    /// <summary>Records a finished run. <paramref name="id"/> is the timestamped folder name, so runs sort by time.</summary>
    public RunRecord Save(RunRecord run)
    {
        var dir = Path.Combine(_paths.Runs, run.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.json"), JsonSerializer.Serialize(run, Options));
        return run;
    }

    public RunRecord? Load(string id)
    {
        var file = Path.Combine(_paths.Runs, id, "run.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), Options) : null;
    }

    /// <summary>Newest first. <paramref name="kind"/> filters to one operation ("scan", "migrate-plan", ...).</summary>
    public List<RunRecord> List(string? kind = null, int limit = 100)
    {
        if (!Directory.Exists(_paths.Runs))
        {
            return [];
        }

        var runs = new List<RunRecord>();
        foreach (var dir in Directory.GetDirectories(_paths.Runs).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "run.json");
            if (!File.Exists(file))
            {
                continue;
            }

            RunRecord? run;
            try
            {
                run = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (run == null || (kind != null && run.Kind != kind))
            {
                continue;
            }

            runs.Add(run);
            if (runs.Count >= limit)
            {
                break;
            }
        }

        return runs;
    }

    /// <summary>The newest run of a kind, or null.</summary>
    public RunRecord? Latest(string kind) => List(kind, 1).FirstOrDefault();

    /// <summary>
    /// Writes edited rows back to the run and to its CSV, so the apply step (which reads the CSV, as the CLI does)
    /// sees exactly what was reviewed on screen. Only columns the run marks editable are taken from the edit.
    /// </summary>
    public RunRecord UpdateRows(string id, List<Dictionary<string, string>> edited)
    {
        var run = Load(id) ?? throw new FileNotFoundException($"No run {id}.");
        if (run.KeyColumn.Length == 0)
        {
            throw new InvalidOperationException($"Run {id} cannot be edited.");
        }

        var byKey = edited
            .Where(r => r.TryGetValue(run.KeyColumn, out var k) && k.Length > 0)
            .GroupBy(r => r[run.KeyColumn], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (var row in run.Rows)
        {
            if (!row.TryGetValue(run.KeyColumn, out var key) || !byKey.TryGetValue(key, out var change))
            {
                continue;
            }

            foreach (var column in run.EditableColumns)
            {
                if (change.TryGetValue(column, out var value))
                {
                    row[column] = value;
                }
            }
        }

        if (run.CsvPath.Length > 0)
        {
            Csv.WriteTable(run.CsvPath, run.Columns, run.Rows);
        }

        run.ReviewedAt = DateTimeOffset.Now;
        return Save(run);
    }
}

/// <summary>One run of one operation: what it produced, where its report went, and the rows a screen can review.</summary>
public sealed class RunRecord
{
    /// <summary>Folder name, "&lt;kind&gt;-yyyyMMdd-HHmmss".</summary>
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Title { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Whether the run only read, or changed something.</summary>
    public bool Applied { get; set; }

    /// <summary>Headline numbers for the dashboard and the run list.</summary>
    public List<string> Summary { get; set; } = [];

    public List<string> Columns { get; set; } = [];

    public List<Dictionary<string, string>> Rows { get; set; } = [];

    /// <summary>Column that identifies a row when edits are merged back (for example RatingKey or TraktId).</summary>
    public string KeyColumn { get; set; } = "";

    /// <summary>Columns the review screen may change (for example Approve, Keep or Action).</summary>
    public List<string> EditableColumns { get; set; } = [];

    /// <summary>Full path of the CSV this run wrote under Reports, kept so the apply step and Excel both work.</summary>
    public string CsvPath { get; set; } = "";

    public string HtmlPath { get; set; } = "";

    /// <summary>The operation and arguments that carry this run forward, shown as the next step on screen.</summary>
    public string? NextStep { get; set; }

    public static string NewId(string kind) => $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}";
}
