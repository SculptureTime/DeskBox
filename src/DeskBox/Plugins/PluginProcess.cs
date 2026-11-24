using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Plugins;

internal sealed partial class PluginProcess : IAsyncDisposable
{
    private const int MaxProtocolLineBytes = 256 * 1024;
    private const int ReadBufferBytes = 8192;
    private const int MaxStderrLineBytes = 16 * 1024;
    private const int MaxStderrLines = 50;
    private const int ConsecutiveTimeoutResetThreshold = 2;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private static readonly Encoding PipeEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly PluginDescriptor _plugin;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PluginResponse>> _pending = new();
    private readonly object _stderrGate = new();
    private readonly Queue<string> _stderrLines = new();

    private volatile Process? _process;
    private CancellationTokenSource? _processLifetimeCts;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private nint _jobHandle;
    private long _nextRequestId;
    private int _inFlightCount;
    private long _lastActivityTickMs = Environment.TickCount64;
    private int _consecutiveTimeouts;
    private bool _disposed;

    public PluginProcess(PluginDescriptor plugin)
    {
        _plugin = plugin;
    }

    public Task<PluginResponse> QueryAsync(
        string keyword,
        string query,
        int timeoutMs,
        CancellationToken cancellationToken) =>
        SendRequestAsync(
            reqId => new PluginRequest(
                reqId,
                PluginProtocol.QueryMethod,
                Keyword: keyword,
                Query: query),
            timeoutMs,
            cancellationToken);

    public Task<PluginResponse> ExecuteAsync(
        string actionId,
        int timeoutMs,
        CancellationToken cancellationToken) =>
        SendRequestAsync(
            reqId => new PluginRequest(
                reqId,
                PluginProtocol.ExecuteMethod,
                Id: actionId),
            timeoutMs,
            cancellationToken);

    public async Task<bool> TryReclaimIdleAsync(TimeSpan idleTimeout)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return false;
            }

            Process? process = _process;
            if (process is null)
            {
                return false;
            }

            if (HasExited(process))
            {
                ReleaseExitedProcessLocked(process);
                return true;
            }

            if (_inFlightCount != 0 ||
                Environment.TickCount64 - _lastActivityTickMs < idleTimeout.TotalMilliseconds)
            {
                return false;
            }

            App.Log($"[Plugins] Reclaiming idle plugin '{_plugin.Id}'.");
            await StopProcessLockedAsync(
                graceful: true,
                new IOException("Plugin process was reclaimed after being idle."))
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<PluginResponse> SendRequestAsync(
        Func<long, PluginRequest> requestFactory,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        Process process = await BeginRequestAsync().ConfigureAwait(false);
        long reqId = Interlocked.Increment(ref _nextRequestId);
        PluginRequest request = requestFactory(reqId);
        var completion = new TaskCompletionSource<PluginResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(reqId, completion))
        {
            await EndRequestAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Duplicate plugin request id.");
        }

        try
        {
            try
            {
                await WriteRequestAsync(process, request)
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(reqId, out _);
                completion.TrySetException(ex);
                await ResetProcessAsync(process, $"stdin write failed: {ex.Message}")
                    .ConfigureAwait(false);
                throw new IOException($"Plugin '{_plugin.Id}' request write failed.", ex);
            }

            try
            {
                PluginResponse response = await completion.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _consecutiveTimeouts, 0);
                return response;
            }
            catch (TimeoutException)
            {
                _pending.TryRemove(reqId, out _);
                int timeoutCount = Interlocked.Increment(ref _consecutiveTimeouts);
                if (timeoutCount >= ConsecutiveTimeoutResetThreshold)
                {
                    await ResetProcessAsync(
                            process,
                            $"{timeoutCount} consecutive request timeouts")
                        .ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException)
            {
                _pending.TryRemove(reqId, out _);
                throw;
            }
        }
        finally
        {
            await EndRequestAsync().ConfigureAwait(false);
        }
    }

    private async Task<Process> BeginRequestAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Process process = EnsureStartedLocked();
            _inFlightCount++;
            _lastActivityTickMs = Environment.TickCount64;
            return process;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task EndRequestAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_inFlightCount > 0)
            {
                _inFlightCount--;
            }

            _lastActivityTickMs = Environment.TickCount64;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Process EnsureStartedLocked()
    {
        Process? existing = _process;
        if (existing is not null)
        {
            if (!HasExited(existing))
            {
                return existing;
            }

            ReleaseExitedProcessLocked(existing);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _plugin.EntryPath,
            WorkingDirectory = _plugin.PluginDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = PipeEncoding,
            StandardOutputEncoding = PipeEncoding,
            StandardErrorEncoding = PipeEncoding
        };

        var process = new Process
        {
            StartInfo = startInfo
        };

        nint jobHandle = 0;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Plugin '{_plugin.Id}' process did not start.");
            }

            process.StandardInput.NewLine = "\n";
            jobHandle = CreateKillOnCloseJob();
            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"AssignProcessToJobObject failed with Win32 error {error}.");
            }

            var lifetimeCts = new CancellationTokenSource();
            _process = process;
            _jobHandle = jobHandle;
            _processLifetimeCts = lifetimeCts;
            _consecutiveTimeouts = 0;
            _lastActivityTickMs = Environment.TickCount64;

            process.Exited += OnProcessExited;
            process.EnableRaisingEvents = true;

            _stdoutLoop = ReadStdoutLoopAsync(
                process,
                process.StandardOutput.BaseStream,
                lifetimeCts.Token);
            _stderrLoop = DrainStderrLoopAsync(
                process.StandardError.BaseStream,
                lifetimeCts.Token);

            App.Log(
                $"[Plugins] Started '{_plugin.Id}' pid={process.Id} entry='{_plugin.EntryPath}'.");
            return process;
        }
        catch
        {
            try
            {
                if (!HasExited(process))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            if (jobHandle != 0)
            {
                DeskBox.Platform.Kernel32NativeMethods.CloseHandle(jobHandle);
            }

            process.Dispose();
            throw;
        }
    }

    private async Task WriteRequestAsync(Process process, PluginRequest request)
    {
        string json = PluginProtocol.SerializeRequest(request);

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_process, process) || HasExited(process))
            {
                throw new IOException($"Plugin '{_plugin.Id}' process is not running.");
            }

            await process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(
        Process process,
        Stream stdout,
        CancellationToken lifetimeToken)
    {
        byte[] readBuffer = new byte[ReadBufferBytes];
        byte[] lineBuffer = new byte[MaxProtocolLineBytes];
        int lineLength = 0;

        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                int read = await stdout
                    .ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), lifetimeToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    if (!lifetimeToken.IsCancellationRequested)
                    {
                        await ResetProcessAsync(process, "stdout closed unexpectedly")
                            .ConfigureAwait(false);
                    }

                    return;
                }

                for (int i = 0; i < read; i++)
                {
                    byte value = readBuffer[i];
                    if (value == (byte)'\n')
                    {
                        int payloadLength = lineLength;
                        if (payloadLength > 0 && lineBuffer[payloadLength - 1] == (byte)'\r')
                        {
                            payloadLength--;
                        }

                        if (payloadLength > 0)
                        {
                            if (!TryDispatchProtocolLine(lineBuffer.AsSpan(0, payloadLength), out string? error))
                            {
                                await ResetProcessAsync(process, error ?? "invalid stdout frame")
                                    .ConfigureAwait(false);
                                return;
                            }
                        }

                        lineLength = 0;
                        continue;
                    }

                    if (lineLength == MaxProtocolLineBytes)
                    {
                        await ResetProcessAsync(
                                process,
                                $"stdout line exceeded {MaxProtocolLineBytes} bytes")
                            .ConfigureAwait(false);
                        return;
                    }

                    lineBuffer[lineLength++] = value;
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!lifetimeToken.IsCancellationRequested)
            {
                await ResetProcessAsync(process, $"stdout reader failed: {ex.Message}")
                    .ConfigureAwait(false);
            }
        }
    }

    private bool TryDispatchProtocolLine(ReadOnlySpan<byte> utf8Line, out string? error)
    {
        error = null;
        try
        {
            PluginResponse? response = PluginProtocol.DeserializeResponse(utf8Line);
            if (response is null || response.ReqId <= 0)
            {
                error = "stdout frame is missing a valid req_id";
                return false;
            }

            if (_pending.TryRemove(
                    response.ReqId,
                    out TaskCompletionSource<PluginResponse>? completion))
            {
                completion.TrySetResult(response);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"invalid stdout JSON: {ex.Message}";
            return false;
        }
    }

    private async Task DrainStderrLoopAsync(
        Stream stderr,
        CancellationToken lifetimeToken)
    {
        byte[] readBuffer = new byte[ReadBufferBytes];
        byte[] lineBuffer = new byte[MaxStderrLineBytes];
        int lineLength = 0;
        bool truncated = false;

        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                int read = await stderr
                    .ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), lifetimeToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    byte value = readBuffer[i];
                    if (value == (byte)'\n')
                    {
                        AddStderrLine(lineBuffer, lineLength, truncated);
                        lineLength = 0;
                        truncated = false;
                        continue;
                    }

                    if (!truncated)
                    {
                        if (lineLength < MaxStderrLineBytes)
                        {
                            lineBuffer[lineLength++] = value;
                        }
                        else
                        {
                            truncated = true;
                        }
                    }
                }
            }

            if (lineLength > 0 || truncated)
            {
                AddStderrLine(lineBuffer, lineLength, truncated);
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[Plugins] stderr reader for '{_plugin.Id}' failed: {ex.Message}");
        }
    }

    private void AddStderrLine(byte[] lineBuffer, int lineLength, bool truncated)
    {
        int payloadLength = lineLength;
        if (payloadLength > 0 && lineBuffer[payloadLength - 1] == (byte)'\r')
        {
            payloadLength--;
        }

        string line = PipeEncoding.GetString(lineBuffer, 0, payloadLength);
        if (truncated)
        {
            line += " …[truncated]";
        }

        lock (_stderrGate)
        {
            if (_stderrLines.Count == MaxStderrLines)
            {
                _stderrLines.Dequeue();
            }

            _stderrLines.Enqueue(line);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process || !ReferenceEquals(_process, process))
        {
            return;
        }

        string stderrTail = GetStderrTail();
        App.Log(
            $"[Plugins] Plugin '{_plugin.Id}' exited with code {SafeExitCode(process)}" +
            (stderrTail.Length == 0 ? "." : $". stderr: {stderrTail}"));
        FailAllPending(new IOException($"Plugin '{_plugin.Id}' exited unexpectedly."));
        _ = ReleaseExitedProcessAsync(process);
    }

    private async Task ReleaseExitedProcessAsync(Process expectedProcess)
    {
        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_process, expectedProcess) && HasExited(expectedProcess))
                {
                    ReleaseExitedProcessLocked(expectedProcess);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Plugins] Failed to release exited plugin '{_plugin.Id}': {ex.Message}");
        }
    }

    private async Task ResetProcessAsync(Process expectedProcess, string reason)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_process, expectedProcess))
            {
                return;
            }

            App.Log($"[Plugins] Resetting '{_plugin.Id}': {reason}");
            await StopProcessLockedAsync(
                    graceful: false,
                    new IOException($"Plugin '{_plugin.Id}' protocol/runtime failure: {reason}"))
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopProcessLockedAsync(bool graceful, Exception pendingError)
    {
        Process? process = _process;
        _process = null;

        CancellationTokenSource? lifetimeCts = _processLifetimeCts;
        _processLifetimeCts = null;
        nint jobHandle = _jobHandle;
        _jobHandle = 0;

        FailAllPending(pendingError);

        if (process is null)
        {
            lifetimeCts?.Cancel();
            lifetimeCts?.Dispose();
            if (jobHandle != 0)
            {
                DeskBox.Platform.Kernel32NativeMethods.CloseHandle(jobHandle);
            }
            return;
        }

        try
        {
            process.Exited -= OnProcessExited;

            if (graceful && !HasExited(process))
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                }

                using var graceCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try
                {
                    await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Plugins] Failed to stop '{_plugin.Id}': {ex.Message}");
        }
        finally
        {
            lifetimeCts?.Cancel();
            lifetimeCts?.Dispose();

            if (jobHandle != 0)
            {
                DeskBox.Platform.Kernel32NativeMethods.CloseHandle(jobHandle);
            }

            process.Dispose();
            _stdoutLoop = null;
            _stderrLoop = null;
        }
    }

    private void ReleaseExitedProcessLocked(Process process)
    {
        if (!ReferenceEquals(_process, process))
        {
            return;
        }

        _process = null;
        _processLifetimeCts?.Cancel();
        _processLifetimeCts?.Dispose();
        _processLifetimeCts = null;

        if (_jobHandle != 0)
        {
            DeskBox.Platform.Kernel32NativeMethods.CloseHandle(_jobHandle);
            _jobHandle = 0;
        }

        process.Exited -= OnProcessExited;
        process.Dispose();
        _stdoutLoop = null;
        _stderrLoop = null;
    }

    private void FailAllPending(Exception error)
    {
        foreach ((long reqId, TaskCompletionSource<PluginResponse> completion) in _pending)
        {
            if (_pending.TryRemove(reqId, out TaskCompletionSource<PluginResponse>? removed))
            {
                removed.TrySetException(error);
            }
        }
    }

    private string GetStderrTail()
    {
        lock (_stderrGate)
        {
            return string.Join(" | ", _stderrLines.TakeLast(3));
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static nint CreateKillOnCloseJob()
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"CreateJobObjectW failed with Win32 error {error}.");
        }

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastWin32Error();
            DeskBox.Platform.Kernel32NativeMethods.CloseHandle(job);
            throw new InvalidOperationException(
                $"SetInformationJobObject failed with Win32 error {error}.");
        }

        return job;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopProcessLockedAsync(
                    graceful: true,
                    new ObjectDisposedException(nameof(PluginProcess)))
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint job,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation jobObjectInformation,
        uint jobObjectInformationLength);

    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
