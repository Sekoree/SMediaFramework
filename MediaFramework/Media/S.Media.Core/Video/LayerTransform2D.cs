namespace S.Media.Core.Video;

/// <summary>
/// 2×3 affine transform mapping a layer's source pixel coordinates into the compositor's
/// destination canvas (also in pixels, top-left origin, Y down).
/// </summary>
/// <remarks>
/// <para>
/// Maps a column vector <c>(sx, sy, 1)</c> to <c>(dx, dy)</c> via
/// <c>dx = M11*sx + M12*sy + Tx</c>, <c>dy = M21*sx + M22*sy + Ty</c>. The same shape as
/// <c>System.Numerics.Matrix3x2</c>; kept as a record struct so it can travel through
/// <see cref="CompositorLayer"/> by value.
/// </para>
/// <para>
/// CPU compositor (<see cref="CpuVideoCompositor"/>) supports any non-degenerate affine via
/// inverse mapping + nearest-neighbor sampling. GL compositors apply the same matrix in a
/// per-draw vertex uniform.
/// </para>
/// </remarks>
public readonly record struct LayerTransform2D(
    float M11, float M12, float Tx,
    float M21, float M22, float Ty)
{
    /// <summary>Identity transform: source pixels map 1:1 to destination starting at (0,0).</summary>
    public static LayerTransform2D Identity { get; } = new(1f, 0f, 0f, 0f, 1f, 0f);

    /// <summary>Pure translation by <paramref name="dx"/>, <paramref name="dy"/> destination pixels.</summary>
    public static LayerTransform2D Translate(float dx, float dy) => new(1f, 0f, dx, 0f, 1f, dy);

    /// <summary>Pure scale around the source origin (0,0).</summary>
    public static LayerTransform2D Scale(float sx, float sy) => new(sx, 0f, 0f, 0f, sy, 0f);

    /// <summary>Pure rotation by <paramref name="radians"/> around the source origin (0,0).</summary>
    public static LayerTransform2D Rotate(float radians)
    {
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        return new(c, -s, 0f, s, c, 0f);
    }

    /// <summary>Apply this transform to a source pixel coordinate.</summary>
    public (float X, float Y) Apply(float sx, float sy) =>
        (M11 * sx + M12 * sy + Tx, M21 * sx + M22 * sy + Ty);

    /// <summary>Returns <c>a ∘ b</c> — apply <paramref name="b"/> first, then <paramref name="a"/>.</summary>
    public static LayerTransform2D Compose(LayerTransform2D a, LayerTransform2D b) => new(
        a.M11 * b.M11 + a.M12 * b.M21,
        a.M11 * b.M12 + a.M12 * b.M22,
        a.M11 * b.Tx + a.M12 * b.Ty + a.Tx,
        a.M21 * b.M11 + a.M22 * b.M21,
        a.M21 * b.M12 + a.M22 * b.M22,
        a.M21 * b.Tx + a.M22 * b.Ty + a.Ty);

    /// <summary>
    /// Returns the affine inverse. Throws when the matrix is singular (degenerate — zero scale on
    /// an axis). Used by <see cref="CpuVideoCompositor"/> for inverse sampling.
    /// </summary>
    public LayerTransform2D Invert()
    {
        var det = M11 * M22 - M12 * M21;
        if (det is 0f or float.NaN)
            throw new InvalidOperationException("LayerTransform2D is singular and cannot be inverted.");
        var inv = 1f / det;
        var a = M22 * inv;
        var b = -M12 * inv;
        var c = -M21 * inv;
        var d = M11 * inv;
        var tx = -(a * Tx + b * Ty);
        var ty = -(c * Tx + d * Ty);
        return new LayerTransform2D(a, b, tx, c, d, ty);
    }
}
