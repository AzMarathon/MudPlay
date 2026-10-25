# GMCP replay fixtures

These synthetic sequences come from Anomaly contract 1.0.0 (schema commit `0e9ac9f`, including nullable monster level). They exercise the client and are not evidence of server correctness. Captured recordings will replace matching replay inputs when the isolated test host supplies them. Preserve capture headers and raw bytes when importing captures.

`Captured/` contains server captures for client replay. Client-to-server text is omitted so login credentials do not enter the public repository; server frames, GMCP raw bytes, headers and build identifiers are preserved. The full evidence remains in the shared contract repository. These replays validate client handling; they do not replace active client/server runs.

The opt-in live xUnit scenarios require disposable accounts in `MUDPLAY_GMCP_ACCOUNT`, `MUDPLAY_GMCP_PASSWORD`, `MUDPLAY_GMCP_SYSOP_ACCOUNT` and `MUDPLAY_GMCP_SYSOP_PASSWORD`. They are skipped during normal regression runs. Coordinate an exclusive test window before running them: one scenario toggles GMCP on the isolated host. `run-live.py` runs them through a passive recorder and accepts the disposable account pairs as JSON on stdin. Its evidence directory must be outside this repository because full captures include login traffic. Never commit those full captures.
