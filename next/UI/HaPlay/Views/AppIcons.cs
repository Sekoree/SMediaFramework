using Avalonia.Media;

namespace HaPlay.Views;

/// <summary>
/// The app's monochrome vector icon set (24×24 filled geometries, default even-odd fill).
/// Replaces the emoji glyphs that rendered as tofu boxes on systems without a color-emoji
/// font. Used from XAML as <c>&lt;PathIcon Data="{x:Static views:AppIcons.Play}" /&gt;</c> —
/// PathIcon paints with the control Foreground, so the icons follow the Classic theme's
/// text colors (including HighlightText on selection) for free.
/// </summary>
public static class AppIcons
{
    // ---- transport ----
    public static StreamGeometry Play { get; } = G("M6,4 L20,12 L6,20 Z");
    public static StreamGeometry Pause { get; } = G("M5,4 H10 V20 H5 Z M14,4 H19 V20 H14 Z");
    public static StreamGeometry Stop { get; } = G("M5,5 H19 V19 H5 Z");
    public static StreamGeometry Previous { get; } = G("M5,4 H8 V11 L20,4 V20 L8,13 V20 H5 Z");
    public static StreamGeometry Next { get; } = G("M16,4 H19 V20 H16 V13 L4,20 V4 L16,11 Z");

    // ---- workspaces ----
    public static StreamGeometry Menu { get; } = G("M3,5 H21 V7 H3 Z M3,11 H21 V13 H3 Z M3,17 H21 V19 H3 Z");
    public static StreamGeometry Cue { get; } = G("M12,5 A7,7 0 1 0 12,19 A7,7 0 1 0 12,5 Z");
    public static StreamGeometry Grid { get; } = G("M3,3 H10.5 V10.5 H3 Z M13.5,3 H21 V10.5 H13.5 Z M3,13.5 H10.5 V21 H3 Z M13.5,13.5 H21 V21 H13.5 Z");
    public static StreamGeometry Sliders { get; } = G(
        "M3,5.2 H21 V6.8 H3 Z M6.5,3.2 H10 V8.8 H6.5 Z " +
        "M3,11.2 H21 V12.8 H3 Z M13.5,9.2 H17 V14.8 H13.5 Z " +
        "M3,17.2 H21 V18.8 H3 Z M8.5,15.2 H12 V20.8 H8.5 Z");
    public static StreamGeometry SwapArrows { get; } = G(
        "M4,7.2 H14 V4.6 L20.5,8.4 L14,12.2 V9.6 H4 Z " +
        "M20,16.8 H10 V19.4 L3.5,15.6 L10,11.8 V14.4 H20 Z");
    public static StreamGeometry Folder { get; } = G("M2,5 H9.5 L11.5,7.5 H22 V19 H2 Z");

    // ---- actions ----
    public static StreamGeometry Gear { get; } = G(
        "M10,2 H14 V5.1 A7,7 0 0 1 16.9,6.8 L19.7,5.4 L21.7,8.9 L19.1,10.6 " +
        "A7.2,7.2 0 0 1 19.1,13.4 L21.7,15.1 L19.7,18.6 L16.9,17.2 " +
        "A7,7 0 0 1 14,18.9 V22 H10 V18.9 A7,7 0 0 1 7.1,17.2 L4.3,18.6 L2.3,15.1 L4.9,13.4 " +
        "A7.2,7.2 0 0 1 4.9,10.6 L2.3,8.9 L4.3,5.4 L7.1,6.8 A7,7 0 0 1 10,5.1 Z " +
        "M12,8.6 A3.4,3.4 0 1 0 12,15.4 A3.4,3.4 0 1 0 12,8.6 Z");
    public static StreamGeometry Close { get; } = G(
        "M5,6.4 L6.4,5 L12,10.6 L17.6,5 L19,6.4 L13.4,12 L19,17.6 L17.6,19 L12,13.4 L6.4,19 L5,17.6 L10.6,12 Z");
    public static StreamGeometry Plus { get; } = G("M10,4 H14 V10 H20 V14 H14 V20 H10 V14 H4 V10 H10 Z");
    public static StreamGeometry ArrowUp { get; } = G("M12,4 L20,12 H15 V20 H9 V12 H4 Z");
    public static StreamGeometry ArrowDown { get; } = G("M12,20 L4,12 H9 V4 H15 V12 H20 Z");
    public static StreamGeometry Refresh { get; } = G(
        "M12,3 L18,7.5 L12,12 V9 A5,5 0 1 0 17,14 H20 A8,8 0 1 1 12,6 Z");
    public static StreamGeometry Loop { get; } = G(
        "M7,6.8 H16 V4.2 L21.5,8 L16,11.8 V9.2 H9.4 V12 H7 Z " +
        "M17,17.2 H8 V19.8 L2.5,16 L8,12.2 V14.8 H14.6 V12 H17 Z");
    public static StreamGeometry Shuffle { get; } = G(
        "M3,6.8 H8.2 L10.6,9.8 L9.1,11.7 L7,9.2 H3 Z " +
        "M14.5,6.8 H18 V4.2 L23,8 L18,11.8 V9.2 H15.7 L9.2,17.2 H3 V14.8 H8 Z " +
        "M13.4,14.2 L14.9,12.3 L15.7,14.8 H18 V12.2 L23,16 L18,19.8 V17.2 H14.5 Z");
    public static StreamGeometry Pin { get; } = G(
        "M14,2 L22,10 L19.5,10.8 L15.5,14.8 L15.2,19 L13.5,20.7 L9.6,14.7 L3.7,20.6 L2.3,19.2 L8.2,13.3 L2.2,9.4 L3.9,7.7 L8.1,7.4 L12.1,3.4 Z");
    public static StreamGeometry Warning { get; } = G(
        "M12,2.5 L23,21.5 H1 Z M11,9 H13 V15 H11 Z M11,16.8 H13 V19 H11 Z");
    public static StreamGeometry Info { get; } = G(
        "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z M11,6.6 H13 V8.8 H11 Z M11,10.4 H13 V17.4 H11 Z");
    public static StreamGeometry Blocked { get; } = G(
        "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z M12,4.4 A7.6,7.6 0 0 1 19.6,12 A7.6,7.6 0 0 1 18.2,16.4 L7.6,5.8 A7.6,7.6 0 0 1 12,4.4 Z " +
        "M5.8,7.6 L16.4,18.2 A7.6,7.6 0 0 1 12,19.6 A7.6,7.6 0 0 1 4.4,12 A7.6,7.6 0 0 1 5.8,7.6 Z");
    public static StreamGeometry Duplicate { get; } = G(
        "M8,2 H22 V16 H18 V13.6 H19.6 V4.4 H10.4 V6 H8 Z M2,8 H16 V22 H2 Z M4.4,10.4 V19.6 H13.6 V10.4 Z");
    public static StreamGeometry Edit { get; } = G(
        "M3,17.2 L14.9,5.3 L18.7,9.1 L6.8,21 H3 Z M16.3,3.9 L18.2,2 L22,5.8 L20.1,7.7 Z");
    public static StreamGeometry Eject { get; } = G("M12,4 L20,13 H4 Z M4,15.5 H20 V18.5 H4 Z");
    public static StreamGeometry Lock { get; } = G(
        "M12,2.6 A4.4,4.4 0 0 0 7.6,7 V9.6 H9.7 V7 A2.3,2.3 0 0 1 14.3,7 V9.6 H16.4 V7 A4.4,4.4 0 0 0 12,2.6 Z " +
        "M5.4,9.6 H18.6 V21 H5.4 Z");

    // ---- media kinds ----
    public static StreamGeometry Image { get; } = G(
        "M2,4 H22 V20 H2 Z M4,6 V18 H20 V6 Z M5.2,16.8 L10,10 L13.6,14.8 L16.2,11.6 L19,16.8 Z " +
        "M15.6,7.4 A1.9,1.9 0 1 0 15.6,11.2 A1.9,1.9 0 1 0 15.6,7.4 Z");
    public static StreamGeometry Speech { get; } = G("M3,4 H21 V16 H12 L7,21 V16 H3 Z");
    public static StreamGeometry TextCard { get; } = G(
        "M3,4 H21 V20 H3 Z M5,6 V18 H19 V6 Z M7,8.2 H17 V10 H7 Z M7,11.8 H14 V13.6 H7 Z");
    public static StreamGeometry Person { get; } = G(
        "M12,3 A3.4,3.4 0 1 0 12,9.8 A3.4,3.4 0 1 0 12,3 Z M4.8,21 A7.2,7.2 0 0 1 19.2,21 Z");
    public static StreamGeometry VideoClip { get; } = G("M3,5 H21 V19 H3 Z M9.8,8.6 L16,12 L9.8,15.4 Z");
    public static StreamGeometry Antenna { get; } = G(
        "M12,8 A2.4,2.4 0 1 0 12,12.8 A2.4,2.4 0 1 0 12,8 Z M11,14 H13 V21 H11 Z " +
        "M6.9,5.3 L8.3,6.7 A5.6,5.6 0 0 0 8.3,14.1 L6.9,15.5 A7.6,7.6 0 0 1 6.9,5.3 Z " +
        "M17.1,5.3 A7.6,7.6 0 0 1 17.1,15.5 L15.7,14.1 A5.6,5.6 0 0 0 15.7,6.7 Z " +
        "M4.1,2.5 L5.5,3.9 A9.6,9.6 0 0 0 5.5,16.9 L4.1,18.3 A11.6,11.6 0 0 1 4.1,2.5 Z " +
        "M19.9,2.5 A11.6,11.6 0 0 1 19.9,18.3 L18.5,16.9 A9.6,9.6 0 0 0 18.5,3.9 Z");
    public static StreamGeometry Microphone { get; } = G(
        "M9,3.2 A3,3 0 0 1 15,3.2 V11 A3,3 0 0 1 9,11 Z " +
        "M5,10 H7 A5,5 0 0 0 17,10 H19 A7,7 0 0 1 13,16.92 V19 H16 V21 H8 V19 H11 V16.92 A7,7 0 0 1 5,10 Z");

    private static StreamGeometry G(string pathData) => StreamGeometry.Parse(pathData);
}
