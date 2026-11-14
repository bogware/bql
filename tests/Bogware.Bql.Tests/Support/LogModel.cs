using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Bogware.Bql.Tests;

// A log-shaped model for the tests ported from Cabinet. It reproduces what Cabinet's engine saw,
// so the same queries give the same answers here. The library itself knows none of these names.

public enum LogSeverity
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    Fatal = 6,
}

public static class SourceTypes
{
    public const string Generic = "Generic";
    public const string Json = "JSON";
    public const string Apache = "Apache";
    public const string Nginx = "Nginx";
    public const string Syslog = "Syslog";
}

/// <summary>Time-ordered IDs, as Cabinet's ULIDs are.</summary>
public static class Ulid
{
    private static long _counter;

    public static string NewUlid() =>
        $"{DateTime.UtcNow.Ticks:X16}{Interlocked.Increment(ref _counter):X10}";
}

public sealed class LogEvent
{
    public string Id { get; set; } = Ulid.NewUlid();

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public DateTime IndexTime { get; set; } = DateTime.UtcNow;

    public string Index { get; set; } = "main";

    public string SourceType { get; set; } = SourceTypes.Generic;

    public string? Host { get; set; }

    public LogSeverity Severity { get; set; } = LogSeverity.Info;

    public required string RawLine { get; set; }

    public string? SourceFile { get; set; }

    public long LineNumber { get; set; }

    public ConcurrentDictionary<string, object?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetField(string key, object? value) => Fields[key] = value;
}

/// <summary>The schema Cabinet's rows have.</summary>
public static class LogSchema
{
    public static BqlSchema Instance { get; } = new()
    {
        TimeField = "_time",
        TimeFieldSynonyms = ["Timestamp"],
        TextField = "_raw",
        TextFieldSynonyms = ["RawLine"],
        DatasetField = "index",
        Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source_type"] = "SourceType",
            ["raw_line"] = "RawLine",
            ["source_file"] = "SourceFile",
            ["line_number"] = "LineNumber",
            ["level"] = "LogLevel",
            ["log_level"] = "LogLevel",
        },
        OrdinalFields = new Dictionary<string, BqlOrdinalScale>(StringComparer.OrdinalIgnoreCase)
        {
            ["severity"] = new BqlOrdinalScale(
            [
                new BqlOrdinalLevel(0, ["trace"]),
                new BqlOrdinalLevel(1, ["debug"]),
                new BqlOrdinalLevel(2, ["info", "information"]),
                new BqlOrdinalLevel(3, ["warning", "warn"]),
                new BqlOrdinalLevel(4, ["error", "err"]),
                new BqlOrdinalLevel(5, ["critical", "crit"]),
                new BqlOrdinalLevel(6, ["fatal"]),
            ]),
        },
        InternalFields = ["Id", "Timestamp", "SourceFile", "LineNumber", "Index", "SourceType", "Severity", "RawLine", "Message", "_time", "_raw", "_indextime", "Tags"],
        DefaultSort = new BqlSortOrder("_time", Descending: true),
        Fields =
        [
            new BqlField("_time", BqlFieldType.Time),
            new BqlField("_raw", BqlFieldType.String),
            new BqlField("host", BqlFieldType.String),
            new BqlField("sourcetype", BqlFieldType.String),
            new BqlField("index", BqlFieldType.String),
            new BqlField("severity", BqlFieldType.Integer),
        ],
    };

    /// <summary>A row as Cabinet's engine built one from an event.</summary>
    public static BqlRow ToRow(LogEvent evt)
    {
        var dict = Instance.NewRow();

        dict["Id"] = evt.Id;
        var timestamp = evt.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(evt.Timestamp, DateTimeKind.Utc) : evt.Timestamp.ToUniversalTime();
        dict["Timestamp"] = timestamp;
        dict["SourceType"] = evt.SourceType;
        dict["Host"] = evt.Host ?? "";
        dict["Index"] = evt.Index;
        dict["Severity"] = (int)evt.Severity;
        dict["RawLine"] = evt.RawLine;
        dict["SourceFile"] = evt.SourceFile ?? "";
        dict["LineNumber"] = evt.LineNumber;

        foreach (var field in evt.Fields)
            dict.TryAdd(field.Key, field.Value);

        dict.TryAdd("Message", evt.RawLine);

        if (evt.Tags.Count > 0)
            dict["Tags"] = evt.Tags.ToArray();

        dict["_time"] = timestamp;
        dict["_raw"] = evt.RawLine;
        dict["_indextime"] = evt.IndexTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(evt.IndexTime, DateTimeKind.Utc) : evt.IndexTime.ToUniversalTime();

        return dict;
    }
}

/// <summary>Reads JSON log lines as Cabinet's JSON parser does, for seeding tests.</summary>
public sealed class JsonLogParser
{
    private static readonly string[] TimestampFieldNames = ["timestamp", "@timestamp", "time", "datetime", "date", "ts", "eventTime", "created_at", "_time", "event.created"];
    private static readonly string[] MessageFieldNames = ["message", "msg", "text", "body", "log", "description", "event.original"];
    private static readonly string[] LevelFieldNames = ["level", "severity", "loglevel", "log_level", "priority", "sev", "log.level"];
    private static readonly string[] HostFieldNames = ["host", "hostname", "server", "node", "host.name"];

    public Task<LogEvent?> ParseAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return Task.FromResult<LogEvent?>(null);
        }

        var evt = new LogEvent { RawLine = line, SourceType = SourceTypes.Json, Timestamp = DateTime.UtcNow, Severity = LogSeverity.Info };
        using var doc = JsonDocument.Parse(line);
        Extract(doc.RootElement, string.Empty, 0, evt);
        evt.Tags.Add("json");
        return Task.FromResult<LogEvent?>(evt);
    }

    private static void Extract(JsonElement element, string prefix, int depth, LogEvent evt)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var key = prefix + prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Object && depth < 8)
            {
                Extract(prop.Value, key + ".", depth + 1, evt);
                continue;
            }

            object? value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };

            if (TimestampFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && TryTime(value, out var timestamp))
            {
                evt.Timestamp = timestamp;
            }
            else if (MessageFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value != null)
            {
                evt.SetField("Message", value.ToString()!);
            }
            else if (LevelFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value != null)
            {
                if (value is string text && TryLevel(text, out var level))
                {
                    evt.Severity = level;
                }

                evt.SetField("LogLevel", value.ToString()!);
            }
            else if (HostFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value is string host)
            {
                evt.Host = host;
            }
            else if (value != null)
            {
                evt.SetField(key, value);
            }
        }
    }

    private static bool TryLevel(string level, out LogSeverity severity)
    {
        severity = level.Trim().ToLowerInvariant() switch
        {
            "trace" or "verbose" => LogSeverity.Trace,
            "debug" or "dbg" => LogSeverity.Debug,
            "warn" or "warning" => LogSeverity.Warning,
            "error" or "err" or "fail" or "failure" => LogSeverity.Error,
            "critical" or "crit" => LogSeverity.Critical,
            "fatal" or "panic" or "emerg" or "emergency" or "alert" => LogSeverity.Fatal,
            _ => LogSeverity.Info,
        };
        return true;
    }

    private static bool TryTime(object? value, out DateTime timestamp)
    {
        timestamp = default;
        const DateTimeStyles Utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        switch (value)
        {
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                return FromEpoch(number, out timestamp);
            case string text:
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, Utc, out timestamp);
            case long number:
                return FromEpoch(number, out timestamp);
            case double number:
                return FromEpoch(number, out timestamp);
            default:
                return false;
        }
    }

    private static bool FromEpoch(double value, out DateTime timestamp)
    {
        timestamp = default;
        if (!double.IsFinite(value) || value <= 0)
        {
            return false;
        }

        var seconds = value switch
        {
            < 1e11 => value,
            < 1e14 => value / 1e3,
            < 1e17 => value / 1e6,
            _ => value / 1e9,
        };
        timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seconds).UtcDateTime;
        return true;
    }
}
