# Windows 2.6.23 verification — 2026-11-17

## Reproduction and cause
The user's existing VPN stayed connected throughout the local work. No live routing or DNS settings were changed. Three initial profiles and another eight-profile sample failed with the 2.6.22 map checker. A native isolated test returned HTTPS 204 immediately after replacing the candidate bootstrap DNS with authenticated DoH and enabling independent interface detection. The separate probe cannot use the active tunnel's FakeIP mappings.

The same eight-profile sample using the fixed checker produced six verified HTTPS successes and two completed failures. First positive result arrived after 1.8 seconds. This does not establish that the two failing servers are offline. The UI labels a failed HTTPS check, not server downtime.

## Local tests
- Build-Review.ps1 with the local Roslyn compiler: PASS.
- InteractiveMapTests.ps1 with installed 45 MB sing-box: PASS, including encrypted fixture HTTPS, wrong credentials, cancelled subscribers, cleanup, independent active-VPN DNS and early publication while a silent neighbour is pending.
- ServerRegionMapTests.ps1: PASS, five theme palettes and geographic rendering.
- AppUpdateAndPerformanceTests.ps1: PASS, updater parsing, sampling and real narrow/wide map visibility.
- ReleaseNotesTests.ps1: PASS for 2.6.23 before adding the transfer note.
- ServerConfigTransferTests.ps1: PASS, native server-only JSON round trip, XHTTP/TLS/full JSON preservation, no mutation of export source, duplicate prevention and preservation of existing routes.
- HomeConfigTransferLayoutTests.ps1: PASS, five themes at widths 800/1280 and 140% text size.

Runtime probe configs containing user keys were transient and removed by the owned process cleanup. No user profiles are committed. GitHub release workflow must finish successfully before publication is reported complete. This file does not assert live installation on the user's PC.

Final server toolbar update: removed My VPS and corporate proxy actions, added explicit server config import/export. HomeConfigTransferLayoutTests checks home and server pages in five themes. DesktopControlsUiTests: 576 checks, seven window/theme/mode variants PASS. ServerConfigTransferTests repeated PASS. Rendered servers-dark.png inspected locally.
