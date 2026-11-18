using System.Globalization;

namespace Jellyfin.Plugin.AutoSubs.Core;

public sealed record ScanResult(int Movies, int Links, int AmbiguousFolders);

/// <summary>
/// Pure filesystem engine; does not depend on Jellyfin internals. It NEVER deletes,
/// overwrites, moves, or copies a media/subtitle file.
/// </summary>
public sealed class SubtitleOrganizer
{
    private static readonly HashSet<string> MovieExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts" };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".srt", ".ass", ".ssa", ".vtt", ".idx", ".sub", ".sup", ".smi", ".sami", ".ttml" };

    private sealed record SubtitleGroup(string Stem, string[] Files);

    public ScanResult Scan(string root, bool dryRun, int maxDepth, CancellationToken cancellationToken, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!Directory.Exists(root))
        {
            log($"Root inaccessible: {root}");
            return new ScanResult(0, 0, 0);
        }

        var movies = 0;
        var links = 0;
        var ambiguous = 0;
        var directories = new Stack<(string Path, int Depth)>();
        directories.Push((Path.GetFullPath(root), 0));

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = directories.Pop();
            if (IsLink(directory)) continue;

            try
            {
                var videos = Directory.EnumerateFiles(directory)
                    .Where(f => MovieExtensions.Contains(Path.GetExtension(f)) && !IsLink(f))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray();

                if (videos.Length == 1)
                {
                    movies++;
                    links += ProcessMovie(directory, videos[0], dryRun, maxDepth - depth, cancellationToken, log);
                }
                else if (videos.Length > 1)
                {
                    ambiguous++;
                    log($"SKIP ambiguous: {videos.Length} video files in {directory}");
                }

                if (depth < maxDepth)
                {
                    foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(x => x, StringComparer.Ordinal))
                    {
                        if (!IsLink(child)) directories.Push((child, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                log($"SKIP inaccessible directory {directory}: {ex.Message}");
            }
        }

        return new ScanResult(movies, links, ambiguous);
    }

    private static int ProcessMovie(
        string folder, string video, bool dryRun, int allowedDepth,
        CancellationToken cancellationToken, Action<string> log)
    {
        var baseName = Path.GetFileNameWithoutExtension(video);
        var candidates = new List<string>();
        var directories = new Stack<(string Path, int Depth)>();
        directories.Push((folder, 0));

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = directories.Pop();
            try
            {
                // Never take subtitles from another video-containing subfolder.
                if (depth > 0 && Directory.EnumerateFiles(directory)
                    .Any(f => MovieExtensions.Contains(Path.GetExtension(f)))) continue;

                candidates.AddRange(Directory.EnumerateFiles(directory)
                    .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f)) && !IsLink(f)));

                if (depth < allowedDepth)
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                        if (!IsLink(child)) directories.Push((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                log($"SKIP subtitle directory {directory}: {ex.Message}");
            }
        }

        var groups = MakeGroups(candidates, log);
        var linkedSources = FindAlreadyLinkedSources(folder);
        var changes = 0;
        var index = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A subtitle already named after the video in the same folder is already visible.
            if (group.Files.All(f => Path.GetDirectoryName(f) == folder &&
                IsConventionalSubtitleName(Path.GetFileNameWithoutExtension(f), baseName))) continue;

            // All components of a VobSub pair must be linked together.
            if (group.Files.All(linkedSources.Contains)) continue;
            if (group.Files.Any(linkedSources.Contains))
            {
                log($"SKIP partially linked subtitle group (manual review recommended): {group.Stem}");
                continue;
            }

            var language = InferLanguage(Path.GetFileName(group.Stem));
            string targetStem;
            do
            {
                index++;
                targetStem = Path.Combine(folder, $"{baseName}.{language}.autosubs{index:00}");
            }
            while (group.Files.Any(f => ExistsIncludingDanglingLink(targetStem + Path.GetExtension(f).ToLowerInvariant())));

            if (dryRun)
            {
                log($"DRY RUN: {string.Join(", ", group.Files.Select(Path.GetFileName))} -> {Path.GetFileName(targetStem)}");
                changes += group.Files.Length;
                continue;
            }

            var created = new List<string>();
            try
            {
                foreach (var source in group.Files)
                {
                    var target = targetStem + Path.GetExtension(source).ToLowerInvariant();
                    // Relative links work in Docker if both paths are under the same mount.
                    var relative = Path.GetRelativePath(folder, source);
                    File.CreateSymbolicLink(target, relative);
                    created.Add(target);
                    linkedSources.Add(source);
                    changes++;
                }
                log($"LINKED {string.Join(", ", created.Select(Path.GetFileName))} from {group.Stem}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Roll back only links this invocation created, never original files.
                foreach (var createdPath in created)
                {
                    try
                    {
                        if (new FileInfo(createdPath).LinkTarget is not null) File.Delete(createdPath);
                        changes--;
                    }
                    catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                    {
                        log($"WARNING could not rollback new symlink {createdPath}: {rollbackException.Message}");
                    }
                }
                log($"ERROR creating links for {group.Stem}: {ex.Message}");
            }
        }

        return changes;
    }

    private static bool IsConventionalSubtitleName(string stem, string videoStem)
    {
        return string.Equals(stem, videoStem, StringComparison.OrdinalIgnoreCase) ||
               stem.StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SubtitleGroup> MakeGroups(List<string> files, Action<string> log)
    {
        var grouped = files
            .GroupBy(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();
        var result = new List<SubtitleGroup>();

        foreach (var group in grouped)
        {
            var items = group.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var idx = items.FirstOrDefault(x => Path.GetExtension(x).Equals(".idx", StringComparison.OrdinalIgnoreCase));
            var sub = items.FirstOrDefault(x => Path.GetExtension(x).Equals(".sub", StringComparison.OrdinalIgnoreCase));

            if (idx is not null)
            {
                if (sub is null)
                {
                    log($"SKIP orphan VobSub index (no matching .sub): {idx}");
                }
                else
                {
                    result.Add(new SubtitleGroup(group.Key, [idx, sub]));
                }
            }
            else if (sub is not null)
            {
                // .sub without .idx may be MicroDVD text; do not assume VobSub.
                result.Add(new SubtitleGroup(group.Key, [sub]));
            }

            foreach (var item in items)
            {
                if (Path.GetExtension(item).Equals(".idx", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(item).Equals(".sub", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new SubtitleGroup(Path.Combine(Path.GetDirectoryName(item)!, Path.GetFileNameWithoutExtension(item)), [item]));
            }
        }

        return result;
    }

    private static HashSet<string> FindAlreadyLinkedSources(string movieFolder)
    {
        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(movieFolder))
        {
            if (!Path.GetFileName(file).Contains(".autosubs", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var target = new FileInfo(file).LinkTarget;
                if (target is not null)
                    linked.Add(Path.GetFullPath(Path.Combine(movieFolder, target)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Treat malformed or inaccessible links as foreign files.
            }
        }
        return linked;
    }

    private static bool ExistsIncludingDanglingLink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        try { return new FileInfo(path).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string InferLanguage(string name)
    {
        var words = name.ToLowerInvariant().Split(
            ['.', '_', '-', ' ', '[', ']', '(', ')', '+'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in words)
        {
            switch (token)
            {
                case "es": case "spa": case "spanish": case "espanol": case "español": case "castellano": return "es";
                case "en": case "eng": case "english": return "en";
                case "fr": case "fre": case "fra": case "french": return "fr";
                case "de": case "ger": case "deu": case "german": return "de";
                case "it": case "ita": case "italian": return "it";
                case "pt": case "por": case "portuguese": return "pt";
                case "ja": case "jpn": case "japanese": return "ja";
            }
        }
        return "und";
    }
}
