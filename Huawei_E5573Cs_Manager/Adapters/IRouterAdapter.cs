namespace ModemManagerNative.Adapters;

public interface IRouterAdapter
{
    string Name { get; }
    Task<bool> CanHandleAsync(CancellationToken cancellationToken = default);
    Task<RouterStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record RouterStatus(
    string? Model,
    string? Firmware,
    string? WanIp,
    string? ConnectionState,
    int? ConnectedClients,
    int? SignalPercent,
    int? BatteryPercent = null,
    string? NetworkType = null,
    string? Rsrp = null,
    string? Rsrq = null,
    string? Sinr = null);
