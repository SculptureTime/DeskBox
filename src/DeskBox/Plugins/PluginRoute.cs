namespace DeskBox.Plugins;

internal readonly record struct PluginRoute(
    string PluginId,
    string Keyword,
    string QueryText,
    int DebounceMs,
    int TimeoutMs);
