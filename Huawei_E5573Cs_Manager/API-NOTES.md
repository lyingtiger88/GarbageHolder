# Huawei E5573Cs API notes

This project talks only to the modem's local Huawei HiLink HTTP/XML API.

## SMS

Typical endpoints used by this firmware family:

- `GET api/sms/sms-count`
- `POST api/sms/sms-list`
- `POST api/sms/send-sms`
- `POST api/sms/set-read`
- `POST api/sms/delete-sms`

Older E5573-class WebUI builds are strict about `sms-list` arguments. v5 intentionally uses `ReadCount=20`. Error `113017` means an SMS argument was null, illegal, or unsupported.

## Wi-Fi clients / MAC filter

- `GET api/wlan/host-list`
- `GET api/wlan/station-information` (fallback)
- `GET api/wlan/multi-macfilter-settings-ex`
- `GET/POST api/wlan/multi-macfilter-settings`
- `GET/POST api/wlan/mac-filter` (legacy fallback)

Huawei's public/commonly reverse-engineered HiLink surface does not expose a consistently supported per-client deauthentication endpoint on this generation. The app's **Disconnect selected** command therefore temporarily adds that client's MAC to the blacklist, waits about four seconds, then restores the previous blacklist.

Do not use Disconnect/Blacklist on the PC running the manager. v5 checks the selected client's IP against the current local IPv4 and blocks that action when they match.
