using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.Game
{
    /// <summary>
    /// A profile's daily scheduled-restart times, typed as 24-hour "HH:mm" times separated by commas, semicolons or
    /// spaces ("06:00, 18:30"). Empty text means no scheduled restart.
    /// </summary>
    public static class RestartSchedule
    {
        private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };
        private static readonly string[] Formats = { @"h\:mm", @"hh\:mm" };

        /// <summary>Parses the times, sorted and without duplicates. Throws <see cref="ArgumentException"/> naming
        /// the first entry that is not a valid time of day.</summary>
        public static IReadOnlyList<TimeSpan> Parse(string? text)
        {
            var times = new SortedSet<TimeSpan>();
            foreach (var entry in (text ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TimeSpan.TryParseExact(entry, Formats, CultureInfo.InvariantCulture, out var time)
                    || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
                {
                    throw new ArgumentException(string.Format(Strings.Validation_RestartTimeInvalid, entry));
                }

                times.Add(time);
            }

            return times.ToList();
        }

        /// <summary>The first scheduled time strictly after <paramref name="after"/>, or null with no times.</summary>
        public static DateTime? NextAfter(DateTime after, IReadOnlyList<TimeSpan> times)
        {
            if (times.Count == 0) return null;

            foreach (var day in new[] { after.Date, after.Date.AddDays(1) })
            {
                foreach (var time in times)
                {
                    var candidate = day + time;
                    if (candidate > after) return candidate;
                }
            }

            return null; // unreachable: tomorrow's first time is always later
        }
    }
}
