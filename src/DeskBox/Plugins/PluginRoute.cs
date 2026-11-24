namespace DeskBox.Plugins;

internal readonly record struct PluginRoute(
    string PluginId,
    string Keyword,
    string QueryText);
