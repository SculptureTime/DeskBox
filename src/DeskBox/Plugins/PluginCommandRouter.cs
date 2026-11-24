namespace DeskBox.Plugins;

internal sealed class PluginCommandRouter
{
    public bool TryRoute(string? input, out PluginRoute route)
    {
        route = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string value = input.Trim();
        const string keyword = "ts";

        if (value.Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            route = new PluginRoute(
                PluginId: "test.timestamp",
                Keyword: keyword,
                QueryText: string.Empty);
            return true;
        }

        if (value.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase))
        {
            route = new PluginRoute(
                PluginId: "test.timestamp",
                Keyword: keyword,
                QueryText: value[(keyword.Length + 1)..].TrimStart());
            return true;
        }

        return false;
    }
}
