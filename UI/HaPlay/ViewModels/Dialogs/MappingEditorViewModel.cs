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
    private readonly Action<CueOutputMapping?> _apply;
    private readonly Func<bool, bool>? _setTestPattern;
    private bool _suppressApply;

    public MappingEditorViewModel(
        string outputName,
        int canvasWidth,
        int canvasHeight,
        CueOutputMapping? initial,
        Action<CueOutputMapping?> apply,
        Func<bool, bool>? setTestPattern = null)
    {
        OutputName = outputName;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        _apply = apply;
        _setTestPattern = setTestPattern;

        _suppressApply = true;
        _mappingEnabled = initial is not null;
        _outputWidth = initial?.OutputWidth;
        _outputHeight = initial?.OutputHeight;
        var seed = initial ?? CueOutputMapping.Identity();
        foreach (var section in seed.Sections)
            Sections.Add(Wrap(MappingSectionViewModel.FromModel(section)));
        SelectedSection = Sections.FirstOrDefault();
        _suppressApply = false;
    }

    public string OutputName { get; }

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

        _suppressApply = true;
        Sections.Clear();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                Sections.Add(Wrap(MappingSectionViewModel.FromModel(new CueOutputMappingSection
                {
                    Name = rows > 1 ? $"R{r + 1} C{c + 1}" : $"Panel {c + 1}",
                    SrcX = (double)c / cols,
                    SrcY = (double)r / rows,
                    SrcWidth = 1.0 / cols,
                    SrcHeight = 1.0 / rows,
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

    /// <summary>The mapping as currently edited — null when disabled.</summary>
    public CueOutputMapping? ToMapping() =>
        !MappingEnabled
            ? null
            : new CueOutputMapping
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
        _apply(ToMapping());
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

    public CueOutputMappingSection ToModel() => new()
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
    };

    public static MappingSectionViewModel FromModel(CueOutputMappingSection model) => new()
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
    };
}
