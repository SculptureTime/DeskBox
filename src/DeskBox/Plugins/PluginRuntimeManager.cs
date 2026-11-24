using System.Collections.Concurrent;

namespace DeskBox.Plugins;

internal sealed record PluginBoundAction(
    long BatchId,
    string PluginId,
    string Type,
    string? Value,
    string? ExecuteId,
    bool KeepOpen);

internal sealed class PluginRuntimeManager : IAsyncDisposable
{
    public const string ActionHandlePrefix = "plugin-action:";

    private static readonly object SharedGate = new();
    private static PluginRuntimeManager? s_shared;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan IdleScanInterval = TimeSpan.FromMinutes(1);

    private readonly PluginCatalog _catalog;
    private readonly ConcurrentDictionary<string, PluginProcess> _processes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginBoundAction> _actions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginBoundAction> _inFlightActions =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _idleLoop;
    private long _nextActionToken;
    private int _disposed;

    private PluginRuntimeManager(PluginCatalog catalog)
    {
        _catalog = catalog;
        Router = new PluginCommandRouter(catalog);
        _idleLoop = RunIdleLoopAsync(_lifetimeCts.Token);
    }

    public static PluginRuntimeManager Shared
    {
        get
        {
            lock (SharedGate)
            {
                return s_shared ??= new PluginRuntimeManager(PluginCatalog.LoadDefault());
            }
        }
    }

    public PluginCommandRouter Router { get; }

    public static async Task DisposeSharedAsync()
    {
        PluginRuntimeManager? runtime;
        lock (SharedGate)
        {
            runtime = s_shared;
            s_shared = null;
        }

        if (runtime is not null)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<PluginResponse> QueryAsync(
        PluginRoute route,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_catalog.TryGetPlugin(route.PluginId, out PluginDescriptor plugin))
        {
            throw new InvalidOperationException(
                $"Plugin '{route.PluginId}' is no longer available.");
        }

        PluginProcess process = _processes.GetOrAdd(
            plugin.Id,
            _ => new PluginProcess(plugin));
        return await process.QueryAsync(
                route.Keyword,
                route.QueryText,
                route.TimeoutMs,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ExecuteAsync(
        string pluginId,
        string executeId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_catalog.TryGetPlugin(pluginId, out PluginDescriptor plugin))
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' is no longer available.");
        }

        PluginProcess process = _processes.GetOrAdd(
            plugin.Id,
            _ => new PluginProcess(plugin));
        PluginResponse response = await process.ExecuteAsync(
                executeId,
                plugin.TimeoutMs,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.Ok != true || !string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.Error)
                    ? $"Plugin '{pluginId}' execute request failed."
                    : response.Error);
        }
    }

    public string? RegisterAction(
        long batchId,
        string pluginId,
        string resultId,
        PluginAction? action)
    {
        if (batchId <= 0 || action is null || Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        string type = action.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        string? value = action.Value;
        string? executeId = null;

        switch (type)
        {
            case "copy":
            case "open_url":
            case "open_path":
                if (value is null)
                {
                    return null;
                }
                break;

            case "plugin":
                executeId = !string.IsNullOrWhiteSpace(action.Id)
                    ? action.Id.Trim()
                    : !string.IsNullOrWhiteSpace(action.Value)
                        ? action.Value.Trim()
                        : resultId;
                if (string.IsNullOrWhiteSpace(executeId))
                {
                    return null;
                }
                break;

            default:
                return null;
        }

        string token = Interlocked
            .Increment(ref _nextActionToken)
            .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        _actions[token] = new PluginBoundAction(
            batchId,
            pluginId,
            type,
            value,
            executeId,
            action.KeepOpen);
        return ActionHandlePrefix + token;
    }

    public bool TryStageAction(
        string? actionId,
        out PluginBoundAction action,
        out string stagedActionId)
    {
        action = null!;
        stagedActionId = string.Empty;
        if (!TryGetActionToken(actionId, out string sourceToken) ||
            !_actions.TryGetValue(sourceToken, out PluginBoundAction? captured))
        {
            return false;
        }

        string stagedToken = Interlocked
            .Increment(ref _nextActionToken)
            .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        _inFlightActions[stagedToken] = captured;
        action = captured;
        stagedActionId = ActionHandlePrefix + stagedToken;
        return true;
    }

    public bool TryTakeStagedAction(string? actionId, out PluginBoundAction action)
    {
        action = null!;
        if (!TryGetActionToken(actionId, out string token))
        {
            return false;
        }

        return _inFlightActions.TryRemove(token, out action!);
    }

    public static bool IsActionHandle(string? actionId) =>
        !string.IsNullOrWhiteSpace(actionId) &&
        actionId.StartsWith(ActionHandlePrefix, StringComparison.Ordinal);

    public void ClearActionBatch(long batchId)
    {
        if (batchId <= 0)
        {
            return;
        }

        foreach ((string token, PluginBoundAction action) in _actions)
        {
            if (action.BatchId == batchId)
            {
                _actions.TryRemove(token, out _);
            }
        }
    }

    private static bool TryGetActionToken(string? actionId, out string token)
    {
        token = string.Empty;
        if (!IsActionHandle(actionId))
        {
            return false;
        }

        token = actionId![ActionHandlePrefix.Length..];
        return token.Length > 0;
    }

    private async Task RunIdleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(IdleScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (PluginProcess process in _processes.Values)
                {
                    try
                    {
                        await process.TryReclaimIdleAsync(IdleTimeout).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[Plugins] Idle reclaim failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _actions.Clear();
        _inFlightActions.Clear();
        _lifetimeCts.Cancel();
        try
        {
            await _idleLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        PluginProcess[] processes = _processes.Values.ToArray();
        _processes.Clear();
        await Task.WhenAll(processes.Select(
            static process => process.DisposeAsync().AsTask())).ConfigureAwait(false);

        _lifetimeCts.Dispose();
    }
}
