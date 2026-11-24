using System.Collections.Generic;
using System.Text.Json;

namespace DeskBox.Plugins;

internal readonly record struct PluginKeywordBinding(
    string Keyword,
    PluginDescriptor Plugin);

internal sealed class PluginCatalog
{
    private static readonly HashSet<string> ReservedWords = new(
        [
            "apps", "app", "应用",
            "files", "file", "文件",
            "folders", "folder", "dir", "文件夹", "目录",
            "images", "image", "img", "pic", "图片",
            "documents", "document", "doc", "文档",
            "videos", "video", "视频",
            "music", "audio", "音乐", "音频",
            "archives", "archive", "zip", "压缩包",
            "todos", "todo", "td", "task", "待办",
            "notes", "note", "jot", "随记", "速记", "记",
            "clipboard", "clip", "cb", "剪贴板",
            "widgets", "widget", "小组件",
            "settings", "setting", "set", "设置",
            "cmd", "命令"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PluginDescriptor> _pluginsById;
    private readonly PluginKeywordBinding[] _keywordBindings;

    private PluginCatalog(
        Dictionary<string, PluginDescriptor> pluginsById,
        PluginKeywordBinding[] keywordBindings)
    {
        _pluginsById = pluginsById;
        _keywordBindings = keywordBindings;
    }

    public static string DefaultRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "Plugins");

    public IReadOnlyList<PluginKeywordBinding> KeywordBindings => _keywordBindings;

    public bool TryGetPlugin(string pluginId, out PluginDescriptor plugin) =>
        _pluginsById.TryGetValue(pluginId, out plugin!);

    public static PluginCatalog LoadDefault() => Load(DefaultRootDirectory);

    internal static PluginCatalog Load(string rootDirectory)
    {
        var plugins = new Dictionary<string, PluginDescriptor>(StringComparer.OrdinalIgnoreCase);
        var keywordOwners = new Dictionary<string, PluginDescriptor>(StringComparer.OrdinalIgnoreCase);
        var bindings = new List<PluginKeywordBinding>();

        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return new PluginCatalog(plugins, []);
        }

        IEnumerable<string> pluginDirectories;
        try
        {
            pluginDirectories = Directory
                .EnumerateDirectories(rootDirectory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            App.Log($"[Plugins] Failed to enumerate '{rootDirectory}': {ex.Message}");
            return new PluginCatalog(plugins, []);
        }

        foreach (string directory in pluginDirectories)
        {
            string manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(manifestPath);
                PluginManifest? manifest = JsonSerializer.Deserialize(
                    json,
                    PluginManifestJsonContext.Default.PluginManifest);
                if (manifest is null)
                {
                    App.Log($"[Plugins] Skipping '{directory}': plugin.json is empty.");
                    continue;
                }

                if (!TryBuildDescriptor(directory, manifest, out PluginDescriptor? candidate))
                {
                    continue;
                }

                if (plugins.ContainsKey(candidate.Id))
                {
                    App.Log(
                        $"[Plugins] Duplicate plugin id '{candidate.Id}' in '{directory}'; " +
                        "first loaded plugin wins.");
                    continue;
                }

                var acceptedKeywords = new List<string>();
                foreach (string rawKeyword in candidate.Keywords)
                {
                    string keyword = rawKeyword.Trim();
                    if (!IsAllowedKeyword(keyword))
                    {
                        App.Log(
                            $"[Plugins] Ignoring reserved/invalid keyword '{rawKeyword}' " +
                            $"from plugin '{candidate.Id}'.");
                        continue;
                    }

                    if (keywordOwners.TryGetValue(keyword, out PluginDescriptor? owner))
                    {
                        App.Log(
                            $"[Plugins] Keyword '{keyword}' from '{candidate.Id}' conflicts with " +
                            $"'{owner.Id}'; first loaded plugin wins.");
                        continue;
                    }

                    acceptedKeywords.Add(keyword);
                }

                if (acceptedKeywords.Count == 0)
                {
                    App.Log($"[Plugins] Skipping '{candidate.Id}': no usable keywords remain.");
                    continue;
                }

                PluginDescriptor descriptor = candidate with { Keywords = acceptedKeywords };
                plugins.Add(descriptor.Id, descriptor);
                foreach (string keyword in acceptedKeywords)
                {
                    keywordOwners.Add(keyword, descriptor);
                    bindings.Add(new PluginKeywordBinding(keyword, descriptor));
                }
            }
            catch (Exception ex)
            {
                App.Log($"[Plugins] Skipping malformed plugin '{manifestPath}': {ex.Message}");
            }
        }

        PluginKeywordBinding[] orderedBindings = bindings
            .OrderByDescending(binding => binding.Keyword.Length)
            .ThenBy(binding => binding.Keyword, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PluginCatalog(plugins, orderedBindings);
    }

    private static bool TryBuildDescriptor(
        string pluginDirectory,
        PluginManifest manifest,
        out PluginDescriptor? descriptor)
    {
        descriptor = null;

        if (manifest.ProtocolVersion != 1)
        {
            App.Log(
                $"[Plugins] Skipping '{pluginDirectory}': unsupported protocol_version " +
                $"'{manifest.ProtocolVersion}'.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.Name) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.Entry) ||
            manifest.Keywords is null ||
            manifest.Keywords.Count == 0)
        {
            App.Log($"[Plugins] Skipping '{pluginDirectory}': required manifest field is missing.");
            return false;
        }

        string canonicalDirectory = Path.GetFullPath(pluginDirectory);
        string directoryPrefix =
            canonicalDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string entryPath = Path.GetFullPath(Path.Combine(canonicalDirectory, manifest.Entry));

        if (!entryPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(entryPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(entryPath))
        {
            App.Log(
                $"[Plugins] Skipping '{manifest.Id}': entry must be an existing .exe " +
                "strictly inside the plugin directory.");
            return false;
        }

        var normalizedKeywords = manifest.Keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        descriptor = new PluginDescriptor(
            manifest.Id.Trim(),
            manifest.Name.Trim(),
            manifest.Version.Trim(),
            canonicalDirectory,
            entryPath,
            normalizedKeywords,
            Math.Clamp(manifest.DebounceMs, 0, 2000),
            Math.Clamp(manifest.TimeoutMs, 500, 15000));
        return true;
    }

    private static bool IsAllowedKeyword(string keyword)
    {
        if (keyword.Length < 2 || ReservedWords.Contains(keyword))
        {
            return false;
        }

        if (keyword.IndexOfAny([':', '：']) >= 0)
        {
            return false;
        }

        return keyword[0] is not ('>' or '＞' or '?' or '+' or ';');
    }
}
