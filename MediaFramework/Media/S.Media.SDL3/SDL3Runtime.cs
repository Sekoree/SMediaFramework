namespace S.Media.SDL3;

/// <summary>
/// Reference-counted lifetime for the SDL3 video subsystem. Each
/// <see cref="SDL3VideoOutput"/> calls <see cref="Acquire"/> when its render
/// thread starts and <see cref="Release"/> on dispose; <c>SDL_QuitSubSystem</c>
/// only runs when the last holder lets go.
/// </summary>
/// <remarks>
/// Threading: SDL's docs recommend initializing video on the main thread on
/// macOS (window/event handling there is pinned to it). On Linux/Windows the
/// init thread is flexible — the SDL3VideoOutput runs all of its calls on its
/// own dedicated render thread, which is fine outside of macOS. macOS support
/// will require an external pump-on-main-thread harness; not implemented yet.
/// </remarks>
public static class SDL3Runtime
{
    private static readonly Lock Gate = new();
    private static int _refCount;

    /// <summary>Initialise the SDL video subsystem (idempotent, ref-counted).</summary>
    public static void Acquire()
    {
        lock (Gate)
        {
            if (_refCount == 0)
            {
                // Ask the WM to activate the window when SDL_RaiseWindow is used (helps Linux/Wayland + smoke tools).
                SDL.SetHint(SDL.Hints.WindowActivateWhenRaised, "1");
                if (!SDL.Init(SDL.InitFlags.Video))
                    throw new InvalidOperationException(
                        $"SDL_Init(VIDEO) failed: {SDL.GetError()}");
            }
            _refCount++;
        }
    }

    /// <summary>Release one ref; tear down SDL when the count hits zero.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (_refCount == 0) return;
            _refCount--;
            if (_refCount == 0) SDL.Quit();
        }
    }
}
