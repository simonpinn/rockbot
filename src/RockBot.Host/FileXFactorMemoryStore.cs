using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// In-process x-factor memory store with PVC-backed persistence — the identical hot-dict plus
/// append-only-JSONL-plus-periodic-snapshot shape as <see cref="FileFailureClusterStore"/>,
/// since this is exactly the same "small records, many writes, needs restart-survival" shape.
/// See <c>design/cognitive-tiers.md</c>, "X-factor memory".
/// </summary>
internal sealed class FileXFactorMemoryStore : IXFactorMemoryStore, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string SnapshotFileName = "x-factor-memory.snapshot.json";
    private const string JsonlFileName = "x-factor-memory.jsonl";

    private readonly XFactorMemoryOptions _options;
    private readonly ILogger<FileXFactorMemoryStore> _logger;
    private readonly string _basePath;
    private readonly string _snapshotPath;
    private readonly string _jsonlPath;

    private readonly ConcurrentDictionary<XFactorMemoryKey, XFactorMemoryRecord> _records = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private Timer? _flushTimer;
    private bool _loaded;

    public FileXFactorMemoryStore(
        IOptions<XFactorMemoryOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileXFactorMemoryStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _basePath = ResolvePath(_options.BasePath, profileOptions.Value.BasePath);
        _snapshotPath = Path.Combine(_basePath, SnapshotFileName);
        _jsonlPath = Path.Combine(_basePath, JsonlFileName);
        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("X-factor memory store path: {Path}", _basePath);
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        if (_options.FlushInterval > TimeSpan.Zero)
        {
            _flushTimer = new Timer(
                _ => _ = FlushAsync(CancellationToken.None),
                state: null,
                dueTime: _options.FlushInterval,
                period: _options.FlushInterval);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_flushTimer is not null)
        {
            await _flushTimer.DisposeAsync();
            _flushTimer = null;
        }

        await FlushAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_flushTimer is not null)
        {
            await _flushTimer.DisposeAsync();
            _flushTimer = null;
        }
        _fileLock.Dispose();
    }

    // ── IXFactorMemoryStore ───────────────────────────────────────────────────

    public async Task<XFactorMemoryRecord?> RecordSightingAsync(
        XFactorMemoryKey key, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var prior = _records.TryGetValue(key, out var existing) ? existing : null;

            _records.AddOrUpdate(
                key,
                _ => new XFactorMemoryRecord(key, Count: 1, FirstSeen: at, LastSeen: at, _options.DefaultSalienceWeight),
                (_, current) => current with
                {
                    Count = current.Count + 1,
                    FirstSeen = current.FirstSeen <= at ? current.FirstSeen : at,
                    LastSeen = current.LastSeen >= at ? current.LastSeen : at,
                });

            var line = SerializeJsonlEvent(JsonlEventKind.Sighting, key, at, weight: null);
            await File.AppendAllTextAsync(_jsonlPath, line, cancellationToken);

            return prior;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public Task<XFactorMemoryRecord?> TryGetAsync(XFactorMemoryKey key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.TryGetValue(key, out var record) ? record : null);

    public Task<IReadOnlyList<XFactorMemoryRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<XFactorMemoryRecord> snapshot = _records.Values.OrderByDescending(r => r.LastSeen).ToList();
        return Task.FromResult(snapshot);
    }

    public async Task<XFactorMemoryRecord?> AdjustSalienceAsync(
        XFactorMemoryKey key, double newWeight, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!_records.TryGetValue(key, out var prior))
                return null;

            _records[key] = prior with { SalienceWeight = newWeight };

            var line = SerializeJsonlEvent(JsonlEventKind.Adjust, key, DateTimeOffset.UtcNow, newWeight);
            await File.AppendAllTextAsync(_jsonlPath, line, cancellationToken);

            return prior;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded) return;

            DateTimeOffset snapshotWrittenAt = DateTimeOffset.MinValue;
            var loaded = 0;

            if (File.Exists(_snapshotPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_snapshotPath, cancellationToken);
                    var dto = JsonSerializer.Deserialize<SnapshotDto>(json, JsonOptions);
                    if (dto is not null)
                    {
                        snapshotWrittenAt = dto.WrittenAt;
                        foreach (var r in dto.Records ?? [])
                        {
                            var record = RecordFromDto(r);
                            if (record is not null)
                            {
                                _records[record.Key] = record;
                                loaded++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load x-factor memory snapshot from {Path}", _snapshotPath);
                }
            }

            var replayed = 0;
            if (File.Exists(_jsonlPath))
            {
                using var stream = new FileStream(_jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JsonlEventDto? evt;
                    try
                    {
                        evt = JsonSerializer.Deserialize<JsonlEventDto>(line, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Skipping malformed x-factor memory JSONL line in {Path}", _jsonlPath);
                        continue;
                    }

                    if (evt is null || evt.Key is null) continue;
                    if (evt.At < snapshotWrittenAt) continue;

                    XFactorMemoryKey key;
                    try
                    {
                        key = new XFactorMemoryKey(evt.Key.Channel, evt.Key.Value);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    if (evt.Kind == JsonlEventKind.Adjust && evt.Weight is { } weight)
                    {
                        if (_records.TryGetValue(key, out var existing))
                            _records[key] = existing with { SalienceWeight = weight };
                    }
                    else
                    {
                        _records.AddOrUpdate(
                            key,
                            _ => new XFactorMemoryRecord(key, Count: 1, FirstSeen: evt.At, LastSeen: evt.At, _options.DefaultSalienceWeight),
                            (_, current) => current with
                            {
                                Count = current.Count + 1,
                                FirstSeen = current.FirstSeen <= evt.At ? current.FirstSeen : evt.At,
                                LastSeen = current.LastSeen >= evt.At ? current.LastSeen : evt.At,
                            });
                    }
                    replayed++;
                }
            }

            _loaded = true;

            if (loaded > 0 || replayed > 0)
            {
                _logger.LogInformation(
                    "Loaded {Loaded} x-factor record(s) from snapshot and replayed {Replayed} JSONL event(s)",
                    loaded, replayed);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string SerializeJsonlEvent(JsonlEventKind kind, XFactorMemoryKey key, DateTimeOffset at, double? weight)
    {
        var evt = new JsonlEventDto
        {
            Kind = kind,
            At = at,
            Key = new XFactorMemoryKeyDto { Channel = key.Channel, Value = key.Value },
            Weight = weight,
        };
        return JsonSerializer.Serialize(evt, JsonOptions) + Environment.NewLine;
    }

    /// <summary>Writes in-memory state to the snapshot atomically and truncates the JSONL log.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = new SnapshotDto
            {
                WrittenAt = DateTimeOffset.UtcNow,
                Records = _records.Values.OrderByDescending(r => r.LastSeen).Select(RecordToDto).ToList(),
            };

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tempPath = _snapshotPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, _snapshotPath, overwrite: true);

            if (File.Exists(_jsonlPath))
            {
                using var fs = new FileStream(_jsonlPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush x-factor memory snapshot");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // ── DTOs and conversions ──────────────────────────────────────────────────

    private enum JsonlEventKind { Sighting, Adjust }

    private sealed class SnapshotDto
    {
        public DateTimeOffset WrittenAt { get; set; }
        public List<XFactorMemoryRecordDto>? Records { get; set; }
    }

    private sealed class XFactorMemoryRecordDto
    {
        public XFactorMemoryKeyDto? Key { get; set; }
        public int Count { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public double SalienceWeight { get; set; }
    }

    private sealed class XFactorMemoryKeyDto
    {
        public string Channel { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class JsonlEventDto
    {
        public JsonlEventKind Kind { get; set; }
        public DateTimeOffset At { get; set; }
        public XFactorMemoryKeyDto? Key { get; set; }
        public double? Weight { get; set; }
    }

    private static XFactorMemoryRecordDto RecordToDto(XFactorMemoryRecord r) => new()
    {
        Key = new XFactorMemoryKeyDto { Channel = r.Key.Channel, Value = r.Key.Value },
        Count = r.Count,
        FirstSeen = r.FirstSeen,
        LastSeen = r.LastSeen,
        SalienceWeight = r.SalienceWeight,
    };

    private static XFactorMemoryRecord? RecordFromDto(XFactorMemoryRecordDto dto)
    {
        if (dto.Key is null) return null;

        XFactorMemoryKey key;
        try
        {
            key = new XFactorMemoryKey(dto.Key.Channel, dto.Key.Value);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new XFactorMemoryRecord(key, dto.Count, dto.FirstSeen, dto.LastSeen, dto.SalienceWeight);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolvePath(string path, string profileBasePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, path);
    }
}
