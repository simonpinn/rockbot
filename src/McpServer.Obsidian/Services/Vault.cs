using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Obsidian.Services;

public sealed record NoteInfo(string Path, string Title, DateTimeOffset Modified, long Size);

public sealed record SearchHit(string Path, string Title, string Modified, string? Snippet);

public sealed record Note(string Path, string? Frontmatter, string Content, string Modified, bool Truncated);

/// <summary>
/// All filesystem access to the vault. Paths from tool arguments are vault-relative; every one is
/// checked to stay inside the vault, avoid hidden folders (.obsidian, .trash) and symlinks, and name a
/// markdown note. Writes are allowed only under <see cref="ObsidianOptions.WriteFolder"/>.
/// </summary>
public sealed partial class Vault
{
    private const long MaxSearchFileBytes = 1_000_000;
    private const int MaxScannedFiles = 50_000;
    private const int SnippetRadius = 100;
    private const int MaxTitle = 120;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    private readonly string _root;
    private readonly TimeZoneInfo _zone;
    private readonly string _vaultName;

    public Vault(IOptions<ObsidianOptions> options)
    {
        var o = options.Value;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(o.VaultPath));
        _zone = TryFindZone(o.TimeZone) ?? TimeZoneInfo.Utc;
        _vaultName = o.VaultName.Trim();
        WriteFolder = NormalisePath(o.WriteFolder);
    }

    /// <summary>The vault-relative folder writes are confined to.</summary>
    public string WriteFolder { get; }

    public static TimeZoneInfo? TryFindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith('/') || p.Contains(':') || p.Any(char.IsControl))
            return false;
        var segments = p.TrimEnd('/').Split('/');
        return segments.All(s => s.Length > 0 && !s.StartsWith('.') && s.Trim().Length > 0);
    }

    // ---- reads ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string? folder, int top, CancellationToken ct)
    {
        var phrase = query.Trim();
        var terms = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hits = new List<(int Rank, NoteInfo Note, string? Snippet)>();

        foreach (var note in EnumerateNotes(folder))
        {
            ct.ThrowIfCancellationRequested();
            var text = note.Size <= MaxSearchFileBytes ? await ReadTextAsync(note.Path, ct) : "";
            var haystack = note.Path + "\n" + text;
            if (!terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            var rank = note.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase) ? 0
                : terms.All(t => note.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 1
                : 2;
            hits.Add((rank, note, Snippet(text, terms)));
        }

        return hits
            .OrderBy(h => h.Rank).ThenByDescending(h => h.Note.Modified)
            .Take(top)
            .Select(h => new SearchHit(h.Note.Path, h.Note.Title, Format(h.Note.Modified), h.Snippet))
            .ToList();
    }

    public async Task<Note> ReadNoteAsync(string path, int maxChars, CancellationToken ct)
    {
        var rel = NotePath(path);
        var full = FullPath(rel);
        if (!File.Exists(full))
            throw new McpException($"note not found: {rel}");

        var text = NormaliseText(await File.ReadAllTextAsync(full, ct));
        string? frontmatter = null;
        var match = FrontmatterPattern().Match(text);
        if (match.Success)
        {
            frontmatter = match.Groups[1].Value;
            text = text[match.Length..];
        }

        var truncated = text.Length > maxChars;
        if (truncated)
            text = text[..maxChars] + $"\n\n[truncated: the note is {text.Length:N0} characters]";
        return new Note(rel, frontmatter, text, Format(ToZone(File.GetLastWriteTimeUtc(full))), truncated);
    }

    public IReadOnlyList<NoteInfo> ListNotes(string? folder, int top, bool byName)
    {
        var notes = EnumerateNotes(folder);
        var sorted = byName
            ? notes.OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            : notes.OrderByDescending(n => n.Modified);
        return sorted.Take(top).ToList();
    }

    public IReadOnlyList<string> ListFolders(string? folder)
    {
        var rel = FolderPath(folder);
        var full = RequireDirectory(rel);
        return Directory.EnumerateDirectories(full, "*", TopLevel)
            .Select(RelativeOf)
            .Where(p => !p.Split('/').Any(s => s.StartsWith('.')))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Format(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    // ---- writes --------------------------------------------------------------------------------

    /// <summary>Creates a note under the write folder and returns its vault-relative path.</summary>
    public async Task<string> CreateNoteAsync(
        string title, string content, string? folder, IReadOnlyList<string> tags, string? source, bool overwrite, CancellationToken ct)
    {
        var name = SanitiseTitle(title);
        var folderRel = string.IsNullOrWhiteSpace(folder) ? WriteFolder : $"{WriteFolder}/{RequireRelative(folder, "folder")}";
        Directory.CreateDirectory(FullPath(folderRel));

        var body = Frontmatter(tags, source) + NormaliseText(content).Trim() + "\n";
        for (var n = 1; n <= 100; n++)
        {
            var rel = $"{folderRel}/{(n == 1 ? name : $"{name} ({n})")}.md";
            var full = FullPath(rel);
            if (!overwrite && File.Exists(full))
                continue;
            try
            {
                await WriteAtomicAsync(full, body, overwrite, ct);
                return rel;
            }
            catch (IOException) when (!overwrite && File.Exists(full))
            {
                // Created between the check and the move; try the next name.
            }
        }
        throw new McpException($"too many notes are already named '{name}'");
    }

    /// <summary>Appends to an existing note in the write folder, under <paramref name="heading"/> when given.</summary>
    public async Task<string> AppendToNoteAsync(string path, string content, string? heading, CancellationToken ct)
    {
        var rel = NotePath(path);
        if (!rel.StartsWith(WriteFolder + "/", StringComparison.Ordinal))
            throw new McpException($"only notes under {WriteFolder}/ can be changed; {rel} is read-only");
        var full = FullPath(rel);
        if (!File.Exists(full))
            throw new McpException($"note not found: {rel}");

        var text = NormaliseText(await File.ReadAllTextAsync(full, ct));
        await WriteAtomicAsync(full, InsertUnderHeading(text, NormaliseText(content).Trim(), heading), replace: true, ct);
        return rel;
    }

    /// <summary>An obsidian:// link that opens the note, or null when no vault name is configured.</summary>
    public string? LinkTo(string rel) =>
        _vaultName.Length == 0
            ? null
            : $"obsidian://open?vault={Uri.EscapeDataString(_vaultName)}&file={Uri.EscapeDataString(rel[..^3])}";

    internal static string InsertUnderHeading(string text, string content, string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
            return $"{text.TrimEnd()}\n\n{content}\n";

        var wanted = heading.Trim().TrimStart('#').Trim();
        var lines = text.Split('\n');
        var start = -1;
        var level = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = HeadingPattern().Match(lines[i]);
            if (m.Success && string.Equals(m.Groups[2].Value.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                level = m.Groups[1].Length;
                break;
            }
        }
        if (start < 0)
            return $"{text.TrimEnd()}\n\n## {wanted}\n\n{content}\n";

        // The section runs until the next heading of the same or a higher level.
        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var m = HeadingPattern().Match(lines[i]);
            if (m.Success && m.Groups[1].Length <= level)
            {
                end = i;
                break;
            }
        }

        var before = string.Join('\n', lines.Take(end)).TrimEnd();
        var rest = string.Join('\n', lines.Skip(end)).Trim();
        return rest.Length == 0 ? $"{before}\n\n{content}\n" : $"{before}\n\n{content}\n\n{rest}\n";
    }

    internal static string SanitiseTitle(string title)
    {
        var cleaned = new string(title.Select(c => char.IsControl(c) || "\\/:*?\"<>|#^[]".Contains(c) ? ' ' : c).ToArray());
        cleaned = Whitespace().Replace(cleaned, " ").Trim().Trim('.').Trim();
        if (cleaned.Length > MaxTitle)
            cleaned = cleaned[..MaxTitle].Trim().TrimEnd('.');
        if (!cleaned.Any(char.IsLetterOrDigit))
            throw new McpException("title must contain at least one letter or digit");
        return cleaned;
    }

    private string Frontmatter(IReadOnlyList<string> tags, string? source)
    {
        var sb = new StringBuilder("---\n");
        sb.Append("created: ").Append(ToZone(DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrWhiteSpace(source))
            sb.Append("source: ").Append(JsonSerializer.Serialize(source.Trim())).Append('\n');   // JSON strings are valid YAML scalars
        sb.Append("tags:\n");
        foreach (var tag in tags)
            sb.Append("  - ").Append(tag).Append('\n');
        return sb.Append("---\n\n").ToString();
    }

    private static async Task WriteAtomicAsync(string full, string text, bool replace, CancellationToken ct)
    {
        // Write beside the target and rename, so Sync never uploads a half-written note.
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".rockbot-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, text, Utf8, ct);
        try
        {
            File.Move(temp, full, replace);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // ---- paths ---------------------------------------------------------------------------------

    private IEnumerable<NoteInfo> EnumerateNotes(string? folder)
    {
        var full = RequireDirectory(FolderPath(folder));
        return Directory.EnumerateFiles(full, "*.md", Recursive)
            .Take(MaxScannedFiles)
            .Select(f => (Full: f, Rel: RelativeOf(f)))
            .Where(f => !f.Rel.Split('/').Any(s => s.StartsWith('.')))
            .Select(f =>
            {
                var info = new FileInfo(f.Full);
                return new NoteInfo(f.Rel, Path.GetFileNameWithoutExtension(f.Rel), ToZone(info.LastWriteTimeUtc), info.Length);
            });
    }

    private async Task<string> ReadTextAsync(string rel, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(Path.Combine(_root, rel), ct);
        }
        catch (IOException)
        {
            return "";   // Removed or locked mid-scan, e.g. by Sync.
        }
    }

    private string FolderPath(string? folder) => string.IsNullOrWhiteSpace(folder) ? "" : RequireRelative(folder, "folder");

    private string RequireDirectory(string rel)
    {
        var full = FullPath(rel);
        if (!Directory.Exists(full))
            throw new McpException($"folder not found: {(rel.Length == 0 ? "/" : rel)}");
        return full;
    }

    private static string NotePath(string path)
    {
        var rel = RequireRelative(path, "path");
        return rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? rel : rel + ".md";
    }

    private static string RequireRelative(string? path, string name)
    {
        if (!IsSafeRelativePath(path))
            throw new McpException($"{name} must be a vault-relative path such as 'Projects/Plan.md', with no '..', leading '/' or hidden folders");
        return NormalisePath(path!);
    }

    private static string NormalisePath(string path) =>
        string.Join('/', path.Trim().Replace('\\', '/').Trim('/').Split('/').Select(s => s.Trim()));

    private static string NormaliseText(string text) => text.Replace("\r\n", "\n");

    /// <summary>The absolute path for a vault-relative one, refusing anything outside the vault or through a symlink.</summary>
    private string FullPath(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(_root, rel));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new McpException("path is outside the vault");

        var current = _root;
        foreach (var segment in Path.GetRelativePath(_root, full).Split(Path.DirectorySeparatorChar))
        {
            if (segment == ".")
                break;
            current = Path.Combine(current, segment);
            if (new FileInfo(current).LinkTarget is not null)
                throw new McpException($"{RelativeOf(current)} is a symbolic link, which the tools don't follow");
        }
        return full;
    }

    private string RelativeOf(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

    private DateTimeOffset ToZone(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), _zone);

    private static string? Snippet(string text, string[] terms)
    {
        var at = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (at < 0)
            return null;
        var start = Math.Max(0, at - SnippetRadius);
        var end = Math.Min(text.Length, at + SnippetRadius);
        var snippet = Whitespace().Replace(text[start..end], " ").Trim();
        return (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : "");
    }

    [GeneratedRegex(@"\A---\n(.*?)\n---[ \t]*(\n|\z)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterPattern();

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
