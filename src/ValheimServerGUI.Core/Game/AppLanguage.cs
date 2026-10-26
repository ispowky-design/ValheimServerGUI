using System;
using System.IO;
using Newtonsoft.Json;

namespace ValheimServerGUI.Game
{
    /// <summary>
    /// The user's UI language preference. <see cref="System"/> follows the OS UI language (English when the app has
    /// no translation for it). Stored as its number, so values are append-only. Applied at startup only: the app is
    /// restart-to-switch (see <c>Program.ConfigureCulture</c>).
    /// </summary>
    public enum AppLanguage
    {
        System = 0,
        English = 1,
        French = 2,
    }

    public static class AppLanguages
    {
        /// <summary>The UI culture name for <paramref name="language"/>, or null to keep the OS UI culture.</summary>
        public static string? ToCultureName(AppLanguage language) => language switch
        {
            AppLanguage.English => "en",
            AppLanguage.French => "fr",
            _ => null,
        };

        /// <summary>
        /// The UI culture name stored in the preferences file at <paramref name="userPrefsFilePath"/>, read before
        /// the app's services exist. Null (keep the OS UI culture) when the file is missing, unreadable or says
        /// <see cref="AppLanguage.System"/>.
        /// </summary>
        public static string? ReadCultureName(string userPrefsFilePath)
        {
            try
            {
                if (!File.Exists(userPrefsFilePath)) return null;
                var file = JsonConvert.DeserializeObject<UserPreferencesFile>(File.ReadAllText(userPrefsFilePath));
                return ToCultureName(file?.Language ?? AppLanguage.System);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
