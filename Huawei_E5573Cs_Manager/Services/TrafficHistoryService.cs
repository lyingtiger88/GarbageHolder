using System.IO;
using System.Text.Json;
using ModemManagerNative.Models;

namespace ModemManagerNative.Services;

public sealed class TrafficHistoryService
{
    private readonly string _path;
    private TrafficHistoryState _state;

    public TrafficHistoryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BDFR",
            "HuaweiE5573CsManager");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "traffic-history.json");
        _state = Load();
    }

    public IReadOnlyList<QuarterTrafficRecord> Update(TrafficStatistics stats)
    {
        if (stats.TotalUploadBytes is null || stats.TotalDownloadBytes is null)
            return GetRecords();

        var now = DateTimeOffset.Now;
        var currentQuarter = QuarterKey(now);

        if (_state.LastTotalUploadBytes is not null &&
            _state.LastTotalDownloadBytes is not null &&
            _state.LastSeenUtc is not null)
        {
            var previousQuarter = QuarterKey(_state.LastSeenUtc.Value.ToLocalTime());

            // If the app was not observed across a quarter boundary, the modem only
            // gives us one lifetime delta. Do not guess how that delta should be split.
            if (previousQuarter == currentQuarter)
            {
                var upDelta = Delta(_state.LastTotalUploadBytes.Value, stats.TotalUploadBytes.Value);
                var downDelta = Delta(_state.LastTotalDownloadBytes.Value, stats.TotalDownloadBytes.Value);

                if (!_state.Quarters.TryGetValue(currentQuarter, out var bucket))
                    bucket = new QuarterTrafficBucket();

                bucket.UploadBytes += upDelta;
                bucket.DownloadBytes += downDelta;
                _state.Quarters[currentQuarter] = bucket;
            }
        }

        _state.LastTotalUploadBytes = stats.TotalUploadBytes;
        _state.LastTotalDownloadBytes = stats.TotalDownloadBytes;
        _state.LastSeenUtc = now.UtcDateTime;
        Save();
        return GetRecords();
    }

    public IReadOnlyList<QuarterTrafficRecord> GetRecords() =>
        _state.Quarters
            .OrderByDescending(x => x.Key, StringComparer.Ordinal)
            .Select(x => new QuarterTrafficRecord(
                x.Key,
                x.Value.UploadBytes,
                x.Value.DownloadBytes))
            .ToList();

    private static long Delta(long previous, long current) =>
        current >= previous ? current - previous : Math.Max(0, current);

    private TrafficHistoryState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new TrafficHistoryState();
            return JsonSerializer.Deserialize<TrafficHistoryState>(File.ReadAllText(_path))
                   ?? new TrafficHistoryState();
        }
        catch
        {
            return new TrafficHistoryState();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_state, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // Traffic history is a convenience feature; modem management must keep working
            // even if the local profile directory is read-only or temporarily unavailable.
        }
    }

    private static string QuarterKey(DateTimeOffset value)
    {
        var quarter = ((value.Month - 1) / 3) + 1;
        return $"{value.Year}-Q{quarter}";
    }

    private sealed class TrafficHistoryState
    {
        public long? LastTotalUploadBytes { get; set; }
        public long? LastTotalDownloadBytes { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public Dictionary<string, QuarterTrafficBucket> Quarters { get; set; } = new();
    }

    private sealed class QuarterTrafficBucket
    {
        public long UploadBytes { get; set; }
        public long DownloadBytes { get; set; }
    }
}

public sealed record QuarterTrafficRecord(
    string Quarter,
    long UploadBytes,
    long DownloadBytes)
{
    public string UploadDisplay => TrafficFormat.Bytes(UploadBytes);
    public string DownloadDisplay => TrafficFormat.Bytes(DownloadBytes);
    public string TotalDisplay => TrafficFormat.Bytes(UploadBytes + DownloadBytes);
}
