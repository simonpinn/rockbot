using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Stimulus;

/// <summary>
/// Hosted service that loads stimulus pattern files during startup and watches the directory
/// for changes, reloading automatically — a direct copy of <c>RockBot.Host.AgentProfileLoader</c>'s
/// shape (watcher + debounce + poll fallback), because stimulus patterns sit on the exact same
/// PR-reviewed, image/PVC-deployed surface <c>AgentProfileLoader</c> already watches for
/// <c>soul.md</c>/<c>directives.md</c>, and the same Docker Desktop/Longhorn filesystem
/// unreliability applies identically here.
/// </summary>
internal sealed class StimulusRegistryLoader : IHostedService, IDisposable
{
    private readonly StimulusPatternHolder _holder;
    private readonly StimulusOptions _options;
    private readonly AgentProfileOptions _profileOptions;
    private readonly ILogger<StimulusRegistryLoader> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private int _reloadPending;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private string? _baseDir;
    private string? _lastStamp;

    public StimulusRegistryLoader(
        StimulusPatternHolder holder,
        IOptions<StimulusOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<StimulusRegistryLoader> logger)
    {
        _holder = holder;
        _options = options.Value;
        _profileOptions = profileOptions.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _baseDir = ResolvePath(_options.BasePath, _profileOptions.BasePath);
        _logger.LogInformation("Loading stimulus patterns from {Path}...", _baseDir);

        _holder.Update(LoadPatternsFromDirectory(_baseDir, _logger));
        _lastStamp = ReadDirectoryStamp(_baseDir);

        StartWatching();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        StopWatching();

        if (_pollCts is not null)
            await _pollCts.CancelAsync();

        if (_pollTask is not null)
            await _pollTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private void StartWatching()
    {
        if (_baseDir is null || !Directory.Exists(_baseDir))
        {
            _logger.LogWarning("Stimulus directory {Path} does not exist, file watching disabled", _baseDir);
            return;
        }

        _watcher = new FileSystemWatcher(_baseDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileChanged;

        _logger.LogInformation("Watching stimulus directory {Path} for changes", _baseDir);

        if (_options.PollIntervalSeconds > 0)
        {
            _pollCts = new CancellationTokenSource();
            _pollTask = RunPollAsync(_pollCts.Token);
        }
    }

    private void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounce?.Dispose();
        _debounce = null;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => TriggerReload("watcher");

    /// <summary>
    /// Polling fallback — see <c>AgentProfileLoader.RunPollAsync</c> for why this exists
    /// (inotify is never delivered for host-side edits on a Docker Desktop bind mount, and is
    /// unreliable on network/overlay filesystems such as Longhorn PVCs).
    /// </summary>
    private async Task RunPollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_baseDir is null) continue;

                var current = ReadDirectoryStamp(_baseDir);
                if (current is not null && current != _lastStamp)
                    TriggerReload("poll");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void TriggerReload(string reason)
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) != 0)
            return;

        _debounce?.Dispose();
        _debounce = new Timer(_ => Reload(reason), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    private void Reload(string reason)
    {
        try
        {
            _logger.LogInformation("Stimulus directory change detected via {Reason}, reloading...", reason);
            _holder.Update(LoadPatternsFromDirectory(_baseDir!, _logger));
            _logger.LogInformation("Stimulus patterns reloaded (version {Version})", _holder.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload stimulus patterns, keeping previous version");
        }
        finally
        {
            if (_baseDir is not null)
                _lastStamp = ReadDirectoryStamp(_baseDir);
            Interlocked.Exchange(ref _reloadPending, 0);
        }
    }

    /// <summary>
    /// Loads every <c>*.json</c> file directly in <paramref name="directory"/> as a
    /// <see cref="StimulusPattern"/>. A file that fails to parse, or whose <c>reflexTool</c>
    /// does not start with <see cref="StimulusPattern.ReflexToolPrefix"/>, is logged and
    /// excluded — the rest of the directory still loads. Missing directory returns empty,
    /// never throws.
    /// </summary>
    internal static IReadOnlyList<StimulusPattern> LoadPatternsFromDirectory(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory))
            return [];

        var patterns = new List<StimulusPattern>();
        foreach (var path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            StimulusPattern? pattern;
            try
            {
                var json = File.ReadAllText(path);
                pattern = JsonSerializer.Deserialize<StimulusPattern>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "Skipping unreadable/malformed stimulus pattern file {Path}", path);
                continue;
            }

            if (pattern is null)
            {
                logger.LogWarning("Stimulus pattern file {Path} deserialized to null, skipping", path);
                continue;
            }

            if (!pattern.IsValid)
            {
                logger.LogWarning(
                    "Stimulus pattern {Id} in {Path} names reflexTool '{ReflexTool}' which does not start " +
                    "with '{Prefix}' — rejected, this dispatcher can never fire anything but a validated reflex",
                    pattern.Id, path, pattern.ReflexTool, StimulusPattern.ReflexToolPrefix);
                continue;
            }

            patterns.Add(pattern);
        }

        return patterns;
    }

    /// <summary>Fingerprint of every <c>*.json</c> file directly in <paramref name="directory"/> — mirrors <c>AgentProfileLoader.ReadDirectoryStamp</c>.</summary>
    internal static string? ReadDirectoryStamp(string directory)
    {
        try
        {
            var entries = new DirectoryInfo(directory)
                .GetFiles("*.json", SearchOption.TopDirectoryOnly)
                .Select(f => $"{f.Name}:{f.LastWriteTimeUtc.Ticks}:{f.Length}")
                .Order(StringComparer.Ordinal);
            return string.Join('|', entries);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePath(string path, string basePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(AppContext.BaseDirectory, basePath);

        return Path.Combine(baseDir, path);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public void Dispose()
    {
        StopWatching();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }
}
