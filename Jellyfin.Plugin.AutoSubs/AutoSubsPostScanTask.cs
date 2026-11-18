using System.Globalization;
using Jellyfin.Plugin.AutoSubs.Core;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubs;

/// <summary>Executes once after every Jellyfin library scan.</summary>
public sealed class AutoSubsPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AutoSubsPostScanTask> _logger;

    public AutoSubsPostScanTask(ILibraryManager libraryManager, ILogger<AutoSubsPostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance?.Configuration;
        if (settings is null || !settings.Enabled)
        {
            progress.Report(100);
            return Task.CompletedTask;
        }

        var organizer = new SubtitleOrganizer();
        var roots = _libraryManager.GetVirtualFolders()
            .Where(v => string.Equals(Convert.ToString(v.CollectionType, CultureInfo.InvariantCulture), "movies", StringComparison.OrdinalIgnoreCase))
            .SelectMany(v => v.Locations ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var totalMovies = 0;
        var links = 0;
        var ambiguous = 0;
        _logger.LogInformation("AutoSubs: starting, movie libraries={LibraryCount}, dry-run={DryRun}", roots.Length, settings.DryRun);

        for (int index = 0; index < roots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[index];
            try
            {
                var result = organizer.Scan(root, settings.DryRun, Math.Clamp(settings.MaxDepth, 1, 30), cancellationToken, message => _logger.LogInformation("AutoSubs: {Message}", message));
                totalMovies += result.Movies;
                links += result.Links;
                ambiguous += result.AmbiguousFolders;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AutoSubs: failed to scan library root {Root}", root);
            }

            progress.Report(100.0 * (index + 1) / Math.Max(1, roots.Length));
        }

        _logger.LogInformation("AutoSubs: complete; movies={Movies}, links={Links}, ambiguousFolders={AmbiguousFolders}", totalMovies, links, ambiguous);
        progress.Report(100);
        return Task.CompletedTask;
    }
}
