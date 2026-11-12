# Windows 2.6.12 routing cards — 2026-11-12

Local checkout: release-windows-dual-2.6.4 (historical directory name). Based on stable 2.6.11.

Implemented detached editable lists for multi-server routing, alongside preserved legacy names, block lists and other assignments. Duplicate warnings never remove entries. Persisted documents retain duplicates; only compilation resolves identical selectors. Transition from ordinary lists now preserves entries assigned to multiple destinations. New VPN-2 executable names use canonical case-insensitive path regexes understood by the route inspector.

Local checks:
- Build-Review release build passed (local Roslyn and pinned dependency directory).
- DesktopControlsUiTests: 567 checks, including 13 direct + 72 primary site fixtures, all six main cards, legacy app visibility, informational warning and unsaved draft surviving navigation. Seven desktop size/theme cases; routing screenshot manually inspected.
- RoutingEntryUiTests: eight original editors, multiline paste, add/remove, warnings for URL/host and name/path collisions, Zapret locks, light/dark, large text/narrow views passed.
- RoutingCardsTests: 36 checks covering document roundtrip, old rules, duplicates, deletion isolation, process names, compiled routes, export and migration.
- MultiServerSettingsTests 21, MultiServerConfigTests 59, MultiServerEditorTests 107, MultiServerRoutingHistoryTests 70, RoutingApplyTransactionTests 27 passed.
- SettingsTransferTests and SmoothScrollIntegrationTests passed.
- Real installed profile decoded only in memory: 85 site assignments and 20 legacy app entries represented; unchanged state.dat SHA256 before/after. No secrets or plaintext settings written to evidence.
- Existing live sing-box PID 35968 unchanged. No host installation, VPN restart or route modifications performed.

Synthetic config is checked with the bundled native sing-box `check` command; release workflow repeats tests and native validation before publication. No new live two-exit network test was performed on the user's connected VPN.

Evidence: artifacts/routing-cards-warning/verified-build (tests, logs, synthetic config), desktop-ui-verified and entry-ui-warning. Recreate with scripts/Build-Review.ps1, scripts/Run-MultiServerRegressionChecks.ps1 and the listed WPF test scripts. Evidence folders must be fresh for the regression runner.
