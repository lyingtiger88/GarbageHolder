namespace ModemManagerNative.Models;

public sealed record MacFilterDevice(
    string HostName,
    string MacAddress,
    string SsidIndex = "0");
