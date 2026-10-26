using System;
using System.IO;
using ValheimServerGUI.Game;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game
{
    // The language preference is read straight from the preferences file at startup, before any service exists.
    public sealed class AppLanguageTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("vsg-lang-").FullName;

        private string PrefsPath => Path.Combine(_dir, "userprefs.json");

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        [Theory]
        [InlineData(AppLanguage.System, null)]
        [InlineData(AppLanguage.English, "en")]
        [InlineData(AppLanguage.French, "fr")]
        public void Maps_each_language_to_its_culture(AppLanguage language, string? culture)
        {
            Assert.Equal(culture, AppLanguages.ToCultureName(language));
        }

        [Fact]
        public void Reads_the_stored_language()
        {
            File.WriteAllText(PrefsPath, "{\"theme\":3,\"language\":2}");

            Assert.Equal("fr", AppLanguages.ReadCultureName(PrefsPath));
        }

        [Theory]
        [InlineData("{\"theme\":3}")]
        [InlineData("not json")]
        [InlineData("{\"language\":\"klingon\"}")]
        public void A_file_without_a_usable_language_keeps_the_OS_language(string content)
        {
            File.WriteAllText(PrefsPath, content);

            Assert.Null(AppLanguages.ReadCultureName(PrefsPath));
        }

        [Fact]
        public void A_missing_file_keeps_the_OS_language()
        {
            Assert.Null(AppLanguages.ReadCultureName(PrefsPath));
        }
    }
}
