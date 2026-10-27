namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>
    /// How the pins shared on cartography tables are drawn, in the app and on the web page alike: an SVG path on a
    /// 16-unit square centred on the pin (y down), filled and outlined in ink, and crossed out in red when checked.
    /// </summary>
    public static class WorldMapPinShapes
    {
        /// <summary>The cross drawn over a pin that is crossed out in game.</summary>
        public const string CheckedPath = "M-6 -6L6 6M6 -6L-6 6";

        public static string Path(WorldMapPinIcon icon) => icon switch
        {
            WorldMapPinIcon.Fire => "M0 -7C3 -4 5 -1 5 2.5A5 4.5 0 0 1 -5 2.5C-5 0 -3.5 -1.5 -2.5 -3.5C-1.5 -1.5 -0.5 -1.5 0 -7Z",
            WorldMapPinIcon.Home => "M-6.5 0L0 -6.5L6.5 0H4.5V6H-4.5V0Z",
            WorldMapPinIcon.Hammer => "M-6.5 -6H4.5V-1.5H-6.5Z M-1.3 -1.5H1.3V7H-1.3Z",
            WorldMapPinIcon.Rune => "M0 -7L5.5 0L0 7L-5.5 0Z",
            WorldMapPinIcon.Boss => "M-5 1A5 5 0 1 0 5 1A5 5 0 1 0 -5 1Z M-4.5 -2L-7 -8L-1.5 -4Z M4.5 -2L7 -8L1.5 -4Z",
            WorldMapPinIcon.Quest => "M0 -7L1.8 -2.4L6.7 -2.2L2.9 0.9L4.1 5.7L0 3L-4.1 5.7L-2.9 0.9L-6.7 -2.2L-1.8 -2.4Z",
            _ => "M-4.5 0A4.5 4.5 0 1 0 4.5 0A4.5 4.5 0 1 0 -4.5 0Z",
        };

        /// <summary>The pin's fill colour as <c>#rrggbb</c>.</summary>
        public static string Fill(WorldMapPinIcon icon) => icon switch
        {
            WorldMapPinIcon.Fire => "#e8792b",
            WorldMapPinIcon.Rune => "#8fb8de",
            WorldMapPinIcon.Boss => "#c0392b",
            WorldMapPinIcon.Quest => "#e8b93c",
            _ => "#f4e6c3",
        };

        public const string Ink = "#1f160f";
        public const string CheckedColor = "#c0392b";
    }
}
