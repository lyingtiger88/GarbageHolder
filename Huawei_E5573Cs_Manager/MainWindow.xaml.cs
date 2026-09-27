using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModemManagerNative.Adapters;
using ModemManagerNative.Models;
using ModemManagerNative.Services;

namespace ModemManagerNative;

public partial class MainWindow : Window
{
    private readonly NetworkDiscoveryService _network = new();
    private string? _gateway;
    private HuaweiHiLinkAdapter? _huawei;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            UpdateSmsCounter();
            await RefreshNetworkAsync();
            await DetectHuaweiAsync(silent: true);
        };
        Closed += (_, _) => _huawei?.Dispose();
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private async Task RefreshNetworkAsync()
    {
        try
        {
            var info = await _network.GetNetworkInfoAsync();
            _gateway = info.Gateway;
            GatewayText.Text = info.Gateway ?? "Not found";
            LocalIpText.Text = info.LocalIPv4 ?? "Not found";
            SsidText.Text = info.Ssid ?? "Unknown";

            if (!string.IsNullOrWhiteSpace(info.Gateway))
                AdminUrlBox.Text = $"http://{info.Gateway}";
            else
                AdminUrlBox.Text = "http://192.168.8.1";

            Log($"Network refreshed. Gateway={info.Gateway ?? "n/a"}, IPv4={info.LocalIPv4 ?? "n/a"}, SSID={info.Ssid ?? "n/a"}");
        }
        catch (Exception ex)
        {
            Log("Network refresh failed: " + ex.Message);
        }
    }

    private Uri GetAdminUri()
    {
        var raw = AdminUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) raw = "http://192.168.8.1";
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = "http://" + raw;
        return new Uri(raw);
    }

    private async Task<bool> DetectHuaweiAsync(bool silent = false)
    {
        try
        {
            _huawei?.Dispose();
            _huawei = new HuaweiHiLinkAdapter(GetAdminUri());
            var detected = await _huawei.CanHandleAsync();
            if (detected)
            {
                if (!silent) Log("Huawei HiLink API detected successfully.");
                await RefreshHuaweiStatusAsync();
                return true;
            }

            if (!silent) Log("Huawei HiLink API was not detected at the current admin address.");
            _huawei.Dispose();
            _huawei = null;
            return false;
        }
        catch (Exception ex)
        {
            if (!silent) Log("Huawei detection failed: " + ex.Message);
            _huawei?.Dispose();
            _huawei = null;
            return false;
        }
    }

    private async Task EnsureHuaweiAsync()
    {
        if (_huawei is not null) return;
        if (!await DetectHuaweiAsync())
            throw new InvalidOperationException("Huawei HiLink modem was not detected. Check that the PC is connected to the E5573Cs and verify the admin address.");
    }

    private async Task EnsureLoggedInAsync()
    {
        await EnsureHuaweiAsync();
        if (_huawei!.IsLoggedIn) return;

        // Important: protected actions never auto-submit the password. Auto-login here
        // used to turn one user mistake into several rapid attempts and could trigger
        // Huawei error 108007. Only the explicit Login button may submit credentials.
        var state = await _huawei.GetLoginStateAsync();
        if (state.IsLoggedIn)
        {
            LoginStateText.Text = "Logged in";
            return;
        }

        if (state.IsLocked)
        {
            var wait = state.RemainingWaitSeconds > 0
                ? $" Wait about {state.RemainingWaitSeconds} second(s)."
                : " Wait for the lockout to expire.";
            LoginStateText.Text = state.RemainingWaitSeconds > 0
                ? $"Locked ({state.RemainingWaitSeconds}s)"
                : "Temporarily locked";
            throw new InvalidOperationException("The modem has temporarily locked admin login." + wait + " No new login attempt was sent by this action.");
        }

        LoginStateText.Text = "Not logged in";
        throw new InvalidOperationException("Admin login is required. Enter the password and click Login first. This action did not submit a login attempt.");
    }

    private async Task RefreshHuaweiStatusAsync()
    {
        if (_huawei is null) return;
        try
        {
            var status = await _huawei.GetStatusAsync();
            ModelText.Text = string.IsNullOrWhiteSpace(status.Firmware)
                ? status.Model ?? "Huawei HiLink"
                : $"{status.Model}  |  {status.Firmware}";
            ConnectionText.Text = string.IsNullOrWhiteSpace(status.WanIp)
                ? status.ConnectionState ?? "Unknown"
                : $"{status.ConnectionState}  |  {status.WanIp}";
            NetworkSignalText.Text = $"{status.NetworkType ?? "Unknown"}  |  {(status.SignalPercent is null ? "-" : status.SignalPercent + "%")}";

            WifiInfo? wifi = null;
            try
            {
                wifi = await _huawei.GetWifiInfoAsync();
                if (!string.IsNullOrWhiteSpace(wifi.Ssid))
                    SsidText.Text = wifi.Ssid;
                UpdateWifiDetails(wifi);
            }
            catch (Exception ex)
            {
                Log("Wi-Fi settings unavailable: " + ex.Message);
            }

            IReadOnlyList<ConnectedDevice> devices = Array.Empty<ConnectedDevice>();
            try
            {
                devices = await _huawei.GetConnectedDevicesAsync();
                ClientsGrid.ItemsSource = devices;
            }
            catch (Exception ex)
            {
                Log("Connected-device list unavailable: " + ex.Message);
            }

            var clientCount = devices.Count > 0 ? devices.Count : status.ConnectedClients;
            BatteryClientsText.Text = $"{(status.BatteryPercent is null ? "Battery -" : "Battery " + status.BatteryPercent + "%")}  |  {(clientCount is null ? "Clients -" : "Clients " + clientCount)}";

            if (status.ConnectedClients is > 0 && devices.Count == 0)
                Log($"Modem reports {status.ConnectedClients} Wi-Fi client(s), but host-list returned no rows. Login may be required by this firmware.");

            try
            {
                var loginState = await _huawei.GetLoginStateAsync();
                if (loginState.IsLoggedIn)
                    LoginStateText.Text = "Logged in";
                else if (loginState.IsLocked)
                    LoginStateText.Text = loginState.RemainingWaitSeconds > 0
                        ? $"Temporarily locked (wait {loginState.RemainingWaitSeconds})"
                        : "Temporarily locked";
                else
                    LoginStateText.Text = "Not logged in";
            }
            catch
            {
                // Status data is still useful even if login-state probing is unavailable.
            }

            Log($"Huawei status: {status.Model}, {status.ConnectionState}, {status.NetworkType}, signal {status.SignalPercent?.ToString() ?? "n/a"}%.");
        }
        catch (HuaweiHiLinkException ex)
        {
            Log("Huawei API: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log("Could not refresh Huawei status: " + ex.Message);
        }
    }

    private void UpdateWifiDetails(WifiInfo wifi)
    {
        var enabled = wifi.Enabled is null ? "state ?" : wifi.Enabled.Value ? "ON" : "OFF";
        var hidden = wifi.Hidden == true ? "hidden" : "visible";
        var isolation = wifi.ClientIsolation == true ? "isolation ON" : "isolation OFF";
        WifiDetailsText.Text = $"SSID: {wifi.Ssid ?? "?"}   |   Wi-Fi {enabled}   |   Channel {wifi.Channel ?? "?"}   |   {wifi.Mode ?? "mode ?"}   |   Max {wifi.MaxClients?.ToString() ?? "?"}   |   {hidden}, {isolation}";
    }

    private async Task RefreshClientsAsync(bool requireLogin)
    {
        if (requireLogin) await EnsureLoggedInAsync();
        else await EnsureHuaweiAsync();

        var wifi = await _huawei!.GetWifiInfoAsync();
        if (!string.IsNullOrWhiteSpace(wifi.Ssid)) SsidText.Text = wifi.Ssid;
        UpdateWifiDetails(wifi);

        var devices = await _huawei.GetConnectedDevicesAsync();
        ClientsGrid.ItemsSource = devices;
        Log($"Wi-Fi client list refreshed: {devices.Count} client(s) returned by the modem API.");
    }

    private async Task RefreshBlacklistAsync(bool requireLogin = true)
    {
        if (requireLogin) await EnsureLoggedInAsync();
        else await EnsureHuaweiAsync();

        var devices = await _huawei!.GetBlacklistedDevicesAsync();
        BlacklistGrid.ItemsSource = devices;
        Log($"Wi-Fi blacklist refreshed: {devices.Count} entr{(devices.Count == 1 ? "y" : "ies")}.");
    }

    private bool IsLocalClient(ConnectedDevice device)
    {
        var localIp = LocalIpText.Text?.Trim();
        return !string.IsNullOrWhiteSpace(localIp) &&
               !string.IsNullOrWhiteSpace(device.IpAddress) &&
               localIp.Equals(device.IpAddress.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshSmsAsync()
    {
        await EnsureLoggedInAsync();
        var box = GetSelectedSmsBox();

        // Keep these requests sequential. Older HiLink firmware rotates verification
        // tokens aggressively, and parallel count/list requests can race each other.
        var counts = await _huawei!.GetSmsCountsAsync();
        var messages = await _huawei.GetSmsMessagesAsync(box, page: 1, readCount: 20);

        SmsGrid.ItemsSource = messages;
        SmsBodyBox.Clear();
        SmsCountText.Text = $"Inbox {counts.LocalInbox}  |  Unread {counts.LocalUnread}  |  Sent {counts.LocalOutbox}  |  Drafts {counts.LocalDraft}";
        Log($"SMS refreshed: {messages.Count} message(s) loaded from {box}.");
    }

    private SmsBoxType GetSelectedSmsBox() => SmsBoxCombo.SelectedIndex switch
    {
        1 => SmsBoxType.Sent,
        2 => SmsBoxType.Draft,
        _ => SmsBoxType.Inbox
    };

    private async void DetectHuawei_Click(object sender, RoutedEventArgs e) => await DetectHuaweiAsync();

    private async void RefreshHuawei_Click(object sender, RoutedEventArgs e)
    {
        await RefreshNetworkAsync();
        if (_huawei is null) await DetectHuaweiAsync();
        else await RefreshHuaweiStatusAsync();
    }

    private async void RefreshClients_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshClientsAsync(requireLogin: false);
        }
        catch (Exception ex)
        {
            Log("Could not refresh Wi-Fi clients: " + ex.Message);
        }
    }

    private async void RefreshBlacklist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshBlacklistAsync(requireLogin: true);
        }
        catch (Exception ex)
        {
            Log("Could not refresh Wi-Fi blacklist: " + ex.Message);
            MessageBox.Show(ex.Message, "Wi-Fi blacklist", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BlacklistClient_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ConnectedDevice device)
        {
            MessageBox.Show("Select a connected Wi-Fi device first.", "Wi-Fi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsLocalClient(device))
        {
            MessageBox.Show(
                "This row is the PC currently running the manager. Blocking it would cut the connection to the modem before the app could recover. Use another device to block this PC.",
                "Safety check", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Add {device.HostName} ({device.MacAddress}) to the Wi-Fi blacklist?\n\nThe device should lose Wi-Fi access and remain blocked until you remove it from the blacklist.",
            "Add to blacklist", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await EnsureLoggedInAsync();
            await _huawei!.AddToBlacklistAsync(device.MacAddress, device.HostName);
            Log($"Added {device.HostName} ({device.MacAddress}) to the Wi-Fi blacklist.");
            await Task.Delay(700);
            await RefreshClientsAsync(requireLogin: false);
            await RefreshBlacklistAsync(requireLogin: false);
        }
        catch (Exception ex)
        {
            Log("Could not add Wi-Fi client to blacklist: " + ex.Message);
            MessageBox.Show(ex.Message, "Wi-Fi blacklist", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DisconnectClient_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ConnectedDevice device)
        {
            MessageBox.Show("Select a connected Wi-Fi device first.", "Wi-Fi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsLocalClient(device))
        {
            MessageBox.Show(
                "This row is the PC currently running the manager. Disconnecting it would cut the app off from the modem, so this action is disabled for the local PC.",
                "Safety check", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Disconnect {device.HostName} ({device.MacAddress}) now?\n\nOn this HiLink generation the app performs a short temporary MAC blacklist (about 4 seconds) and then restores the previous blacklist. The client may reconnect afterward.",
            "Disconnect Wi-Fi client", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await EnsureLoggedInAsync();
            Log($"Disconnecting {device.HostName} ({device.MacAddress})...");
            await _huawei!.KickWifiClientAsync(device.MacAddress, device.HostName, TimeSpan.FromSeconds(4));
            Log($"Temporary disconnect completed for {device.HostName} ({device.MacAddress}); previous blacklist restored.");
            await Task.Delay(700);
            await RefreshClientsAsync(requireLogin: false);
            await RefreshBlacklistAsync(requireLogin: false);
        }
        catch (Exception ex)
        {
            Log("Could not disconnect Wi-Fi client: " + ex.Message);
            MessageBox.Show(ex.Message, "Disconnect Wi-Fi client", MessageBoxButton.OK, MessageBoxImage.Error);
            try { await RefreshBlacklistAsync(requireLogin: false); } catch { }
        }
    }

    private async void RemoveBlacklist_Click(object sender, RoutedEventArgs e)
    {
        if (BlacklistGrid.SelectedItem is not MacFilterDevice device)
        {
            MessageBox.Show("Select a blacklist entry first.", "Wi-Fi blacklist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove {device.HostName} ({device.MacAddress}) from the blacklist?",
            "Remove from blacklist", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await EnsureLoggedInAsync();
            await _huawei!.RemoveFromBlacklistAsync(device.MacAddress);
            Log($"Removed {device.HostName} ({device.MacAddress}) from the Wi-Fi blacklist.");
            await RefreshBlacklistAsync(requireLogin: false);
            await RefreshClientsAsync(requireLogin: false);
        }
        catch (Exception ex)
        {
            Log("Could not remove blacklist entry: " + ex.Message);
            MessageBox.Show(ex.Message, "Wi-Fi blacklist", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void HuaweiLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureHuaweiAsync();

            // Check router-side lockout before submitting credentials. This GET does
            // not count as a password attempt.
            var preState = await _huawei!.GetLoginStateAsync();
            if (preState.IsLoggedIn)
            {
                LoginStateText.Text = "Logged in";
                Log("Huawei session is already authenticated; no new login attempt was sent.");
                return;
            }

            if (preState.IsLocked)
            {
                LoginStateText.Text = preState.RemainingWaitSeconds > 0
                    ? $"Locked ({preState.RemainingWaitSeconds}s)"
                    : "Temporarily locked";
                Log(preState.RemainingWaitSeconds > 0
                    ? $"Huawei login is locked by the modem for about {preState.RemainingWaitSeconds} more second(s). No credentials were submitted."
                    : "Huawei login is currently locked by the modem. No credentials were submitted.");
                return;
            }

            var user = string.IsNullOrWhiteSpace(HuaweiUserBox.Text) ? "admin" : HuaweiUserBox.Text.Trim();
            var ok = await _huawei.LoginAsync(user, HuaweiPasswordBox.Password);
            LoginStateText.Text = ok ? "Logged in" : "Login failed";
            Log(ok ? "Huawei admin login succeeded." : "Huawei admin login failed. Exactly one credential attempt was sent.");
            if (ok)
            {
                await RefreshHuaweiStatusAsync();
                try { await RefreshClientsAsync(requireLogin: false); }
                catch (Exception ex) { Log("Post-login Wi-Fi client refresh failed: " + ex.Message); }

                try { await RefreshBlacklistAsync(requireLogin: false); }
                catch (Exception ex) { Log("Post-login blacklist refresh failed: " + ex.Message); }
            }
        }
        catch (HuaweiHiLinkException ex) when (ex.ApiCode == "108007")
        {
            LoginStateText.Text = "Temporarily locked";
            Log("Login blocked by modem lockout: " + ex.Message);
        }
        catch (Exception ex)
        {
            LoginStateText.Text = "Login failed";
            Log("Login error: " + ex.Message);
        }
    }

    private async void MobileDataOn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureLoggedInAsync();
            await _huawei!.SetMobileDataAsync(true);
            Log("Mobile data enabled.");
            await RefreshHuaweiStatusAsync();
        }
        catch (Exception ex) { Log("Could not enable mobile data: " + ex.Message); }
    }

    private async void MobileDataOff_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureLoggedInAsync();
            await _huawei!.SetMobileDataAsync(false);
            Log("Mobile data disabled.");
            await RefreshHuaweiStatusAsync();
        }
        catch (Exception ex) { Log("Could not disable mobile data: " + ex.Message); }
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show("Reboot the Huawei E5573Cs now? Wi-Fi and internet will disconnect briefly.",
            "Confirm reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await EnsureLoggedInAsync();
            await _huawei!.RebootAsync();
            Log("Reboot command accepted by the modem.");
        }
        catch (Exception ex) { Log("Could not reboot modem: " + ex.Message); }
    }

    private async void RefreshSms_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshSmsAsync();
        }
        catch (Exception ex)
        {
            Log("Could not refresh SMS: " + ex.Message);
            MessageBox.Show(ex.Message, "SMS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SmsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SmsGrid.SelectedItem is not SmsMessage message)
        {
            SmsBodyBox.Clear();
            return;
        }

        SmsBodyBox.Text = $"From/To: {message.Phone}{Environment.NewLine}Date: {message.Date}{Environment.NewLine}State: {message.ReadState}{Environment.NewLine}{Environment.NewLine}{message.Content}";
    }

    private async void MarkSmsRead_Click(object sender, RoutedEventArgs e)
    {
        if (SmsGrid.SelectedItem is not SmsMessage message)
        {
            MessageBox.Show("Select an SMS first.", "SMS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await EnsureLoggedInAsync();
            await _huawei!.MarkSmsReadAsync(message.Index);
            Log($"SMS {message.Index} marked as read.");
            await RefreshSmsAsync();
        }
        catch (Exception ex)
        {
            Log("Could not mark SMS as read: " + ex.Message);
            MessageBox.Show(ex.Message, "SMS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteSms_Click(object sender, RoutedEventArgs e)
    {
        if (SmsGrid.SelectedItem is not SmsMessage message)
        {
            MessageBox.Show("Select an SMS first.", "SMS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show($"Delete SMS from {message.Phone}?", "Delete SMS",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await EnsureLoggedInAsync();
            await _huawei!.DeleteSmsAsync(message.Index);
            Log($"SMS {message.Index} deleted.");
            await RefreshSmsAsync();
        }
        catch (Exception ex)
        {
            Log("Could not delete SMS: " + ex.Message);
            MessageBox.Show(ex.Message, "SMS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SendSms_Click(object sender, RoutedEventArgs e)
    {
        var phone = SmsPhoneBox.Text.Trim();
        var message = SmsComposeBox.Text;

        if (string.IsNullOrWhiteSpace(phone))
        {
            MessageBox.Show("Enter the destination phone number.", "Send SMS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            MessageBox.Show("Write a message first.", "Send SMS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (used, limit, encoding) = GetSmsLength(message);
        if (used > limit)
        {
            MessageBox.Show($"This build sends one SMS segment at a time. The message uses {used}/{limit} units for {encoding}. Shorten it before sending.",
                "SMS too long", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SendSmsButton.IsEnabled = false;
            await EnsureLoggedInAsync();
            await _huawei!.SendSmsAsync(phone, message);
            Log($"SMS submitted successfully to {phone}.");
            SmsComposeBox.Clear();

            // Refresh counts and sent box without forcing the user to switch tabs.
            try
            {
                var counts = await _huawei.GetSmsCountsAsync();
                SmsCountText.Text = $"Inbox {counts.LocalInbox}  |  Unread {counts.LocalUnread}  |  Sent {counts.LocalOutbox}  |  Drafts {counts.LocalDraft}";
            }
            catch { /* sending already succeeded */ }

            MessageBox.Show("The modem accepted the SMS for sending.", "SMS sent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("SMS send failed: " + ex.Message);
            MessageBox.Show(ex.Message, "SMS send failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SendSmsButton.IsEnabled = true;
        }
    }

    private void SmsComposeBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSmsCounter();

    private void UpdateSmsCounter()
    {
        if (SmsCharCountText is null || SmsComposeBox is null) return;
        var (used, limit, encoding) = GetSmsLength(SmsComposeBox.Text ?? string.Empty);
        SmsCharCountText.Text = $"{used}/{limit}  {encoding}";
    }

    private static (int Used, int Limit, string Encoding) GetSmsLength(string message)
    {
        const string gsmBasic = "@£$¥èéùìòÇØøÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ`¿abcdefghijklmnopqrstuvwxyzäöñüà";
        const string gsmExtended = "^{}\\[~]|€";

        var septets = 0;
        foreach (var ch in message)
        {
            if (ch == '\n' || ch == '\r' || gsmBasic.Contains(ch)) septets += 1;
            else if (gsmExtended.Contains(ch)) septets += 2;
            else return (message.Length, 70, "Unicode");
        }
        return (septets, 160, "GSM-7");
    }

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = GetAdminUri().ToString();
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log("Opened router admin panel: " + url);
        }
        catch (Exception ex) { Log("Could not open admin panel: " + ex.Message); }
    }

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gateway)) await RefreshNetworkAsync();
        var host = _gateway;
        if (string.IsNullOrWhiteSpace(host)) host = GetAdminUri().Host;

        var result = await _network.PingAsync(host);
        LatencyText.Text = result.Success ? $"{result.RoundtripTimeMs} ms" : "Failed";
        Log(result.Success ? $"Gateway ping succeeded in {result.RoundtripTimeMs} ms." : $"Gateway ping failed: {result.Error}");
    }

    private void CopyGateway_Click(object sender, RoutedEventArgs e)
    {
        var value = !string.IsNullOrWhiteSpace(_gateway) ? _gateway : GetAdminUri().Host;
        Clipboard.SetText(value);
        Log("Gateway address copied to clipboard.");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"e5573cs-manager-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, LogBox.Text);
            Log("Log exported to: " + dlg.FileName);
        }
    }
}
