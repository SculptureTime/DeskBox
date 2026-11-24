namespace DeskBox.Plugins;

internal sealed class PluginCommandRouter
{
    private readonly IReadOnlyList<PluginKeywordBinding> _bindings;

    public PluginCommandRouter(PluginCatalog catalog)
    {
        _bindings = catalog.KeywordBindings;
    }

    public bool TryRoute(string? input, out PluginRoute route)
    {
        route = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string value = input.Trim();
        foreach (PluginKeywordBinding binding in _bindings)
        {
            string keyword = binding.Keyword;
            if (value.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                route = CreateRoute(binding, string.Empty);
                return true;
            }

            if (value.Length > keyword.Length &&
                value.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
                char.IsWhiteSpace(value[keyword.Length]))
            {
                route = CreateRoute(
                    binding,
                    value[keyword.Length..].TrimStart());
                return true;
            }
        }

        return false;
    }

    private static PluginRoute CreateRoute(
        PluginKeywordBinding binding,
        string queryText) =>
        new(
            binding.Plugin.Id,
            binding.Keyword,
            queryText,
            binding.Plugin.DebounceMs,
            binding.Plugin.TimeoutMs);
}
