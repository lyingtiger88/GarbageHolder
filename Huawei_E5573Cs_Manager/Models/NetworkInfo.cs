namespace ModemManagerNative.Models;

public sealed record NetworkInfo(string? Gateway, string? LocalIPv4, string? Ssid);
public sealed record PingResult(bool Success, long? RoundtripTimeMs, string? Error);
