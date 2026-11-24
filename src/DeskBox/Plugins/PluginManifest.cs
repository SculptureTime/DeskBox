using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DeskBox.Plugins;

internal sealed class PluginManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public int ProtocolVersion { get; init; }
    public string Entry { get; init; } = string.Empty;
    public List<string>? Keywords { get; init; } = [];
    public int DebounceMs { get; init; }
    public int TimeoutMs { get; init; } = 3000;
}

internal sealed record PluginDescriptor(
    string Id,
    string Name,
    string Version,
    string PluginDirectory,
    string EntryPath,
    IReadOnlyList<string> Keywords,
    int DebounceMs,
    int TimeoutMs);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(PluginManifest))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;
