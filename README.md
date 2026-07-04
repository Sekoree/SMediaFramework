# Basic (C#) Media Framework & Demo Player App

## Testing
There is currently a basic test build on the Releases page for Windows and Linux to find bugs:<br>
https://github.com/Sekoree/MFPlayer/releases/tag/v1.0.0<br>

## The Framework

Initially this started as a silly way of adding an FFmpeg decoder to [OwnAudioSharp](https://github.com/ModernMube/OwnAudioSharp) which then lead to a hacked together addon that made it play video as well.<br>
Realizing that was a bad idea but liking OwnAudioSharps overall structure this came to be.<br>
<br>
### Core Stuff
Decoding is done via FFmpeg and then fed through various syncing layers to either PortAudio for audio or SDL3, Avalonia or NDI outputs for video (Yes I know NDI audio too).<br>
A bit of mixing functionality for (mostly audio) to route N amount of channel to M amount of outputs.<br>
- See the Media Player and Cue Player of the HaPlayer demo app<br>

### Core Extras
Mainly the composition things. Helpful when just displaying one type of media at a time isnt enough, supporting layers, images, text inculding positioning.<br>
 See Cue Player stuff in the HaPlayer demo app<br>

### Other Extras
Aka. the OSC and MIDI library. Why? I'm forsed to use tablet mixers at work currently, so these are for gluing random MIDI controllers to mixer OSC commands.<br>
Also I love [Mond](https://github.com/Rohansi/Mond), an extremely cool scripting runtime for .Net that does support NativeAOT.<br>
- See the Control parts of the HaPlayer demo app<br>

### HaPlayer
Started out as a quick and dirty way to test playback and all sorts of functions.<br>
The UI and UX is a crime against humanity, but I also havent spent much time at it yet as I still focus on core framework stability.<br>
(most parts were mostly a claude or codex "I need to test this, can you add X")<br>

### Disclaimer
I did use a lot of AI tools for this, mainly to experiemnt to see whats possible (or the usual ffmpeg boilerplate to get stream data etc. or OpenGL shaders).<br>

### Main Dependencies
(I'll probably forget something)<br>
Avalonia (for the UI)<br>
FFmpeg(.AutoGen)<br>
SkiaSharp (inherited from Avalonia, used for text stuff)<br>
SDL3-CS (for video output, so things arent strictly tied to the Avalonia dispatcher)<br>
Mond (for the scripting parts of the "Control" area)<br>
YoutubeExplode (for the YouTube source stuff)<br>
XRAnimator and blender_mmd_tools (to understand how to read model and motion data)<br>
libASS (for fancy subtitles)<br>
Mond (for the scripting parts of the "Control" area)<br>
NDI (to professionally™ send audio and video over the network)<br>

