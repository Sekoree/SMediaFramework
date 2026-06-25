namespace S.Media.Core.Video;

/// <summary>
/// Exact integer ratio. Used for things like 29.97 fps (30000/1001) where
/// a <see cref="double"/> would silently round.
/// </summary>
public readonly record struct Rational(int Numerator, int Denominator)
{
    public static readonly Rational Zero = new(0, 1);

    public double ToDouble() => Denominator == 0 ? 0.0 : (double)Numerator / Denominator;

    public override string ToString() => $"{Numerator}/{Denominator}";
}
