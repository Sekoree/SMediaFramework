# NDI terminology in MediaFramework

Short glossary for types and names in `S.Media.NDI`. These follow common broadcast / NDI usage rather than generic .NET naming.

| Term | Typical type / API | Meaning |
|------|-------------------|---------|
| **Egress** | `NDIEgressPresentationTimeline`, egress tests | Outbound path: frames and audio leaving this process into an NDI sender (program output). |
| **Ingest** | Ingest clocks, receiver paths | Inbound path: receiving NDI (or similar) into decoders / routers for local playout or processing. |
| **Mux** | `MuxPlayheadClock`, shared demux context | Single timeline shared by multiple elementary streams (e.g. one file’s audio + video), or playhead derived from muxed presentation order. |
| **Fusion** | `NDIMonitorReceiverPumpFusion`, `NDIFusionPlaybackHints` | Correlating NDI Monitor receiver feedback (connections, tally) with local `AudioRouter` / `VideoRouter` pump drop counters for HUDs and optional host policy — not automatic pacing. |
| **Aggregating** | `NDIAggregatingSink` (and related) | Combining multiple logical inputs or branches before one NDI or router output. |
| **Pump** | `VideoSinkPump`, `AudioRouter` sink pumps | Async queue between the clock-driven producer thread and a slower sink; pressure events when the queue drops oldest data. |

For framework-wide playback naming (`MediaContainerSession`, `MediaContainerPlaybackBundle`, `PortAudioPlaybackHost`), see `Doc/MediaFramework-Review-2026-05.md` §1.3 and `Doc/MediaFramework-Checklist-2026-05.md` Phase 1.
