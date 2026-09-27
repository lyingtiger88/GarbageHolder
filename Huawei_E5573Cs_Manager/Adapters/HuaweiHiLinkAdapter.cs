using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ModemManagerNative.Models;

namespace ModemManagerNative.Adapters;

/// <summary>
/// Huawei HiLink adapter aimed at E5573/E5573Cs family devices.
/// Uses the modem's local HTTP/XML API only; no cloud service is involved.
/// </summary>
public sealed class HuaweiHiLinkAdapter : IRouterAdapter, IDisposable
{
    private const string TokenHeader = "__RequestVerificationToken";
    private readonly Uri _baseUri;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<string> _postTokens = new();
    private string? _token;
    private bool _initialized;
    private bool _loggedIn;
    private readonly SemaphoreSlim _authGate = new(1, 1);

    public string Name => "Huawei HiLink / E5573Cs";
    public bool IsLoggedIn => _loggedIn;

    public HuaweiHiLinkAdapter(Uri baseUri)
    {
        _baseUri = NormalizeBaseUri(baseUri);
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(8)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
    }

    public async Task<bool> CanHandleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureSessionAsync(cancellationToken);
            var info = await GetXmlAsync("api/device/information", cancellationToken, throwOnApiError: false);
            if (info.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
                return _initialized;

            var model = Value(info, "DeviceName", "devicename", "ModelName", "modelname");
            return !string.IsNullOrWhiteSpace(model) ||
                   info.Name.LocalName.Equals("response", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await EnsureSessionAsync(cancellationToken, force: true);

    public async Task<LoginStateInfo> GetLoginStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync(cancellationToken);
        var xml = await GetXmlAsync("api/user/state-login", cancellationToken, throwOnApiError: false);

        if (xml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
            return new LoginStateInfo(false, false, 0, "4", null, null);

        var stateValue = Value(xml, "State", "state");
        var loggedIn = stateValue == "0";
        var remain = ParseInt(Value(xml, "remainwaittime", "RemainWaitTime")) ?? 0;
        var lockStatus = Value(xml, "lockstatus", "LockStatus");
        var locked = remain > 0 || lockStatus == "1";
        var passwordType = Value(xml, "password_type", "PasswordType") ?? "4";
        var username = Value(xml, "Username", "username");

        _loggedIn = loggedIn;
        return new LoginStateInfo(loggedIn, locked, Math.Max(0, remain), passwordType, username, stateValue);
    }

    public async Task<bool> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            // Reuse the current HiLink session. Refresh buttons never submit credentials,
            // and a new session is created only when the adapter itself is initialized.
            await EnsureSessionAsync(cancellationToken);

            var state = await GetLoginStateAsync(cancellationToken);
            if (state.IsLoggedIn)
                return true;

            if (state.IsLocked)
                throw new HuaweiHiLinkException(
                    state.RemainingWaitSeconds > 0
                        ? $"Login is temporarily locked by the modem. Wait about {state.RemainingWaitSeconds} second(s) before trying again."
                        : "Login is temporarily locked by the modem. Wait for the router lockout to expire before trying again.",
                    "108007");

            if (string.IsNullOrEmpty(password))
                throw new HuaweiHiLinkException("Enter the modem admin password before logging in.");

            var passwordType = state.PasswordType;
            if (passwordType != "3" && passwordType != "4")
                passwordType = "4";

            var token = await TakePostTokenAsync(cancellationToken);
            var encodedPassword = passwordType == "3"
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(password))
                : Base64Sha256Hex(username + Base64Sha256Hex(password) + token);

            var payload = new XElement("request",
                new XElement("Username", username),
                new XElement("Password", encodedPassword),
                new XElement("password_type", passwordType));

            // Exactly one credential POST is made per explicit Login button press.
            // We deliberately do not retry a failed credential attempt because Huawei
            // firmware can trigger error 108007 after repeated attempts.
            var result = await PostXmlAsync("api/user/login", payload, cancellationToken, token);
            _loggedIn = IsOkResponse(result);
            return _loggedIn;
        }
        finally
        {
            _authGate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (!_loggedIn) return;
        var payload = new XElement("request", new XElement("Logout", "1"));
        try { await PostXmlAsync("api/user/logout", payload, cancellationToken); }
        finally { _loggedIn = false; }
    }

    public async Task<RouterStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync(cancellationToken);

        var infoTask = GetXmlAsync("api/device/information", cancellationToken, throwOnApiError: false);
        var statusTask = GetXmlAsync("api/monitoring/status", cancellationToken, throwOnApiError: false);
        var signalTask = GetXmlAsync("api/device/signal", cancellationToken, throwOnApiError: false);

        await Task.WhenAll(infoTask, statusTask, signalTask);

        var info = infoTask.Result;
        var status = statusTask.Result;
        var signal = signalTask.Result;

        var signalIcon = ParseInt(Value(status, "SignalIcon"));
        var maxSignal = ParseInt(Value(status, "maxsignal")) ?? 5;
        int? signalPercent = null;
        if (signalIcon is not null && maxSignal > 0)
            signalPercent = Math.Clamp((int)Math.Round(signalIcon.Value * 100d / maxSignal), 0, 100);

        var directSignal = ParseInt(Value(status, "SignalStrength"));
        if (directSignal is >= 0 and <= 100)
            signalPercent = directSignal;

        var clients = ParseInt(Value(status, "CurrentWifiUser", "currentwifiuser"));
        if (clients is null)
        {
            try { clients = (await GetConnectedDevicesAsync(cancellationToken)).Count; }
            catch { /* optional endpoint on some firmware */ }
        }

        var connectionCode = Value(status, "ConnectionStatus");
        var networkCode = Value(status, "CurrentNetworkTypeEx", "CurrentNetworkType");

        return new RouterStatus(
            Model: Value(info, "DeviceName", "devicename", "ModelName", "modelname") ?? "Huawei HiLink",
            Firmware: Value(info, "SoftwareVersion", "softwareversion", "WebUIVersion"),
            WanIp: Value(status, "WanIPAddress", "WanIPv6Address"),
            ConnectionState: MapConnectionStatus(connectionCode),
            ConnectedClients: clients,
            SignalPercent: signalPercent,
            BatteryPercent: ParseBatteryPercent(status),
            NetworkType: MapNetworkType(networkCode),
            Rsrp: Value(signal, "rsrp", "RSRP"),
            Rsrq: Value(signal, "rsrq", "RSRQ"),
            Sinr: Value(signal, "sinr", "SINR"));
    }

    /// <summary>
    /// Reads Wi-Fi configuration directly from the modem. This is more reliable than parsing
    /// localized netsh output and also works when the PC is connected to the modem via USB.
    /// </summary>
    public async Task<WifiInfo> GetWifiInfoAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync(cancellationToken);
        var xml = await GetXmlAsync("api/wlan/basic-settings", cancellationToken, throwOnApiError: false);
        if (xml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
            return new WifiInfo(null, null, null, null, null, null, null);

        return new WifiInfo(
            Ssid: Value(xml, "WifiSsid", "SSID", "ssid"),
            Enabled: ParseBool01(Value(xml, "WifiEnable")),
            Hidden: ParseBool01(Value(xml, "WifiHide")),
            Channel: Value(xml, "WifiChannel"),
            Mode: Value(xml, "WifiMode"),
            MaxClients: ParseInt(Value(xml, "WifiMaxAssoc", "TotalWifiUser")),
            ClientIsolation: ParseBool01(Value(xml, "WifiIsolate")));
    }

    public async Task<IReadOnlyList<ConnectedDevice>> GetConnectedDevicesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync(cancellationToken);
        var xml = await GetXmlAsync("api/wlan/host-list", cancellationToken, throwOnApiError: false);

        var result = new List<ConnectedDevice>();
        if (!xml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
            AddClientNodes(xml, result, "Host");

        // A few HiLink WebUI builds expose station-information instead of a useful host-list.
        // It is a fallback only; unsupported firmware simply returns an API error.
        if (result.Count == 0)
        {
            try
            {
                var stations = await GetXmlAsync("api/wlan/station-information", cancellationToken, throwOnApiError: false);
                if (!stations.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    AddClientNodes(stations, result, "Host");
                    AddClientNodes(stations, result, "Station");
                    AddClientNodes(stations, result, "WifiHost");
                }
            }
            catch
            {
                // Optional fallback endpoint.
            }
        }

        return result
            .Where(x => x.IpAddress != "-" || x.MacAddress != "-")
            .GroupBy(x => x.MacAddress != "-" ? x.MacAddress : x.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }


    public async Task<IReadOnlyList<MacFilterDevice>> GetBlacklistedDevicesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync(cancellationToken);

        foreach (var endpoint in new[]
                 {
                     "api/wlan/multi-macfilter-settings-ex",
                     "api/wlan/multi-macfilter-settings"
                 })
        {
            var xml = await GetXmlAsync(endpoint, cancellationToken, throwOnApiError: false);
            if (xml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
                continue;

            var parsed = ParseBlacklist(xml);
            if (parsed.Count > 0 || ContainsMacFilterShape(xml))
                return parsed;
        }

        var legacy = await GetXmlAsync("api/wlan/mac-filter", cancellationToken, throwOnApiError: false);
        if (!legacy.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
            return ParseBlacklist(legacy);

        return Array.Empty<MacFilterDevice>();
    }

    public async Task AddToBlacklistAsync(
        string macAddress,
        string? hostName,
        CancellationToken cancellationToken = default)
    {
        var mac = NormalizeMac(macAddress);
        if (mac is null)
            throw new HuaweiHiLinkException("The selected Wi-Fi client does not have a valid MAC address.");

        var name = string.IsNullOrWhiteSpace(hostName) ? "Blocked device" : hostName.Trim();
        var existing = (await GetBlacklistedDevicesAsync(cancellationToken)).ToList();

        if (existing.Any(x => MacEquals(x.MacAddress, mac)))
            return;

        existing.Add(new MacFilterDevice(name, mac, "0"));

        try
        {
            await SetBlacklistMultiAsync(existing, cancellationToken);
            return;
        }
        catch (HuaweiHiLinkException ex) when (ex.ApiCode is "100002" or "100006")
        {
            var payload = new XElement("request",
                new XElement("wifihostname", name),
                new XElement("WifiMacFilterMac", mac));

            await PostXmlAsync("api/wlan/mac-filter", payload, cancellationToken);
        }
    }

    public async Task RemoveFromBlacklistAsync(
        string macAddress,
        CancellationToken cancellationToken = default)
    {
        var mac = NormalizeMac(macAddress);
        if (mac is null)
            throw new HuaweiHiLinkException("The selected blacklist row does not contain a valid MAC address.");

        var existing = (await GetBlacklistedDevicesAsync(cancellationToken))
            .Where(x => !MacEquals(x.MacAddress, mac))
            .ToList();

        await SetBlacklistMultiAsync(existing, cancellationToken);
    }

    public async Task KickWifiClientAsync(
        string macAddress,
        string? hostName,
        TimeSpan? blockDuration = null,
        CancellationToken cancellationToken = default)
    {
        var mac = NormalizeMac(macAddress);
        if (mac is null)
            throw new HuaweiHiLinkException("The selected Wi-Fi client does not have a valid MAC address.");

        var existing = (await GetBlacklistedDevicesAsync(cancellationToken)).ToList();
        if (existing.Any(x => MacEquals(x.MacAddress, mac)))
            throw new HuaweiHiLinkException("This device is already blacklisted.");

        var temporary = existing
            .Append(new MacFilterDevice(
                string.IsNullOrWhiteSpace(hostName) ? "Temporarily disconnected" : hostName.Trim(),
                mac,
                "0"))
            .ToList();

        await SetBlacklistMultiAsync(temporary, cancellationToken);

        try
        {
            await Task.Delay(blockDuration ?? TimeSpan.FromSeconds(4), cancellationToken);
        }
        finally
        {
            try
            {
                await SetBlacklistMultiAsync(existing, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new HuaweiHiLinkException(
                    "The client was disconnected, but the temporary blacklist entry could not be removed automatically. " +
                    "Open the Blacklist section and remove it manually. " + ex.Message,
                    ex);
            }
        }
    }

    private async Task SetBlacklistMultiAsync(
        IReadOnlyList<MacFilterDevice> devices,
        CancellationToken cancellationToken)
    {
        var ssid = new XElement("Ssid",
            new XElement("Index", "0"),
            new XElement("WifiMacFilterStatus", "2"));

        for (var i = 0; i < devices.Count; i++)
        {
            var mac = NormalizeMac(devices[i].MacAddress);
            if (mac is null) continue;

            ssid.Add(new XElement($"WifiMacFilterMac{i}", mac));
            ssid.Add(new XElement($"wifihostname{i}",
                string.IsNullOrWhiteSpace(devices[i].HostName) ? $"Device {i + 1}" : devices[i].HostName));
        }

        var payload = new XElement("request",
            new XElement("Ssids", ssid));

        await PostXmlAsync("api/wlan/multi-macfilter-settings", payload, cancellationToken);
    }

    private static List<MacFilterDevice> ParseBlacklist(XElement xml)
    {
        var result = new List<MacFilterDevice>();

        foreach (var macNode in xml.DescendantsAndSelf().Where(x =>
                     x.Name.LocalName.Equals("WifiMacFilterMac", StringComparison.OrdinalIgnoreCase) ||
                     x.Name.LocalName.StartsWith("WifiMacFilterMac", StringComparison.OrdinalIgnoreCase)))
        {
            if (macNode.Ancestors().Any(a =>
                    a.Name.LocalName.Contains("whitelist", StringComparison.OrdinalIgnoreCase)))
                continue;

            var ssidNode = macNode.Ancestors()
                .FirstOrDefault(a => a.Name.LocalName.Equals("Ssid", StringComparison.OrdinalIgnoreCase));
            var directStatus = ssidNode?.Elements()
                .FirstOrDefault(x => x.Name.LocalName.Equals("WifiMacFilterStatus", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();

            // Status 1 is whitelist, status 2 is blacklist.
            if (directStatus == "1" &&
                !macNode.Ancestors().Any(a => a.Name.LocalName.Contains("blacklist", StringComparison.OrdinalIgnoreCase)))
                continue;

            var mac = NormalizeMac(macNode.Value);
            if (mac is null) continue;

            var suffix = macNode.Name.LocalName["WifiMacFilterMac".Length..];
            var parent = macNode.Parent;
            string? name = null;

            if (parent is not null)
            {
                var nameNode = parent.Elements().FirstOrDefault(x =>
                    x.Name.LocalName.Equals($"wifihostname{suffix}", StringComparison.OrdinalIgnoreCase));

                name ??= nameNode?.Value;

                if (string.IsNullOrWhiteSpace(name))
                    name = Value(parent, "wifihostname", "HostName", "hostname");
            }

            var ssid = macNode.AncestorsAndSelf()
                .Select(x => Value(x, "Index"))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "0";

            if (!result.Any(x => MacEquals(x.MacAddress, mac)))
                result.Add(new MacFilterDevice(
                    string.IsNullOrWhiteSpace(name) ? "Blocked device" : name.Trim(),
                    mac,
                    ssid));
        }

        return result;
    }

    private static bool ContainsMacFilterShape(XElement xml) =>
        xml.DescendantsAndSelf().Any(x =>
            x.Name.LocalName.Contains("MacFilter", StringComparison.OrdinalIgnoreCase) ||
            x.Name.LocalName.Contains("blacklist", StringComparison.OrdinalIgnoreCase) ||
            x.Name.LocalName.Equals("Ssids", StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return null;

        return string.Join(":", Enumerable.Range(0, 6)
            .Select(i => hex.Substring(i * 2, 2)));
    }

    private static bool MacEquals(string? left, string? right)
    {
        var a = NormalizeMac(left);
        var b = NormalizeMac(right);
        return a is not null && b is not null &&
               a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SmsCounts> GetSmsCountsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync(cancellationToken);
        var xml = await GetXmlAsync("api/sms/sms-count", cancellationToken);
        return new SmsCounts(
            LocalUnread: ParseInt(Value(xml, "LocalUnread")) ?? 0,
            LocalInbox: ParseInt(Value(xml, "LocalInbox")) ?? 0,
            LocalOutbox: ParseInt(Value(xml, "LocalOutbox")) ?? 0,
            LocalDraft: ParseInt(Value(xml, "LocalDraft")) ?? 0,
            SimUnread: ParseInt(Value(xml, "SimUnread")) ?? 0,
            SimInbox: ParseInt(Value(xml, "SimInbox")) ?? 0);
    }

    public async Task<IReadOnlyList<SmsMessage>> GetSmsMessagesAsync(
        SmsBoxType box,
        int page = 1,
        int readCount = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;

        // E5573/E5573Cs firmware is notably picky here. Several older WebUI builds
        // return 113017 (illegal SMS argument) when ReadCount is larger than 20.
        // The stock WebUI and multiple independent HiLink clients use 20.
        readCount = Math.Clamp(readCount, 1, 20);

        var payload = new XElement("request",
            new XElement("PageIndex", page),
            new XElement("ReadCount", readCount),
            new XElement("BoxType", (int)box),
            new XElement("SortType", 0),
            new XElement("Ascending", 0),
            new XElement("UnreadPreferred", 0));

        var xml = await PostXmlAsync("api/sms/sms-list", payload, cancellationToken);
        return xml.Descendants()
            .Where(x => x.Name.LocalName.Equals("Message", StringComparison.OrdinalIgnoreCase))
            .Select(message => new SmsMessage(
                Index: Value(message, "Index") ?? "",
                Phone: Value(message, "Phone") ?? "Unknown",
                Content: Value(message, "Content") ?? "",
                Date: Value(message, "Date") ?? "",
                IsRead: Value(message, "Smstat", "Read") == "1",
                SmsType: Value(message, "SmsType"),
                Sca: Value(message, "Sca")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Index))
            .ToList();
    }

    public async Task SendSmsAsync(string phone, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException("A destination phone number is required.", nameof(phone));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("The SMS message is empty.", nameof(message));

        var payload = new XElement("request",
            new XElement("Index", -1),
            new XElement("Phones", new XElement("Phone", phone.Trim())),
            new XElement("Sca", ""),
            new XElement("Content", message),
            new XElement("Length", message.Length),
            new XElement("Reserved", 1),
            new XElement("Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

        var result = await PostXmlAsync("api/sms/send-sms", payload, cancellationToken);
        if (!IsOkResponse(result))
            throw new HuaweiHiLinkException("The modem did not confirm SMS submission.");
    }

    public async Task MarkSmsReadAsync(string index, CancellationToken cancellationToken = default)
    {
        var result = await PostXmlAsync("api/sms/set-read",
            new XElement("request", new XElement("Index", index)), cancellationToken);
        if (!IsOkResponse(result))
            throw new HuaweiHiLinkException("The modem did not confirm the read-state change.");
    }

    public async Task DeleteSmsAsync(string index, CancellationToken cancellationToken = default)
    {
        var result = await PostXmlAsync("api/sms/delete-sms",
            new XElement("request", new XElement("Index", index)), cancellationToken);
        if (!IsOkResponse(result))
            throw new HuaweiHiLinkException("The modem did not confirm SMS deletion.");
    }

    public async Task SetMobileDataAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var payload = new XElement("request", new XElement("dataswitch", enabled ? "1" : "0"));
        await PostXmlAsync("api/dialup/mobile-dataswitch", payload, cancellationToken);
    }

    public async Task RebootAsync(CancellationToken cancellationToken = default)
    {
        var payload = new XElement("request", new XElement("Control", "1"));
        await PostXmlAsync("api/device/control", payload, cancellationToken);
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (_initialized && !force) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/webserver/SesTokInfo");
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var xml = ParseXml(text);
        ThrowIfApiError(xml, "api/webserver/SesTokInfo");

        var sesInfo = Value(xml, "SesInfo");
        var tokInfo = Value(xml, "TokInfo");
        if (string.IsNullOrWhiteSpace(sesInfo) || string.IsNullOrWhiteSpace(tokInfo))
            throw new HuaweiHiLinkException("The modem did not return a HiLink session/token.");

        var sessionId = sesInfo.StartsWith("SessionID=", StringComparison.OrdinalIgnoreCase)
            ? sesInfo["SessionID=".Length..]
            : sesInfo;

        _cookies.SetCookies(_baseUri, $"SessionID={sessionId}");
        SetTokens(new[] { tokInfo });
        CaptureTokens(response, append: true);
        _initialized = true;
    }

    private async Task<XElement> GetXmlAsync(string path, CancellationToken cancellationToken, bool throwOnApiError = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        CaptureTokens(response);
        var xml = ParseXml(text);
        if (throwOnApiError) ThrowIfApiError(xml, path);
        return xml;
    }

    private async Task<XElement> PostXmlAsync(
        string path,
        XElement payload,
        CancellationToken cancellationToken,
        string? explicitToken = null)
    {
        await EnsureSessionAsync(cancellationToken);
        var token = explicitToken ?? await TakePostTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                payload.ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8,
                "application/x-www-form-urlencoded")
        };
        request.Headers.TryAddWithoutValidation(TokenHeader, token);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        CaptureTokens(response);

        var xml = ParseXml(text);
        ThrowIfApiError(xml, path);
        return xml;
    }

    private Task<string> TakePostTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_postTokens.TryDequeue(out var queued) && !string.IsNullOrWhiteSpace(queued))
        {
            _token = queued;
            return Task.FromResult(queued);
        }

        if (!string.IsNullOrWhiteSpace(_token))
            return Task.FromResult(_token);

        throw new HuaweiHiLinkException("No request verification token is available.");
    }

    private void CaptureTokens(HttpResponseMessage response, bool append = false)
    {
        var ordered = new List<string>();
        AddHeaderTokens(response, TokenHeader + "one", ordered);
        AddHeaderTokens(response, TokenHeader + "two", ordered);
        AddHeaderTokens(response, TokenHeader, ordered);

        if (ordered.Count == 0) return;
        if (!append)
        {
            while (_postTokens.TryDequeue(out _)) { }
        }

        foreach (var token in ordered)
            _postTokens.Enqueue(token);
        _token = ordered[^1];
    }

    private static void AddHeaderTokens(HttpResponseMessage response, string headerName, List<string> output)
    {
        if (!response.Headers.TryGetValues(headerName, out var values)) return;
        foreach (var raw in values)
        {
            foreach (var token in raw.Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(token)) output.Add(token);
            }
        }
    }

    private void SetTokens(IEnumerable<string> tokens)
    {
        while (_postTokens.TryDequeue(out _)) { }
        foreach (var token in tokens.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            _postTokens.Enqueue(token);
            _token = token;
        }
    }

    private static void AddClientNodes(XElement xml, List<ConnectedDevice> output, string nodeName)
    {
        foreach (var node in xml.Descendants().Where(x => x.Name.LocalName.Equals(nodeName, StringComparison.OrdinalIgnoreCase)))
        {
            var ip = Value(node, "IpAddress", "IPAddress", "ipaddress", "IP");
            var mac = Value(node, "MacAddress", "MACAddress", "macaddress", "Mac", "MAC");
            if (string.IsNullOrWhiteSpace(ip) && string.IsNullOrWhiteSpace(mac)) continue;

            output.Add(new ConnectedDevice(
                HostName: Value(node, "HostName", "hostname", "Name", "DeviceName") ?? "Unknown device",
                IpAddress: ip ?? "-",
                MacAddress: mac ?? "-",
                AssociatedTime: Value(node, "AssociatedTime", "associatedtime", "ConnectTime")));
        }
    }

    private static XElement ParseXml(string text)
    {
        try { return XDocument.Parse(text).Root ?? throw new HuaweiHiLinkException("Empty XML response."); }
        catch (Exception ex) when (ex is not HuaweiHiLinkException)
        {
            throw new HuaweiHiLinkException("Invalid XML returned by the modem.", ex);
        }
    }

    private static void ThrowIfApiError(XElement xml, string? endpoint = null)
    {
        if (!xml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase)) return;
        var code = Value(xml, "code") ?? "unknown";
        var message = code switch
        {
            "100002" => "Unsupported request or this firmware does not expose the requested feature.",
            "100003" => "The modem is busy.",
            "100004" => "The requested operation timed out in the modem.",
            "100005" => "The modem denied the request.",
            "100006" => "The modem rejected one or more request parameters.",
            "108001" => "Username or password is incorrect.",
            "108002" => "Username or password is incorrect.",
            "108003" => "User is already logged in.",
            "108006" => "Login is required.",
            "108007" => "Login is temporarily locked after failed attempts.",
            "113017" => "The SMS request contains an empty, invalid, or unsupported argument.",
            "113018" => "The SMS operation timed out.",
            "113020" => "The modem could not query the requested SMS index/list.",
            "113036" => "The modem could not delete the SMS.",
            "113053" => "SMS storage does not have enough free space.",
            "113054" => "The destination phone number is too long.",
            "125001" => "Invalid request verification token.",
            "125002" => "Invalid request verification token.",
            "125003" => "Invalid session.",
            _ => "Huawei HiLink API error."
        };
        var where = string.IsNullOrWhiteSpace(endpoint) ? "" : $", endpoint {endpoint}";
        throw new HuaweiHiLinkException($"{message} (code {code}{where})", code);
    }

    private static bool IsOkResponse(XElement xml) =>
        xml.Name.LocalName.Equals("response", StringComparison.OrdinalIgnoreCase) &&
        xml.Value.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);

    private static string? Value(XElement? parent, params string[] names)
    {
        if (parent is null) return null;
        foreach (var name in names)
        {
            var node = parent.DescendantsAndSelf()
                .FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (node is not null && !string.IsNullOrWhiteSpace(node.Value))
                return node.Value.Trim();
        }
        return null;
    }

    private static int? ParseInt(string? value) => int.TryParse(value, out var result) ? result : null;

    private static bool? ParseBool01(string? value) => value switch
    {
        "1" => true,
        "0" => false,
        _ => null
    };

    private static int? ParseBatteryPercent(XElement status)
    {
        var percent = ParseInt(Value(status, "BatteryPercent"));
        if (percent is >= 0 and <= 100) return percent;

        var level = ParseInt(Value(status, "BatteryLevel"));
        if (level is >= 0 and <= 4) return level * 25;
        return null;
    }

    private static string Base64Sha256Hex(string input)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(hex));
    }

    private static string MapConnectionStatus(string? code) => code switch
    {
        "1" => "Roaming / Connected",
        "900" => "Connecting",
        "901" => "Connected",
        "902" => "Disconnected",
        "903" => "Disconnecting",
        "904" => "Connection failed",
        "905" => "No service",
        _ => string.IsNullOrWhiteSpace(code) ? "Unknown" : $"Unknown ({code})"
    };

    private static string MapNetworkType(string? code) => code switch
    {
        "1" => "GSM",
        "2" => "GPRS",
        "3" => "EDGE",
        "4" => "WCDMA / 3G",
        "5" => "HSDPA",
        "6" => "HSUPA",
        "7" => "HSPA",
        "9" => "HSPA+",
        "19" => "LTE / 4G",
        "101" => "LTE / 4G",
        _ => string.IsNullOrWhiteSpace(code) ? "Unknown" : $"Network code {code}"
    };

    private static Uri NormalizeBaseUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    public void Dispose()
    {
        _http.Dispose();
        _authGate.Dispose();
    }
}

public sealed record LoginStateInfo(
    bool IsLoggedIn,
    bool IsLocked,
    int RemainingWaitSeconds,
    string PasswordType,
    string? Username,
    string? RawState);

public sealed class HuaweiHiLinkException : Exception
{
    public string? ApiCode { get; }
    public HuaweiHiLinkException(string message, string? apiCode = null) : base(message) => ApiCode = apiCode;
    public HuaweiHiLinkException(string message, Exception inner) : base(message, inner) { }
}
