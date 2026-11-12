# Windows 2.6.13 UI verification — 2026-11-12

Based on stable 2.6.12. Only home status display and routing page layout changed; no routing settings or connection behavior changed.

- Release build passed locally.
- DesktopControlsUiTests: 646 checks passed. Home VPN-2 visibility/name in disabled, pending and assigned modes; toolbar button gaps and separate scroll viewport at 680/900/1200px; seven theme/window/text-scale cases. Inspected home-dual.png and routing-toolbar-900.png visually.
- MultiExitHealthDisplayTests: 19 checks passed. Secondary failure separate from primary success, secondary latency, changed-config invalidation, no assigned rules, and no selected secondary.
- Existing broader routing/installer tests run by release CI before publication.
- No user VPN restart, personal profile write, installation or host routing change performed. HTTPS status tests used synthetic results; no new live tunnel test.

Evidence: artifacts/home-vpn2-toolbar/build/MultiExitHealthDisplayTests/run.log and artifacts/home-vpn2-toolbar/ui. Recreate with Build-Review.ps1, DesktopControlsUiTests.ps1 and Run-MultiServerRegressionChecks.ps1 -Cases MultiExitHealthDisplayTests.
