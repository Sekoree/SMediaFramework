# GPU frame & handle ownership / threading (GPU-01)

One reference table for who owns each video buffer/handle, what releases it, and which thread may touch it.
It complements the framework-wide ownership rules in the root [README](../README.md#ownership-threading--real-time-rules).

The unifying rule: **`VideoFrame` is one-shot `IDisposable`.** `Dispose()` is
`Interlocked.Exchange(ref _release, null)?.Dispose()` — atomic and safe to call twice; only the first call
fires the release. A frame may be disposed from any thread, but the *backing resource* a release frees
(pooled memory, dma-buf fds, a D3D/GL object) has its own thread rule, below. A consumer that queues a frame
past the `Submit` call **must** take its own reference (a fan-out view / shared reference) first.

| Kind | What it holds | Owner | Released when | Thread rules |
|---|---|---|---|---|
| **CPU frame** (`VideoFrame.Planes`) | `ReadOnlyMemory<byte>[]` over pooled/native planes | the producing decoder/source | `VideoFrame.Dispose()` runs the `_release` (e.g. returns the buffer to its pool) | Dispose from any thread. Zero-copy fan-out (`TryCreateCpuFanOutViews`) refcounts the backing so N outputs share one buffer; each view disposed independently. |
| **DMA-BUF** (`DmabufNv12/P010/P016Backing`, Linux DRM PRIME) | dma-buf `fds` for the semi-planar surface | the backing (owned by the frame) | last **shared reference** disposed (`CreateNv12DmabufSharedReference`, …) refcounts the fds; they close once every view is gone | The producer keeps the fds valid until all references dispose. Import (`mmap` readback / GL import) happens on the **presenter** thread. A branch CPU converter reads back via `VideoDmabufCpuReadback` (may fail for tiled/protected buffers). |
| **Win32 D3D11 shared NV12** (`Win32SharedNv12Backing`) | a shared-handle NV12 texture + keyed mutex | the producing hardware decode context | last shared reference disposed (refcounted like dma-buf) | The GPU uploader borrows the texture **only under its keyed mutex** (`D3d11TextureKeyedMutexScope`) on the presenter/GL thread. A branch CPU converter is **unsupported** for this backing (use NV12 outputs, a single output, or software decode). |
| **GL texture** (presenter-side upload of a CPU/NV12/shared frame) | a GL texture object (`YuvVideoRenderer`, `Nv12Win32SharedHandleGpuUploader`, `SDL3GLVideoOutput`) | the **presenter** | destroyed by the presenter (frame swap / reconfigure / dispose) | A GL context is single-threaded: the texture is created, sampled, and destroyed **only on the presenter's render thread**. The source `VideoFrame` is disposed after the upload completes. |
| **Compositor surface** (layer surface, e.g. the MMD GL layer) | a GL render target + material state | the **compositor** | released with the composition / on reconfigure | The hosting compositor supplies the GL context; the surface renders on the **compositor thread** only. A managed CPU fallback path exists where no GL context is available. |
| **Presenter** (`IVideoOutput`: SDL3 / Avalonia / NDI) | device / context / swapchain / encoder | whoever registered it (usually `VideoRouter`, which disposes owned outputs) | `Dispose()` (the router or the `VideoOutputPump` that wraps it) | `Configure`/`Submit` run on the router's clock-driven submit thread, or the `VideoOutputPump` drain thread for async outputs, and must return promptly (real-time). Pump `Dispose` joins the drain thread; a stuck inner `Submit` is leaked rather than torn out from under. |

## Consequences

- **Never move a GL/D3D object across threads.** Refcounted shared references exist precisely so the
  *release* of a hardware backing can be deferred to the presenter path instead of firing on the decode
  thread. The router stages pixel conversion **outside its lock** (and on the pump's drain thread) so a slow
  swscale repack never blocks routing or holds a backing lock while a subscriber runs.
- **Fan-out shares one backing.** DRM NV12/P010/P016 and CPU frames fan out to multiple outputs via
  refcounted shared references / views — one decode, N presenters, released once all are done.
- **Real-time first.** Output `Submit` and layer render are on the clock/compositor thread; push slow work
  (network encode, swscale) to a pump/own thread. See the audio/video pump pressure notes (ROUTE-01).
