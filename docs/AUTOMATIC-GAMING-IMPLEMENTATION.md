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
- Installer regression fixture respawns its core until its owning UI is terminated.
- Existing real sing-box TCP/UDP/A2S, cancellation, process-tree isolation and real-TUN
  CI tests remain applicable; IPv6 TCP transport coverage added.

## Still required before claiming the full plan complete

1. Complete and independently test per-game adapters, including region identification
   and cold-start behavior for Fortnite, War Thunder and Rust. Current connections can
   include game service endpoints; TCP estimates are not match ping.
2. Resolve unknown UDP destinations without inventing generic ping support. Outside
   active sing-box, the Windows TCP table cannot identify arbitrary UDP peers. Current
   query probing is limited to compatible Rust/Counter-Strike endpoints.
3. Exercise endpoint changes, restart/history, selected-game lifetime and launcher
   transitions end-to-end. Cache freshness is not proof that a new match uses that server.
4. Strengthen recommendation validity against profile edits/deletion, process replacement,
   network reconnect, protected processes and concurrent normal reconnects.
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

Windows endpoint layout references:
- https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable
- https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcp6row_owner_pid
