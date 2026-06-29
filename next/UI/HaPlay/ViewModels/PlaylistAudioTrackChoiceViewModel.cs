using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.NDI;
using S.Media.Audio.PortAudio;

namespace HaPlay.ViewModels;

/// <summary>One entry of the playlist "Audio track" context submenu.</summary>
public sealed partial class PlaylistAudioTrackChoiceViewModel : ObservableObject
{
    private readonly MediaPlayerViewModel _owner;

    public PlaylistAudioTrackChoiceViewModel(MediaPlayerViewModel owner, int? index, string label, bool isSelected)
    {
        _owner = owner;
        Index = index;
        Label = isSelected ? $"✓ {label}" : label;
        IsSelected = isSelected;
    }

    public int? Index { get; }

    public string Label { get; }

    public bool IsSelected { get; }

    [RelayCommand]
    private void Select() => _owner.SetSelectedPlaylistItemAudioTrack(Index);
}
