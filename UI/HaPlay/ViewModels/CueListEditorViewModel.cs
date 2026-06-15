using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public sealed partial class CueListEditorViewModel : ObservableObject
{
    public CueListEditorViewModel(string name)
    {
        Name = name;
    }

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            Name = Strings.CueListFileNameFallback;
    }

    [ObservableProperty]
    private string? _path;

    public ObservableCollection<CueCompositionViewModel> Compositions { get; } = new();

    public ObservableCollection<CueVideoOutputBindingViewModel> VideoOutputs { get; } = new();

    public ObservableCollection<CueNodeViewModel> Nodes { get; } = new();

    public CueList ToModel() => new()
    {
        Name = Name,
        DefaultTriggerMode = DefaultTriggerMode,
        AutoRenumberOnInsert = AutoRenumberOnInsert,
        Compositions = Compositions.Select(c => c.ToModel()).ToList(),
        VideoOutputs = VideoOutputs.Select(o => o.ToModel()).ToList(),
        Nodes = Nodes.Select(n => n.ToModel()).ToList(),
    };

    [ObservableProperty]
    private CueTriggerMode _defaultTriggerMode = CueTriggerMode.Manual;

    [ObservableProperty]
    private bool _autoRenumberOnInsert;

    public static CueListEditorViewModel FromModel(
        CueList list,
        string? path = null,
        Func<Guid, OutputLineViewModel?>? resolveLine = null)
    {
        var vm = new CueListEditorViewModel(list.Name)
        {
            Path = path,
            DefaultTriggerMode = list.DefaultTriggerMode,
            AutoRenumberOnInsert = list.AutoRenumberOnInsert,
        };
        foreach (var c in list.Compositions)
            vm.Compositions.Add(CueCompositionViewModel.FromModel(c));
        foreach (var o in list.VideoOutputs)
            vm.VideoOutputs.Add(CueVideoOutputBindingViewModel.FromModel(o, resolveLine));
        foreach (var node in list.Nodes)
            vm.Nodes.Add(CueNodeViewModel.FromModel(node, resolveLine));
        return vm;
    }
}

public sealed record PreviewAudioDeviceOption(int? DeviceIndex, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public enum CueNodeDropPlacement
{
    Before,
    Inside,
    After,
}
