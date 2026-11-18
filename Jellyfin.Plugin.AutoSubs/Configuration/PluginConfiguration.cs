using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoSubs.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool DryRun { get; set; } = true;
    public int MaxDepth { get; set; } = 12;
}
