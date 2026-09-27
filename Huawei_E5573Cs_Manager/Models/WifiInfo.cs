namespace ModemManagerNative.Models;

public sealed record WifiInfo(
    string? Ssid,
    bool? Enabled,
    bool? Hidden,
    string? Channel,
    string? Mode,
    int? MaxClients,
    bool? ClientIsolation);
