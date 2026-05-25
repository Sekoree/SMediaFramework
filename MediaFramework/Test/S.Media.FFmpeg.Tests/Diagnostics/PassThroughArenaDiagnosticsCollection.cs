using Xunit;

namespace S.Media.FFmpeg.Tests.Diagnostics;

/// <summary>
/// Serializes pass-through arena diagnostic tests that share <see cref="S.Media.FFmpeg.Diagnostics.PassThroughArenaProfiling"/> static counters.
/// </summary>
[CollectionDefinition(nameof(PassThroughArenaDiagnosticsCollection), DisableParallelization = true)]
public sealed class PassThroughArenaDiagnosticsCollection;
