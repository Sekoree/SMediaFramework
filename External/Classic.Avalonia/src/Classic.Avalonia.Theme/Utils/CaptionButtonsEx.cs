using System;
using Avalonia;
using Avalonia.Controls;

namespace Classic.Avalonia.Theme.Utils;

internal class CaptionButtonsEx : CaptionButtons
{
    protected override Type StyleKeyOverride => typeof(CaptionButtons);
    private IDisposable? disposable;

    public override void Attach(Window hostWindow)
    {
        base.Attach(hostWindow);

        // Avalonia 12 exposes the dialog state publicly - the old private-field reflection is gone.
        PseudoClasses.Set(":dialog", hostWindow.IsDialog);

        disposable = hostWindow.GetObservable(Window.CanResizeProperty).Subscribe(x =>
        {
            PseudoClasses.Set(":cantresize", !x);
        });
    }

    public override void Detach()
    {
        disposable?.Dispose();
        disposable = null;
        base.Detach();
    }
}
