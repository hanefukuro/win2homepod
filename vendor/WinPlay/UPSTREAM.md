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

Local engine changes for Issue #1 (2026-10-18):
- `Raop/AirPlayLatency.cs`, `Raop/RaopSession.cs`: allow experimental 40..2000 ms
  requests, with the existing SETUP and sync mapping and 2 s API fallback retained.
- `Raop/ReceiverLatencyDiagnostics.cs`, `Raop/RaopSession.cs`: snapshot numeric
  latency/arrival-to-render fields from both SETUP replies with scoped paths and
  JSON diagnostics. Receiver units remain unspecified; no output latency is inferred.
- Capture, queue thresholds, sender pacing, and 352-frame packets are unchanged.
