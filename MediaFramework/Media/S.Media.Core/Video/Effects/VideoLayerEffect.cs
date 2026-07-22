namespace S.Media.Core.Video.Effects;

/// <summary>
/// One configured instance of a <see cref="VideoLayerEffectDescriptor"/>: the descriptor plus its
/// packed parameter values (declared parameter order). Immutable - build a new instance to change
/// parameters (they're cheap; the expensive shader variant is cached by descriptor chain, not by
/// values, which upload as uniforms every draw).
/// </summary>
public sealed class VideoLayerEffect
{
    private readonly float[] _values;
    private IVideoLayerCpuEffect? _cpuKernel;

    public VideoLayerEffect(VideoLayerEffectDescriptor descriptor, ReadOnlySpan<float> values)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (values.Length != descriptor.TotalComponents)
            throw new ArgumentException(
                $"Effect '{descriptor.Id}' takes {descriptor.TotalComponents} packed floats; got {values.Length}.",
                nameof(values));
        Descriptor = descriptor;
        _values = values.ToArray();
    }

    public VideoLayerEffectDescriptor Descriptor { get; }

    /// <summary>Packed parameter values in declared order.</summary>
    public ReadOnlySpan<float> Values => _values;

    /// <summary>
    /// The CPU fallback kernel, built lazily once (instances are immutable so the kernel is
    /// shareable across frames). Null when the effect is GPU-only. The benign construction race
    /// under concurrent composites is harmless: factories are pure.
    /// </summary>
    public IVideoLayerCpuEffect? CpuKernel =>
        _cpuKernel ??= Descriptor.CpuKernelFactory?.Invoke(_values);
}
