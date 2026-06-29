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

public enum CueNodeKind
{
    Group,
    Media,
    Action,
    Comment,
}

public enum CueRowStatus
{
    Idle,
    Standby,
    Current,
}

public enum CueMidiCommandType
{
    NRPN,
    RPN,
    NoteOff,
    NoteOn,
    PolyphonicAftertouch,
    ControlChange,
    HighResolutionControlChange,
    ProgramChange,
    ChannelAftertouch,
    PitchBend,
    SysEx,
    MIDITimeCode,
    SongPosition,
    SongSelect,
    TuneRequest,
    TimingClock,
    Start,
    Continue,
    Stop,
    ActiveSensing,
    Reset,
}
