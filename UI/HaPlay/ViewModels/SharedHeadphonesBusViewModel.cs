using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels;

/// <summary>
/// §8.2 cross-player follow-up — UI surrogate for <see cref="HaPlay.Models.SharedHeadphonesBus"/>.
/// Mutable in the Outputs workspace; round-trips through
/// <see cref="OutputManagementViewModel.BuildSharedHeadphonesBusesSnapshot"/>.
/// </summary>
public partial class SharedHeadphonesBusViewModel : ObservableObject
{
    public required Guid Id { get; init; }

    [ObservableProperty]
    private string _label = "Monitor Bus";

    /// <summary>References a <see cref="HaPlay.Models.PortAudioOutputDefinition.Id"/> in the
    /// current project. <c>null</c> means "no PA target chosen yet" — players treat the bus as
    /// unavailable until a target is picked.</summary>
    [ObservableProperty]
    private Guid? _portAudioOutputId;
}
