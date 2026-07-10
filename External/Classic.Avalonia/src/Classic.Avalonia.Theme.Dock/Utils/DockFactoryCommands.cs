using System;
using System.Windows.Input;
using Dock.Model.Core;

namespace Classic.Avalonia.Theme.Dock;

/// <summary>
/// Static <see cref="ICommand"/> wrappers for the <see cref="IFactory"/> operations the theme's
/// chrome invokes (float/pin/close/preview). Avalonia 12.1 removed compiled-binding support for
/// methods with a typed parameter (only parameterless or single-<c>object</c> overloads compile),
/// which broke the upstream Dock pattern of <c>Command="{Binding Owner.Factory.CloseDockable}"</c>.
/// Each command takes the <see cref="IDockable"/> as its CommandParameter — the same object those
/// bindings already passed — and resolves the layout's factory from it.
/// </summary>
public static class DockFactoryCommands
{
    public static ICommand Float { get; } = new DockableCommand(static (f, d) => f.FloatDockable(d));
    public static ICommand Close { get; } = new DockableCommand(static (f, d) => f.CloseDockable(d));
    public static ICommand CloseOther { get; } = new DockableCommand(static (f, d) => f.CloseOtherDockables(d));
    public static ICommand CloseAll { get; } = new DockableCommand(static (f, d) => f.CloseAllDockables(d));
    public static ICommand CloseLeft { get; } = new DockableCommand(static (f, d) => f.CloseLeftDockables(d));
    public static ICommand CloseRight { get; } = new DockableCommand(static (f, d) => f.CloseRightDockables(d));
    public static ICommand Pin { get; } = new DockableCommand(static (f, d) => f.PinDockable(d));
    public static ICommand PreviewPinned { get; } = new DockableCommand(static (f, d) => f.PreviewPinnedDockable(d));

    private sealed class DockableCommand(Action<IFactory, IDockable> action) : ICommand
    {
        // Static commands on always-live menu items: executability never changes from the
        // command's side (the chrome gates via IsVisible/IsEnabled bindings, as it always did).
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => ResolveFactory(parameter) is not null;

        public void Execute(object? parameter)
        {
            if (parameter is IDockable dockable && ResolveFactory(dockable) is { } factory)
                action(factory, dockable);
        }

        /// <summary>Owner-first mirrors the old <c>Owner.Factory</c> binding path; the fallback to
        /// the dockable's own factory covers root docks with no owner. Same instance either way —
        /// InitLayout stamps one factory across the layout.</summary>
        private static IFactory? ResolveFactory(object? parameter) =>
            parameter is IDockable dockable ? dockable.Owner?.Factory ?? dockable.Factory : null;
    }
}
