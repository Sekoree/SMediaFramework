using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Numerics;
using HaPlay.Models;
using HaPlay.Resources;
using S.Media.Source.MMD;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// Add/edit dialog for an MMD scene with the RUDIMENTARY 3D camera-placement preview (Gate-6): pick a
/// PMX model + optional motion/camera VMDs, then frame the shot with the manual camera controls while a
/// software-rendered preview updates live. The preview renders on a worker at a debounced cadence — the
/// same renderer playback uses, so what you frame here is what the composition gets.
/// </summary>
public sealed partial class AddMmdDialogViewModel : ObservableObject
{
    private MmdPlaylistItem? _existing;
    private CancellationTokenSource? _renderCts;
    private int _renderScheduled;

    public string DialogTitle => _existing is null ? Strings.AddMmdDialogTitle : Strings.EditMmdDialogTitle;

    [ObservableProperty] private string _modelPath = string.Empty;
    [ObservableProperty] private string _motionPath = string.Empty;
    [ObservableProperty] private string _cameraMotionPath = string.Empty;
    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] private double _cameraDistance = -45;
    [ObservableProperty] private double _cameraTargetX;
    [ObservableProperty] private double _cameraTargetY = 10;
    [ObservableProperty] private double _cameraTargetZ;
    // Direct camera-EYE placement (operator request): kept in two-way sync with the orbit fields
    // (distance/rotation/target) — the persisted item stays in MMD's orbit form, so nothing downstream
    // changes. Position = target + orbitBack(rotation)·|distance|.
    [ObservableProperty] private double _cameraPositionX;
    [ObservableProperty] private double _cameraPositionY = 10;
    [ObservableProperty] private double _cameraPositionZ = 45;
    private bool _syncingCamera;
    [ObservableProperty] private double _cameraRotationXDeg;
    [ObservableProperty] private double _cameraRotationYDeg;
    [ObservableProperty] private double _cameraRotationZDeg;
    [ObservableProperty] private double _cameraFovDeg = 30;
    [ObservableProperty] private double _previewTimeSeconds;
    [ObservableProperty] private double _previewDurationSeconds = 10;
    [ObservableProperty] private bool _renderAntialias = true;
    [ObservableProperty] private bool _renderPhysics = true;
    [ObservableProperty] private int _renderWidth = 1280;
    [ObservableProperty] private int _renderHeight = 720;
    [ObservableProperty] private WriteableBitmap? _previewImage;
    [ObservableProperty] private bool _isRendering;

    /// <summary>Manual camera controls apply only when no camera VMD drives the shot.</summary>
    public bool ManualCameraActive => string.IsNullOrWhiteSpace(CameraMotionPath);

    public void LoadFromExisting(MmdPlaylistItem item)
    {
        _existing = item;
        ModelPath = item.ModelPath;
        MotionPath = item.MotionPath ?? string.Empty;
        CameraMotionPath = item.CameraMotionPath ?? string.Empty;
        CameraDistance = item.CameraDistance;
        CameraTargetX = item.CameraTargetX;
        CameraTargetY = item.CameraTargetY;
        CameraTargetZ = item.CameraTargetZ;
        CameraRotationXDeg = item.CameraRotationXDeg;
        CameraRotationYDeg = item.CameraRotationYDeg;
        CameraRotationZDeg = item.CameraRotationZDeg;
        CameraFovDeg = item.CameraFovDeg;
        RenderAntialias = item.Antialias;
        RenderPhysics = item.Physics;
        RenderWidth = item.RenderWidth;
        RenderHeight = item.RenderHeight;
        OnPropertyChanged(nameof(DialogTitle));
    }

    /// <summary>Builds the resulting item, or null (with a validation message) when the model is missing.</summary>
    public MmdPlaylistItem? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(ModelPath) || !File.Exists(ModelPath))
        {
            ValidationMessage = Strings.MmdModelPathRequired;
            return null;
        }

        return new MmdPlaylistItem(ModelPath)
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            MotionPath = string.IsNullOrWhiteSpace(MotionPath) ? null : MotionPath,
            CameraMotionPath = string.IsNullOrWhiteSpace(CameraMotionPath) ? null : CameraMotionPath,
            RenderWidth = Math.Clamp(RenderWidth, 16, 7680),
            RenderHeight = Math.Clamp(RenderHeight, 16, 4320),
            CameraDistance = CameraDistance,
            CameraTargetX = CameraTargetX,
            CameraTargetY = CameraTargetY,
            CameraTargetZ = CameraTargetZ,
            CameraRotationXDeg = CameraRotationXDeg,
            CameraRotationYDeg = CameraRotationYDeg,
            CameraRotationZDeg = CameraRotationZDeg,
            CameraFovDeg = CameraFovDeg,
            Antialias = RenderAntialias,
            Physics = RenderPhysics,
        };
    }

    // Re-render the preview when anything that affects the frame changes (coalesced on the worker).
    partial void OnModelPathChanged(string value) => ScheduleRender();
    partial void OnMotionPathChanged(string value) => ScheduleRender();
    partial void OnCameraMotionPathChanged(string value)
    {
        OnPropertyChanged(nameof(ManualCameraActive));
        ScheduleRender();
    }

    partial void OnCameraDistanceChanged(double value) { SyncPositionFromOrbit(); ScheduleRender(); }
    partial void OnCameraTargetXChanged(double value) { SyncPositionFromOrbit(); ScheduleRender(); }
    partial void OnCameraTargetYChanged(double value) { SyncPositionFromOrbit(); ScheduleRender(); }
    partial void OnCameraTargetZChanged(double value) { SyncPositionFromOrbit(); ScheduleRender(); }
    partial void OnCameraRotationXDegChanged(double value) { SyncPositionFromOrbit(); ScheduleRender(); }
    partial void OnCameraRotationYDegChanged(double value) { SyncPositionFromOrbit(); ScheduleRender(); }
    partial void OnCameraRotationZDegChanged(double value) { SyncPositionFromOrbit(); ScheduleRender(); }
    partial void OnCameraFovDegChanged(double value) => ScheduleRender();
    partial void OnPreviewTimeSecondsChanged(double value) => ScheduleRender();
    partial void OnCameraPositionXChanged(double value) { SyncOrbitFromPosition(); ScheduleRender(); }
    partial void OnCameraPositionYChanged(double value) { SyncOrbitFromPosition(); ScheduleRender(); }
    partial void OnCameraPositionZChanged(double value) { SyncOrbitFromPosition(); ScheduleRender(); }

    /// <summary>Orbit → eye-position: position = target + back(rotation)·|distance| — the exact vector the
    /// renderer places the camera at (roll does not move the eye, only the horizon).</summary>
    private void SyncPositionFromOrbit()
    {
        if (_syncingCamera) return;
        _syncingCamera = true;
        try
        {
            var rotation =
                Matrix4x4.CreateRotationY((float)(CameraRotationYDeg * Math.PI / 180.0)) *
                Matrix4x4.CreateRotationX((float)(-CameraRotationXDeg * Math.PI / 180.0)) *
                Matrix4x4.CreateRotationZ((float)(-CameraRotationZDeg * Math.PI / 180.0));
            var back = Vector3.TransformNormal(new Vector3(0, 0, 1), rotation);
            var eye = new Vector3((float)CameraTargetX, (float)CameraTargetY, (float)CameraTargetZ)
                      + back * MathF.Abs((float)CameraDistance);
            CameraPositionX = Math.Round(eye.X, 2);
            CameraPositionY = Math.Round(eye.Y, 2);
            CameraPositionZ = Math.Round(eye.Z, 2);
        }
        finally
        {
            _syncingCamera = false;
        }
    }

    /// <summary>Eye-position → orbit: distance = |eye − target|, pitch = atan2(dy, dz),
    /// yaw = atan2(dx, √(dy² + dz²)) — the closed-form inverse of the renderer's
    /// back = (sin yaw, cos yaw·sin pitch, cos yaw·cos pitch). Roll is left untouched.</summary>
    private void SyncOrbitFromPosition()
    {
        if (_syncingCamera) return;
        _syncingCamera = true;
        try
        {
            var d = new Vector3(
                (float)(CameraPositionX - CameraTargetX),
                (float)(CameraPositionY - CameraTargetY),
                (float)(CameraPositionZ - CameraTargetZ));
            var distance = d.Length();
            if (distance < 0.01f)
                return; // eye on the target — orbit undefined, keep the current fields
            var pitch = Math.Atan2(d.Y, d.Z);
            var yaw = Math.Atan2(d.X, Math.Sqrt((double)d.Y * d.Y + (double)d.Z * d.Z));
            CameraDistance = -Math.Round(distance, 2);
            CameraRotationXDeg = Math.Round(pitch * 180.0 / Math.PI, 1);
            CameraRotationYDeg = Math.Round(yaw * 180.0 / Math.PI, 1);
        }
        finally
        {
            _syncingCamera = false;
        }
    }

    [RelayCommand]
    private void RefreshPreview() => ScheduleRender();

    /// <summary>Coalesced worker render: at most one in flight; the newest state wins.</summary>
    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderScheduled, 1) == 1)
            return;
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;
        _ = Task.Run(async () =>
        {
            // Small debounce so slider drags coalesce instead of rendering every tick.
            await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _renderScheduled, 0);
            if (ct.IsCancellationRequested)
                return;
            await RenderPreviewAsync(ct).ConfigureAwait(false);
        });
    }

    private async Task RenderPreviewAsync(CancellationToken ct)
    {
        var model = ModelPath;
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            return;

        const int previewWidth = 480;
        const int previewHeight = 270;
        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsRendering = true);
            var request = new MmdSourceRequest(
                model,
                string.IsNullOrWhiteSpace(MotionPath) ? null : MotionPath,
                string.IsNullOrWhiteSpace(CameraMotionPath) ? null : CameraMotionPath,
                previewWidth, previewHeight,
                (float)CameraDistance,
                new System.Numerics.Vector3((float)CameraTargetX, (float)CameraTargetY, (float)CameraTargetZ),
                new System.Numerics.Vector3((float)CameraRotationXDeg, (float)CameraRotationYDeg, (float)CameraRotationZDeg),
                (float)CameraFovDeg);

            // A fresh source per render keeps this dead simple; model parse dominates and stays in the
            // OS file cache. Fine for a placement dialog — playback uses a long-lived source.
            using var source = new MmdVideoSource(request);
            var duration = source.Duration.TotalSeconds;
            source.Seek(TimeSpan.FromSeconds(Math.Clamp(PreviewTimeSeconds, 0, Math.Max(0, duration - 0.1))));
            if (!source.TryReadNextFrame(out var frame) || ct.IsCancellationRequested)
                return;
            using (frame)
            {
                var pixels = frame.Planes[0].ToArray();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var bitmap = new WriteableBitmap(
                        new PixelSize(previewWidth, previewHeight), new Avalonia.Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    using (var locked = bitmap.Lock())
                        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
                    PreviewImage = bitmap;
                    PreviewDurationSeconds = Math.Max(1, duration);
                    ValidationMessage = null;
                });
            }
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                ValidationMessage = Strings.Format(nameof(Strings.MmdPreviewFailedFormat), ex.Message));
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsRendering = false);
        }
    }

    public void CancelPending() => _renderCts?.Cancel();
}
