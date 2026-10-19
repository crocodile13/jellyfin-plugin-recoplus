# reco-export

Read-only, pseudonymised export of a Jellyfin server's library and watch history, used to evaluate the engine offline (`prototype/`).

- Only `GET` requests are sent to Jellyfin, and the optional Playback Reporting database is opened read-only.
- The API key comes from a file with mode 600 (`~/.config/jellyfin-api.env` by default: `JF_URL=…`, `JF_KEY=…`) and is never printed.
- Accounts are exported as `u01`, `u02`, …. The account mapping is written to a separate file with mode 600, which you should keep to yourself.
- The export contains no account names or IDs, device names, clients or addresses.

```bash
python3 reco-export.py --playback-db /path/to/playback_reporting.db
python3 test_reco_export.py
```

The interface and messages are in French.
