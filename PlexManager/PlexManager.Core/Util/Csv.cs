using System.Text;

namespace PlexManager.Core.Util;

/// <summary>The one CSV reader and writer, extracted from the five console tools that each had their own copy.</summary>
public static class Csv
{
    /// <summary>Splits one CSV line, honouring quotes and doubled quotes.</summary>
    public static List<string> Split(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }

    public static string Cell(string? value)
    {
        var s = value ?? "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    /// <summary>Writes a UTF-8 CSV with a BOM, so Excel opens accented titles correctly.</summary>
    public static void Write(string path, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", header.Select(Cell))).Append('\n');
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Cell))).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Writes rows held as column-name to value maps, in the given column order.</summary>
    public static void WriteTable(string path, IReadOnlyList<string> header, IEnumerable<IDictionary<string, string>> rows) =>
        Write(path, header, rows.Select(r => (IReadOnlyList<string>)header.Select(h => r.TryGetValue(h, out var v) ? v : "").ToList()));

    /// <summary>Reads a CSV into header plus one dictionary per row. Missing trailing cells read as empty.</summary>
    public static CsvTable Read(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
        {
            return new CsvTable([], []);
        }

        var header = Split(lines[0].TrimStart('﻿'));
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var cells = Split(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
            {
                row[header[i]] = i < cells.Count ? cells[i] : "";
            }

            rows.Add(row);
        }

        return new CsvTable(header, rows);
    }
}

/// <summary>A CSV read into memory: its column names and its rows.</summary>
public sealed record CsvTable(List<string> Header, List<Dictionary<string, string>> Rows)
{
    public bool Has(string column) => Header.Any(h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase));

    public string Require(string column) =>
        Header.FirstOrDefault(h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"Column {column} is missing.");
}
