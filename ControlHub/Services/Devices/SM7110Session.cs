namespace ControlHub.Services.Devices;

// A canceled response does not change the instrument's already confirmed settings.
public sealed class SM7110Session
{
    private string? _appliedSetupSignature;
    public bool NeedsResponseSync { get; private set; }

    public void InvalidateSettings() => _appliedSetupSignature = null;

    public void ResetConnection()
    {
        InvalidateSettings();
        NeedsResponseSync = false;
    }

    public async Task<bool> ApplySettingsIfChangedAsync(
        IReadOnlyList<string> commands,
        Func<string, CancellationToken, Task> send,
        Func<string, CancellationToken, Task<string>> query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signature = string.Join('\n', commands);
        if (string.Equals(_appliedSetupSignature, signature, StringComparison.Ordinal))
            return false;

        InvalidateSettings();
        await SM7110Protocol.ApplySetupCommandsAsync(commands, send, query, cancellationToken);
        _appliedSetupSignature = signature;
        return true;
    }

    public async Task<string> QueryMeasurementAsync(
        string command,
        Func<string, CancellationToken, Task<string>> query,
        CancellationToken cancellationToken)
    {
        try { return await query(command, cancellationToken); }
        catch
        {
            NeedsResponseSync = true;
            throw;
        }
    }

    public const int ResponseSyncAttempts = 3;

    public async Task<string?> SynchronizeResponseAsync(
        Func<CancellationToken, Task<string>> queryIdentity,
        CancellationToken cancellationToken,
        Action<int, TimeoutException>? onRetry = null)
    {
        if (!NeedsResponseSync)
            return null;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The caller uses an identity filter to discard late measurement replies.
                // An interrupted measurement may still be running, so a timeout re-sends the barrier;
                // surplus identity replies are discarded by SM7110Protocol.ResponseFilterFor.
                var identity = await queryIdentity(cancellationToken);
                if (!SM7110Protocol.IsSupportedIdentity(identity))
                    throw new InvalidOperationException($"SM7110/SM7120响应同步失败：{identity}");
                NeedsResponseSync = false;
                return identity;
            }
            catch (TimeoutException exception) when (attempt < ResponseSyncAttempts)
            {
                onRetry?.Invoke(attempt, exception);
            }
        }
    }
}
