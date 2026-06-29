using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// Editor for one binding's <see cref="CueOutputMapping"/> (warp sections). Every model-affecting
/// change immediately invokes the apply callback, which persists the mapping into the binding VM
/// and live-applies it to a running composition — calibration is done against live output.
/// See Doc/HaPlay-Output-Mapping-Plan.md.
/// </summary>
public sealed partial class MappingEditorViewModel : ObservableObject
{
    private readonly Action<CueOutputMapping?, bool> _apply;
    private readonly Func<bool, bool>? _setTestPattern;
    private bool _suppressApply;

    /// <param name="apply">Persist + live-apply callback: (edited geometry, enabled). The geometry is
    /// always supplied (even when disabled) so the caller can retain it for a later re-enable.</param>
    /// <param name="initialEnabled">Whether mapping is currently active for this binding.</param>
    public MappingEditorViewModel(
        string outputName,
        int canvasWidth,
        int canvasHeight,
        CueOutputMapping? initial,
        Action<CueOutputMapping?, bool> apply,
        Func<bool, bool>? setTestPattern = null,
        CueOutputMapping? disabledSeed = null,
        bool initialEnabled = false,
        string? dialogTitlePrefix = null,
        string? enableLabel = null,
        string? sizeLabel = null,
        string? testPatternLabel = null,
        bool canEditOutputSize = true)
    {
        OutputName = outputName;
        DialogTitle = $"{(string.IsNullOrWhiteSpace(dialogTitlePrefix) ? "Output mapping" : dialogTitlePrefix)} — {outputName}";
        EnableLabel = string.IsNullOrWhiteSpace(enableLabel) ? "Enable mapping" : enableLabel;
        SizeLabel = string.IsNullOrWhiteSpace(sizeLabel) ? "Output size" : sizeLabel;
        TestPatternLabel = string.IsNullOrWhiteSpace(testPatternLabel)
            ? "Show calibration grid on output"
            : testPatternLabel;
        CanEditOutputSize = canEditOutputSize;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        _apply = apply;
        _setTestPattern = setTestPattern;

        _suppressApply = true;
        _mappingEnabled = initialEnabled;
        var seed = initial ?? disabledSeed ?? CueOutputMapping.Identity();
        _outputWidth = seed.OutputWidth;
        _outputHeight = seed.OutputHeight;
        foreach (var section in seed.Sections)
            Sections.Add(Wrap(MappingSectionViewModel.FromModel(section)));
        SelectedSection = Sections.FirstOrDefault();
        _suppressApply = false;
    }

    public string OutputName { get; }

    public string DialogTitle { get; }

    public string EnableLabel { get; }

    public string SizeLabel { get; }

    public string TestPatternLabel { get; }

    public bool CanEditOutputSize { get; }

    public bool CanShowTestPattern => _setTestPattern is not null;

    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    public int EffectiveOutputWidth => OutputWidth ?? CanvasWidth;

    public int EffectiveOutputHeight => OutputHeight ?? CanvasHeight;

    public ObservableCollection<MappingSectionViewModel> Sections { get; } = new();

    /// <summary>Raised after any change that altered the mapping (the preview re-renders on it).</summary>
    public event Action? MappingChanged;

    /// <summary>Off = the binding has no mapping (raw canvas, zero cost). The section list stays
    /// editable so toggling on restores the layout.</summary>
    [ObservableProperty]
    private bool _mappingEnabled;

    [ObservableProperty]
    private MappingSectionViewModel? _selectedSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveOutputWidth))]
    private int? _outputWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveOutputHeight))]
    private int? _outputHeight;

    [ObservableProperty]
    private int _splitColumns = 3;

    [ObservableProperty]
    private int _splitRows = 1;

    [ObservableProperty]
    private bool _showTestPattern;

    partial void OnMappingEnabledChanged(bool value) => Apply();

    partial void OnOutputWidthChanged(int? value) => Apply();

    partial void OnOutputHeightChanged(int? value) => Apply();

    partial void OnShowTestPatternChanged(bool value)
    {
        if (_setTestPattern?.Invoke(value) == false)
            ShowTestPattern = false; // composition not available (no list/binding) — snap back
    }

    [RelayCommand]
    private void AddSection()
    {
        var vm = Wrap(MappingSectionViewModel.FromModel(CueOutputMappingSection.FullCanvas() with
        {
            Name = $"Section {Sections.Count + 1}",
        }));
        Sections.Add(vm);
        SelectedSection = vm;
        Apply();
    }

    [RelayCommand]
    private void DuplicateSection()
    {
        if (SelectedSection is not { } source)
            return;
        var copy = Wrap(MappingSectionViewModel.FromModel(source.ToModel() with
        {
            Id = Guid.NewGuid(),
            Name = source.Name + " copy",
        }));
        Sections.Insert(Sections.IndexOf(source) + 1, copy);
        SelectedSection = copy;
        Apply();
    }

    [RelayCommand]
    private void RemoveSection()
    {
        if (SelectedSection is not { } section)
            return;
        var idx = Sections.IndexOf(section);
        Sections.Remove(section);
        SelectedSection = Sections.Count > 0 ? Sections[Math.Min(idx, Sections.Count - 1)] : null;
        Apply();
    }

    [RelayCommand]
    private void MoveSectionUp() => MoveSelected(-1);

    [RelayCommand]
    private void MoveSectionDown() => MoveSelected(+1);

    /// <summary>Replaces all sections with an even SplitColumns×SplitRows grid (identity placement
    /// in output space) — the fast path for multi-panel surfaces; the operator then nudges panels.</summary>
    [RelayCommand]
    private void SplitIntoGrid()
    {
        var cols = Math.Clamp(SplitColumns, 1, 64);
        var rows = Math.Clamp(SplitRows, 1, 64);
        var sourceBounds = CurrentSourceBounds();

        _suppressApply = true;
        Sections.Clear();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                Sections.Add(Wrap(MappingSectionViewModel.FromModel(new CueOutputMappingSection
                {
                    Name = rows > 1 ? $"R{r + 1} C{c + 1}" : $"Panel {c + 1}",
                    SrcX = sourceBounds.X + sourceBounds.Width * c / cols,
                    SrcY = sourceBounds.Y + sourceBounds.Height * r / rows,
                    SrcWidth = sourceBounds.Width / cols,
                    SrcHeight = sourceBounds.Height / rows,
                    DestX = (double)c / cols * EffectiveOutputWidth,
                    DestY = (double)r / rows * EffectiveOutputHeight,
                    DestWidth = (double)EffectiveOutputWidth / cols,
                    DestHeight = (double)EffectiveOutputHeight / rows,
                })));
            }
        }
        SelectedSection = Sections.FirstOrDefault();
        _suppressApply = false;
        Apply();
    }

    private (double X, double Y, double Width, double Height) CurrentSourceBounds()
    {
        if (Sections.Count == 0)
            return (0, 0, 1, 1);

        var minX = 1.0;
        var minY = 1.0;
        var maxX = 0.0;
        var maxY = 0.0;
        foreach (var section in Sections)
        {
            if (!section.Enabled || section.SrcWidth <= 0 || section.SrcHeight <= 0)
                continue;
            var x0 = Math.Clamp(section.SrcX, 0.0, 1.0);
            var y0 = Math.Clamp(section.SrcY, 0.0, 1.0);
            var x1 = Math.Clamp(section.SrcX + section.SrcWidth, 0.0, 1.0);
            var y1 = Math.Clamp(section.SrcY + section.SrcHeight, 0.0, 1.0);
            if (x1 <= x0 || y1 <= y0)
                continue;
            minX = Math.Min(minX, x0);
            minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1);
            maxY = Math.Max(maxY, y1);
        }

        return maxX > minX && maxY > minY
            ? (minX, minY, maxX - minX, maxY - minY)
            : (0, 0, 1, 1);
    }

    [RelayCommand]
    private void ResetToIdentity()
    {
        _suppressApply = true;
        Sections.Clear();
        Sections.Add(Wrap(MappingSectionViewModel.FromModel(CueOutputMappingSection.FullCanvas())));
        SelectedSection = Sections[0];
        OutputWidth = null;
        OutputHeight = null;
        _suppressApply = false;
        Apply();
    }

    /// <summary>The mapping geometry as currently edited (independent of <see cref="MappingEnabled"/>),
    /// so the caller can retain it while disabled and restore it on re-enable.</summary>
    public CueOutputMapping ToMapping() =>
        new()
        {
            Sections = Sections.Select(s => s.ToModel()).ToList(),
            OutputWidth = OutputWidth,
            OutputHeight = OutputHeight,
        };

    /// <summary>Persist + live-apply. Called by section VMs on every field change.</summary>
    internal void Apply()
    {
        if (_suppressApply)
            return;
        _apply(ToMapping(), MappingEnabled);
        if (ShowTestPattern && _setTestPattern?.Invoke(true) == false)
            ShowTestPattern = false;
        MappingChanged?.Invoke();
    }

    /// <summary>Turns the grid off when the dialog closes (the slot would otherwise hold the
    /// composition's outputs open).</summary>
    public void OnEditorClosed()
    {
        if (ShowTestPattern)
            ShowTestPattern = false;
    }

    private MappingSectionViewModel Wrap(MappingSectionViewModel section)
    {
        section.Owner = this;
        return section;
    }

    private void MoveSelected(int delta)
    {
        if (SelectedSection is not { } section)
            return;
        var idx = Sections.IndexOf(section);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= Sections.Count)
            return;
        Sections.Move(idx, target);
        Apply();
    }
}

/// <summary>One editable mapping section. Field changes notify the owning editor immediately.</summary>
public sealed partial class MappingSectionViewModel : ObservableObject
{
    internal MappingEditorViewModel? Owner { get; set; }

    public Guid Id { get; private init; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private double _srcX;

    [ObservableProperty]
    private double _srcY;

    [ObservableProperty]
    private double _srcWidth = 1.0;

    [ObservableProperty]
    private double _srcHeight = 1.0;

    [ObservableProperty]
    private double _destX;

    [ObservableProperty]
    private double _destY;

    [ObservableProperty]
    private double _destWidth;

    [ObservableProperty]
    private double _destHeight;

    [ObservableProperty]
    private double _rotationDegrees;

    [ObservableProperty]
    private double _opacity = 1.0;

    [ObservableProperty]
    private double _brightness = 1.0;

    /// <summary>Mesh warp (Phase 4): the grid is kept even while disabled so toggling the checkbox
    /// is non-destructive within the dialog session; only an enabled mesh is persisted.</summary>
    [ObservableProperty]
    private bool _meshEnabled;

    [ObservableProperty]
    private int _meshColumns = 4;

    [ObservableProperty]
    private int _meshRows = 4;

    private List<CuePoint> _meshPoints = new();

    /// <summary>Row-major control points, normalized dest-rect space (see the model docs).</summary>
    public IReadOnlyList<CuePoint> MeshPoints => _meshPoints;

    /// <summary>Moves one control point (editor drag). Coordinates are normalized dest-rect space;
    /// out-of-[0,1] values are legal (overshooting the rect).</summary>
    public void SetMeshPoint(int index, double x, double y)
    {
        if (index < 0 || index >= _meshPoints.Count)
            return;
        _meshPoints[index] = new CuePoint(Math.Round(x, 4), Math.Round(y, 4));
        Owner?.Apply();
    }

    [RelayCommand]
    private void ResetMesh()
    {
        _meshPoints = CueOutputMappingSection.IdentityMeshPoints(ClampedMeshColumns, ClampedMeshRows);
        if (MeshEnabled)
            Owner?.Apply();
    }

    private int ClampedMeshColumns => Math.Clamp(MeshColumns, 2, 16);

    private int ClampedMeshRows => Math.Clamp(MeshRows, 2, 16);

    private void EnsureMeshPointsShape()
    {
        if (_meshPoints.Count != ClampedMeshColumns * ClampedMeshRows)
            _meshPoints = CueOutputMappingSection.IdentityMeshPoints(ClampedMeshColumns, ClampedMeshRows);
    }

    partial void OnMeshEnabledChanged(bool value)
    {
        if (Owner is null)
            return; // model load — points are restored separately by FromModel
        if (value)
            EnsureMeshPointsShape();
        Owner.Apply();
    }

    partial void OnMeshColumnsChanged(int value) => OnMeshGridSizeChanged();

    partial void OnMeshRowsChanged(int value) => OnMeshGridSizeChanged();

    /// <summary>Grid resize restarts from identity (no warp resampling in v1 — resizing is a
    /// set-up-time action, not a calibration tweak).</summary>
    private void OnMeshGridSizeChanged()
    {
        if (Owner is null)
            return;
        _meshPoints = CueOutputMappingSection.IdentityMeshPoints(ClampedMeshColumns, ClampedMeshRows);
        if (MeshEnabled)
            Owner.Apply();
    }

    partial void OnNameChanged(string value) => Owner?.Apply();
    partial void OnEnabledChanged(bool value) => Owner?.Apply();
    partial void OnSrcXChanged(double value) => Owner?.Apply();
    partial void OnSrcYChanged(double value) => Owner?.Apply();
    partial void OnSrcWidthChanged(double value) => Owner?.Apply();
    partial void OnSrcHeightChanged(double value) => Owner?.Apply();
    partial void OnDestXChanged(double value) => Owner?.Apply();
    partial void OnDestYChanged(double value) => Owner?.Apply();
    partial void OnDestWidthChanged(double value) => Owner?.Apply();
    partial void OnDestHeightChanged(double value) => Owner?.Apply();
    partial void OnRotationDegreesChanged(double value) => Owner?.Apply();
    partial void OnOpacityChanged(double value) => Owner?.Apply();
    partial void OnBrightnessChanged(double value) => Owner?.Apply();

    public CueOutputMappingSection ToModel()
    {
        if (MeshEnabled)
            EnsureMeshPointsShape();
        return new CueOutputMappingSection
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            SrcX = SrcX,
            SrcY = SrcY,
            SrcWidth = SrcWidth,
            SrcHeight = SrcHeight,
            DestX = DestX,
            DestY = DestY,
            DestWidth = DestWidth,
            DestHeight = DestHeight,
            RotationDegrees = RotationDegrees,
            Opacity = Opacity,
            Brightness = Brightness,
            MeshColumns = MeshEnabled ? ClampedMeshColumns : 0,
            MeshRows = MeshEnabled ? ClampedMeshRows : 0,
            MeshPoints = MeshEnabled ? new List<CuePoint>(_meshPoints) : null,
        };
    }

    public static MappingSectionViewModel FromModel(CueOutputMappingSection model)
    {
        var hasMesh = model is { MeshColumns: >= 2, MeshRows: >= 2 }
                      && model.MeshPoints is { } mp
                      && mp.Count == model.MeshColumns * model.MeshRows;
        var vm = new MappingSectionViewModel
        {
            Id = model.Id,
            Name = model.Name,
            Enabled = model.Enabled,
            SrcX = model.SrcX,
            SrcY = model.SrcY,
            SrcWidth = model.SrcWidth,
            SrcHeight = model.SrcHeight,
            DestX = model.DestX,
            DestY = model.DestY,
            DestWidth = model.DestWidth,
            DestHeight = model.DestHeight,
            RotationDegrees = model.RotationDegrees,
            Opacity = model.Opacity,
            Brightness = model.Brightness,
            MeshEnabled = hasMesh,
            MeshColumns = hasMesh ? model.MeshColumns : 4,
            MeshRows = hasMesh ? model.MeshRows : 4,
        };
        vm._meshPoints = hasMesh ? new List<CuePoint>(model.MeshPoints!) : new List<CuePoint>();
        return vm;
    }
}
