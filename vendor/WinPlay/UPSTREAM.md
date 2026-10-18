# WinPlay protocol engine

- Source: https://github.com/dineshdhotrad/WinPlay
- Commit: 76c130aa7a61ae19f7d40f763ed2181136b83cc2
- License: GPL-3.0-or-later; see LICENSE and THIRD_PARTY_NOTICES.md.
- Only WinPlay.Core is used. Win2HomePod adds a Windows Forms interface,
  all-or-nothing receiver startup, common-source branching, input metering,
  and persistent Windows default-output recovery.

Local engine changes (2026-10-16):
- `Raop/RaopSession.cs`: selectable 100..2000 ms latency applied to SETUP
  and RTP-to-PTP playback mapping; receiver latency diagnostics and public
  retransmit/send-failure counters. Channel routing and pairing are unchanged.
- `Audio/LoopbackAudioSource.cs`: explicit WASAPI endpoint, 20 ms event-driven
  capture, bounded live queue (80 ms maximum before catch-up to 40 ms), and
  queue/drop/underrun/callback-gap telemetry.

Local engine changes for Issue #2 (2026-10-18):
- `Raop/StreamProbeOptions.cs`, `Raop/StreamProbeReport.cs`, `Raop/ReceiverCapabilitySnapshot.cs`:
  explicit experimental SETUP declarations, scoped response evidence and allowlisted capability snapshots.
- `Raop/RaopSession.cs`: probe-only entry point reusing the normal handshake, with per-phase status
  observations and guaranteed teardown without starting audio. Normal GUI declarations remain unchanged.
- `WinPlay.Core.csproj`: friend access for the experimental probe CLI.
