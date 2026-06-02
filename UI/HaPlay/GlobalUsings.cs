// In HaPlay, "PlaylistItem" always means the app's own model type (the base of FilePlaylistItem /
// NDIInputPlaylistItem / PortAudioInputPlaylistItem). S.Media.Playback also defines a PlaylistItem
// (the MediaPlayerController facade's item); this alias keeps the bare name unambiguous in files that
// import both namespaces. Drop the alias and qualify explicitly if HaPlay ever needs the framework type.
global using PlaylistItem = HaPlay.Models.PlaylistItem;
