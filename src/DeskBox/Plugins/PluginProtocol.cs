using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Plugins;

internal sealed record PluginRequest(
    long ReqId,
    string Method,
    string? Keyword = null,
    string? Query = null,
    string? Id = null);

internal sealed record PluginResponse(
    long ReqId,
    List<PluginResult>? Results = null,
    bool? Ok = null,
    string? Error = null);

internal sealed record PluginResult(
    string Id,
    string Title,
    string? Subtitle = null,
    string? Icon = null,
    PluginAction? Action = null);

internal sealed record PluginAction(
    string Type,
    string? Value = null,
    string? Id = null,
    bool KeepOpen = false);

internal static class PluginProtocol
{
    public const string QueryMethod = "query";
    public const string ExecuteMethod = "execute";

    public static string SerializeRequest(PluginRequest request) =>
        JsonSerializer.Serialize(
            request,
            PluginProtocolJsonContext.Default.PluginRequest);

    public static PluginResponse? DeserializeResponse(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(
            utf8Json,
            PluginProtocolJsonContext.Default.PluginResponse);
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PluginRequest))]
[JsonSerializable(typeof(PluginResponse))]
[JsonSerializable(typeof(PluginResult))]
[JsonSerializable(typeof(PluginAction))]
[JsonSerializable(typeof(List<PluginResult>))]
internal sealed partial class PluginProtocolJsonContext : JsonSerializerContext;
