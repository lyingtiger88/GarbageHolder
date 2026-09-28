## v6

- Added Traffic tab with current-session upload/download, live rates, connection time, lifetime modem counters and current-month traffic.
- Added local quarterly traffic history.
- Converted Wi-Fi client connection duration from raw seconds to HH:MM:SS.
- Added per-client Upload, Download and Total usage columns.
- Per-client counters are populated only from real firmware fields / optional LAN-host detail data; unsupported E5573Cs firmware shows N/A rather than fabricated estimates.
- Added human-readable byte formatting.

# Changelog

## v5

- Fixed SMS inbox refresh on older E5573/E5573Cs WebUI builds by using the conservative `ReadCount=20` used by Huawei-style HiLink clients instead of 100.
- SMS count and SMS list requests are now sequential to avoid verification-token races on older firmware.
- HiLink API exceptions now include the endpoint that returned the error.
- Added a readable mapping for SMS error `113017` (invalid/unsupported SMS argument).
- Added Wi-Fi client actions:
  - Disconnect selected client (implemented as a short, temporary MAC blacklist and then automatic restore).
  - Add selected client to blacklist.
  - View/refresh blacklist.
  - Remove selected blacklist entry.
- Added a safety guard that prevents the manager from blacklisting/disconnecting the PC that is currently running the app based on its local IP.
- MAC blacklist support prefers `api/wlan/multi-macfilter-settings[-ex]` and falls back to the legacy `api/wlan/mac-filter` endpoint for adding a block where supported.

## v4

- Removed automatic credential resubmission from protected actions.
- Added modem-side login lockout inspection.
- Reused authenticated sessions instead of repeatedly calling Login.
