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

    public async Task<string?> SynchronizeResponseAsync(
        Func<CancellationToken, Task<string>> queryIdentity,
        CancellationToken cancellationToken)
    {
        if (!NeedsResponseSync)
            return null;

        // The caller uses an identity filter to discard late measurement replies.
        var identity = await queryIdentity(cancellationToken);
        if (!SM7110Protocol.IsSupportedIdentity(identity))
            throw new InvalidOperationException($"SM7110/SM7120响应同步失败：{identity}");
        NeedsResponseSync = false;
        return identity;
    }
}
