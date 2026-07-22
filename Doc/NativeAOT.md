# NativeAOT guide

The framework treats NativeAOT as a first-class target, not an afterthought.

## What is enforced

- `IsAotCompatible=true` in `Directory.Build.props` runs the trim/AOT analyzers on **every**
  library at build time, with warnings-as-errors. A library that regresses AOT compatibility
  fails the build, not the publish.
- CI AOT-publishes and RUNS three things per push: the `AotSmoke` tool (including Mond's
  runtime-error/StackFrame path, the exact code the accepted IL2026 baseline covers), the
  `s_media_player` C ABI driven by a pure-C client, and the full HaPlay.Desktop app (launch +
  first-frame smoke).

## Rules when contributing

- **No reflection-based JSON.** Use source-generated `JsonSerializerContext`s (see
  `AppSettingsJsonContext` for the pattern).
- **No `Assembly.Load`/plugin scanning.** Composition is explicit through `MediaRegistry.Build`
  module calls; inbound native plugins go through `S.Abi`'s C ABI instead of managed loading.
- **P/Invoke through `[LibraryImport]`** (source-generated marshalling) with module initializers
  installing `DllImportResolver`s — never runtime-emitted marshalling.
- If a narrow warning is genuinely detail-only, baseline it per-project via
  `WarningsNotAsErrors` with a comment explaining why (the Mond IL2026 baseline is the example);
  blanket suppression is not acceptable.

## Publishing

```bash
# The app deliverable:
dotnet publish UI/HaPlay.Desktop/HaPlay.Desktop.csproj -c Release -r linux-x64

# The framework as a C library (s_media_player.so + header under S.Media.Interop/include):
dotnet publish MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj \
  -c Release -r linux-x64 -p:PublishAot=true
```

Native libraries are resolved at runtime (see [Native-Dependencies.md](Native-Dependencies.md));
AOT publishing does not embed them.
