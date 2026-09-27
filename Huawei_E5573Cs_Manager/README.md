# Huawei E5573Cs Manager v5

Native Windows WPF manager for Huawei E5573/E5573Cs HiLink modems.

## Main features

- Automatic HiLink detection
- Admin login with Huawei password type 3/4 support
- Model, firmware, WAN IP, network type, signal, battery and client count
- Connected Wi-Fi client list
- Disconnect selected Wi-Fi client
- Add client to MAC blacklist
- View and remove blacklist entries
- SMS Inbox / Sent / Draft browsing
- Read SMS body, mark read, delete, and send SMS
- Mobile data ON/OFF
- Reboot modem
- Activity log

## v5 SMS fix

On the tested E5573Cs-322 family, error `113017` means the SMS API rejected one or more request arguments. v4 asked for up to 100 messages in one `sms-list` request. Older Huawei WebUI builds commonly use a count of 20 and can reject larger counts. v5 uses 20 and performs SMS count/list requests sequentially.

If an SMS call still fails, the log now includes the exact endpoint that returned the Huawei error code.

## Build

From PowerShell in the project directory:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\publish-win64.ps1
```

Or directly:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Output:

`bin\Release\net8.0-windows\win-x64\publish\`

## Safety

MAC blacklist changes can disconnect devices immediately. The app refuses to block/disconnect a client whose IP matches the Windows PC currently running the manager, because doing so could cut off access before a temporary block can be reverted.
