namespace ValheimServerGUI.Game
{
    /// <summary>
    /// The user's UI theme preference. <see cref="System"/> follows the OS theme
    /// variant; the shell maps this to Avalonia's <c>RequestedThemeVariant</c>. Stored as its number, so values
    /// are append-only.
    /// </summary>
    public enum AppTheme
    {
        System = 0,
        Light = 1,
        Dark = 2,

        /// <summary>Warm wood and gold, built on the dark variant (see <c>AppThemeVariants</c> in the shell).</summary>
        Valheim = 3,
    }
}
