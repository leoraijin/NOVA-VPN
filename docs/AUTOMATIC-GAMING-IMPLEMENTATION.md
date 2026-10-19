# Automatic gaming mode using the user's servers

Requested outcome: one pick action per installed game, one recommended route, no manual
profile/host/port/protocol controls. Only the user's imported profiles are candidates.
Ordinary routing and Kill Switch remain authoritative. Never disconnect the user's
running VPN to benchmark. Publish completed Windows versions to main and normal releases.

## Implemented in the current working tree

- AutomaticGamingService checks every eligible user profile in batches of at most four.
  Three-round screening is followed by ten-round checks of the shortlist and baseline.
  Winners must answer the same targets reliably. Small gains or worse tails keep the
  current route. A query-protocol response is distinguished from a TCP estimate.
- Direct comparison is opt-in from application policy, not a UI choice: enabled only
  when NOVA is disconnected and Kill Switch is off. The ordinary route can win.
- Game cards expose Pick, then Play. No manual route or measurement controls.
- A pick collects game connections automatically. Authenticated sing-box metadata is
  supplemented by Windows' read-only IPv4/IPv6 TCP owner tables when no TUN is active.
- The first pick can start the game through its launcher. Recent game endpoints are
  cached locally in encrypted application state for 30 minutes, keyed by a network hash.
  Cached targets are remeasured and are identified as previous-server evidence.
- A short-lived Windows ETW UDP observer discovers peers without an active NOVA TUN.
  It filters by the payload PID and exact executable path, retains metadata only and
  stops its own unique session. A durable marker supports orphan cleanup after a crash.
- GameAdapter distinguishes Rust, CS2, Fortnite and War Thunder. Rust checks bounded
  separate query-port candidates (+1/+2 and the observed port), requiring a valid A2S
  response. Recognized local region/cluster settings are hints, not measured locations.
- Recommendation tokens hash transport settings, game file identity, network, baseline,
  engine and Kill Switch policy. A change or five-minute expiry requires a fresh pick.
  Tokens are checked again after validation, before stopping any working baseline core.
- Steam software/shared runtimes are filtered; Epic bootstrap launchers are excluded
  from curated game routing identities. Manual overrides inherit detected launcher data.
- Steam/Epic launch URIs and Riot launch products come from local discovery. Protected
  Riot games are not launched directly by bypassing their client.
- Existing temporary routing, journal recovery and owned-core job isolation are retained.
- Installer shutdown now stops the exact installed UI before its cores, preventing the
  older client's auto-reconnect from immediately relocking sing-box.exe.

## Verification added

- Automatic selection tests: stable route retention, multiple comparable destinations,
  unreliable replies, ninth user profile winning, bounded batches, baseline retention,
  cancellation, protocol confidence, allowed direct baseline and launcher validation.
- UI checks require game cards at three widths and reject manual route controls.
- UI checks also require one Play action for a current result and remove it after a
  server change. Algorithm tests cover transport changes, game updates, expiry and policy.
- Installer regression fixture respawns its core until its owning UI is terminated.
- Existing real sing-box TCP/UDP/A2S, cancellation, process-tree isolation and real-TUN
  CI tests remain applicable; IPv6 TCP transport coverage added.

## Still required before claiming the full plan complete

1. Validate adapters against real game sessions, including first-run behavior for
   Fortnite, War Thunder and Rust. Current TCP targets can be service endpoints and
   local region settings may be absent. TCP estimates are not match ping.
2. Unknown UDP peers are now observable via ETW; arbitrary encrypted protocols still
   cannot be probed universally. Query probing remains limited to compatible endpoints.
3. Exercise endpoint changes, restart/history, selected-game lifetime and launcher
   transitions end-to-end. Cache freshness is not proof that a new match uses that server.
4. Exercise protected processes and concurrent normal reconnects in real game sessions;
   recommendation invalidation and exact-path routing already have synthetic coverage.
5. Run updated installer owner-respawn regression and complete Windows TUN CI on the final
   source, then verify packaged payload, checksums and ordinary release metadata.
6. Verify real matches/anti-cheat and improvement claims on the target games. Synthetic
   transport fixtures do not establish live-game latency or anti-cheat compatibility.
7. Set the new release version and publish the completed source/installer to main and a
   normal GitHub release. No local replacement of the user's running installation without
   an explicitly agreed interruption.

Current limitations must stay visible in product wording and release verification.
No claim of universal exact ping measurement, guaranteed improvement or completed
live-game verification is justified by the current tests.

Native UDP observation and real-TUN isolation passed on GitHub run 39228983963.
Run 39232373560 on 90f992a passed orphan recovery, job isolation and the separate TUN
fixture, but failed in the actual MainWindow/CoreManager flow. On 19 October, the HTTP
fixture was corrected to consume Content-Length rather than transport EOF. A loopback
regression fails with the original reader and passes with the corrected one; truncated
bodies are rejected. The SOCKS fixture now uses async IO and remains responsive under
48 idle clients. Flow logs identify each phase, core state and fixture counters.
The corrected GameFlow passed isolated Windows CI 39251652790 on 81bbf6f:
game and ordinary routes, updated-rule restoration, and invalid-config rejection
without stopping the baseline core all succeeded. A second defect was premature
Connected after TUN recreation; Windows preferred-address readiness plus an exact
process-path loopback nonce probe now gate Connected. A miss is rejected before
any external route, and normal user traffic never uses the probe tuple. Local
IPv6 loopback fails with AccessDenied/10013, so it is explicitly skipped locally after
a failed control connection, but remains mandatory in CI. No local network policy is changed.
Version/installer metadata now target 2.6.0. Publication is explicit workflow_dispatch
with publish=true, refuses an existing release and never replaces published 2.5.0.

Windows endpoint layout references:
- https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable
- https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcp6row_owner_pid
- https://learn.microsoft.com/en-us/windows/win32/etw/udpip
- https://wiki.facepunch.com/rust/creating-a-server
