# Pixel formats × OpenGL (MFPlayer MediaFramework)

Rough matrix of **`PixelFormat`** values **`YuvVideoRenderer`** supports via **`GlVideoFormatSupport`** (see also **`YuvVideoRenderer.SupportedPixelFormats`**).

| PixelFormat | Shader(s) | Planes | GL internal (typical plane) | Notes |
|-------------|-----------|--------|------------------------------|-------|
| Bgra32 | bgra.frag | 1 | RGBA8 tex, upload `GL_BGRA` | LE packing note in renderer |
| Rgba32 | rgba.frag | 1 | RGBA8, upload `GL_RGBA` | |
| Argb32 | argb.frag | 1 | RGBA8 CPU order A,R,G,B → shader remap | FFmpeg `ARGB` |
| Abgr32 | abgr.frag | 1 | RGBA8 CPU order A,B,G,R | FFmpeg `ABGR` |
| Rgb24 / Bgr24 | rgb8.frag | 1 | RGB8 | |
| Gray8 | gray.frag | 1 | R8 | `bitScale` 1 |
| Gray16 | gray.frag | 1 | R16 | `bitScale` 1 |
| I420, Yv12, Yuv422P, Yuv444P | yuv_planar.frag | 3 | R8 per plane | YUV offsets + matrix |
| Yuv422P10Le, Yuv420P10Le, Yuv444P10Le | yuv_planar.frag | 3 | R16 per plane | `bitScale` tuned for 10-bit in 16-bit words |
| Yuv420P12Le | yuv_planar.frag | 3 | R16 | `bitScale` for 12-bit in 16-bit words |
| Yuva420p | yuva_planar.frag | 4 | R8 (Y,U,V,A) | Alpha from full-res plane |
| Nv12 / Nv21 | yuv_nv12 / yuv_nv21 | 2 | R8 Y + RG UV/VU | |
| P010 / P016 | yuv_nv12 | 2 | R16 + RG16 | 10-bit scaling on P010 |
| Uyvy / Yuyv | uyvy422 / yuyv422 | 1 | Packed RGBA8 half-width texture | Nearest filtering |

HDR preview knobs live on **`VideoHdrTransfer`** / uniforms `uHdrTransfer`, `uHdrExposure` in planar / packed422 shaders (**`VideoFrame.ColorTransferHint`** steers **`SDL3GLVideoOutput`** when set).
