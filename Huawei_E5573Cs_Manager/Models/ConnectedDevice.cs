namespace ModemManagerNative.Models;

public sealed record ConnectedDevice(
    string HostName,
    string IpAddress,
    string MacAddress,
    string? AssociatedTime = null);
