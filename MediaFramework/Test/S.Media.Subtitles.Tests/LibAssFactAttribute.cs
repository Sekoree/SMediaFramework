using LibAssLib;
using Xunit;

namespace S.Media.Subtitles.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <em>skips</em> (rather than fails) when the native libass cannot be loaded —
/// e.g. a CI runner without the package. The probe runs once and is cached in <see cref="AssLibrary.IsAvailable"/>.
/// </summary>
public sealed class LibAssFactAttribute : FactAttribute
{
    public LibAssFactAttribute()
    {
        if (!AssLibrary.IsAvailable)
            Skip = "libass native not provisioned on this runner";
    }
}
