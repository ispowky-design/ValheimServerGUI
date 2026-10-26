using System;
using System.Linq;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game
{
    public class RestartScheduleTests
    {
        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("06:00", "06:00")]
        [InlineData("18:30, 6:00", "06:00 18:30")]
        [InlineData("06:00;18:00  06:00\n23:59", "06:00 18:00 23:59")]
        [InlineData("0:00", "00:00")]
        public void Parses_sorted_distinct_times(string? text, string expected)
        {
            var times = RestartSchedule.Parse(text).Select(t => t.ToString(@"hh\:mm"));

            Assert.Equal(expected, string.Join(" ", times));
        }

        [Theory]
        [InlineData("24:00", "24:00")]
        [InlineData("6h", "6h")]
        [InlineData("06:00, 7:5", "7:5")]
        [InlineData("12:60", "12:60")]
        [InlineData("-1:00", "-1:00")]
        public void Rejects_an_entry_that_is_not_a_time_of_day(string text, string bad)
        {
            var e = Assert.Throws<ArgumentException>(() => RestartSchedule.Parse(text));

            Assert.Equal(string.Format(Strings.Validation_RestartTimeInvalid, bad), e.Message);
        }

        [Theory]
        [InlineData("2026-10-26 05:00", "2026-10-26 06:00")]
        [InlineData("2026-10-26 06:00", "2026-10-26 18:00")] // strictly after: a restart at 06:00 is not due again at once
        [InlineData("2026-10-26 12:00", "2026-10-26 18:00")]
        [InlineData("2026-10-26 23:00", "2026-10-27 06:00")]
        public void Next_time_is_the_first_one_strictly_after(string now, string expected)
        {
            var times = RestartSchedule.Parse("06:00, 18:00");

            Assert.Equal(DateTime.Parse(expected), RestartSchedule.NextAfter(DateTime.Parse(now), times));
        }

        [Fact]
        public void No_times_means_no_restart()
        {
            Assert.Null(RestartSchedule.NextAfter(DateTime.Now, RestartSchedule.Parse("")));
        }
    }
}
