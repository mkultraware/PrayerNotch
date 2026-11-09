using System;
using System.Globalization;
using System.IO;

namespace PrayerReminder
{
    internal static class Tests
    {
        private static int failures;

        public static int Main()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prayer_time_table-MALMO.txt");
            PrayerSchedule schedule = PrayerSchedule.Load(path);

            ExpectNext(schedule, "2026-03-28 12:15", "Dhuhr", "2026-03-28 12:16");
            ExpectNext(schedule, "2026-03-29 13:15", "Dhuhr", "2026-03-29 13:16");
            ExpectNext(schedule, "2026-10-24 12:54", "Dhuhr", "2026-10-24 12:55");
            ExpectNext(schedule, "2026-10-25 11:54", "Dhuhr", "2026-10-25 11:55");

            ExpectNext(schedule, "2027-03-27 12:15", "Dhuhr", "2027-03-27 12:16");
            ExpectNext(schedule, "2027-03-28 13:15", "Dhuhr", "2027-03-28 13:16");
            ExpectNext(schedule, "2027-10-30 12:53", "Dhuhr", "2027-10-30 12:54");
            ExpectNext(schedule, "2027-10-31 11:53", "Dhuhr", "2027-10-31 11:54");

            PrayerOccurrence leapDay = schedule.FindNext(Parse("2028-02-29 00:00"));
            Assert(leapDay.When.Date == Parse("2028-02-29 00:00").Date && leapDay.Name == "Fajr",
                "Leap day should interpolate a complete day of times.");

            PrayerOccurrence rollover = schedule.FindNext(Parse("2031-12-31 23:59"));
            Assert(rollover.When.Date == Parse("2032-01-01 00:00").Date && rollover.Name == "Fajr",
                "Year rollover should reuse January 1 safely.");

            // The subtitle upper-cases the city name; \u00B7 is the middle dot and
            // \u00D6 the uppercase o-umlaut, kept as escapes so the test source stays pure ASCII.
            PrayerOccurrence scheduled = schedule.FindNext(Parse("2026-11-09 20:00"));
            Assert(scheduled.Subtitle("Malm\u00F6") == "PRAYER TIME  \u00B7  MALM\u00D6",
                "Scheduled reminder should be labelled PRAYER TIME.");

            PrayerOccurrence preview = scheduled.AsPreview();
            Assert(preview.IsPreview && !preview.IsTest && preview.Name == scheduled.Name && preview.When == scheduled.When,
                "AsPreview should keep the occurrence and flag it as a preview.");
            Assert(preview.Subtitle("Malm\u00F6") == "NEXT PRAYER  \u00B7  MALM\u00D6",
                "Tray preview should be labelled NEXT PRAYER.");

            Assert(new PrayerOccurrence("Dhuhr", Parse("2026-11-09 12:00"), true, false).Subtitle("Malm\u00F6") == "TEST REMINDER  \u00B7  MALM\u00D6",
                "Test reminder should be labelled TEST REMINDER.");

            TestComputedSolar();
            TestComputedOrdering();

            if (failures == 0)
            {
                Console.WriteLine("All prayer schedule checks passed.");
                return 0;
            }

            Console.Error.WriteLine(failures + " prayer schedule check(s) failed.");
            return 1;
        }

        private static void TestComputedSolar()
        {
            const double lat = 55.6050;
            const double lon = 13.0038;
            // Expected UTC hours for Umm al-Qura (fajr, sunrise, dhuhr, asr, maghrib, isha).
            CheckSolar(lat, lon, 2026, 1, 1, 5.2275, 7.6092, 11.1925, 12.5453, 14.7759, 16.2759);
            CheckSolar(lat, lon, 2026, 6, 21, 1.4781, 2.4039, 11.1635, 15.6903, 19.9232, 21.4232);
            CheckSolar(lat, lon, 2026, 12, 21, 5.1809, 7.5833, 11.1008, 12.4063, 14.6183, 16.1183);
        }

        private static void CheckSolar(double lat, double lon, int y, int m, int d, double ef, double es, double ed, double ea, double em, double ei)
        {
            double[] t = SolarTimes.Compute(lat, lon, new DateTime(y, m, d), PrayerMethod.UmmAlQura);
            ExpectClose("Fajr", t[0], ef);
            ExpectClose("Sunrise", t[1], es);
            ExpectClose("Dhuhr", t[2], ed);
            ExpectClose("Asr", t[3], ea);
            ExpectClose("Maghrib", t[4], em);
            ExpectClose("Isha", t[5], ei);
        }

        private static void ExpectClose(string name, double actual, double expected)
        {
            if (Math.Abs(actual - expected) <= 0.05)
                return;
            failures++;
            Console.Error.WriteLine(name + " UTC hours: expected " + expected.ToString("F4", CultureInfo.InvariantCulture) +
                ", got " + actual.ToString("F4", CultureInfo.InvariantCulture) + ".");
        }

        private static void TestComputedOrdering()
        {
            LocationConfig config = new LocationConfig
            {
                City = "Malmö",
                Latitude = 55.6050,
                Longitude = 13.0038,
                TimeZoneId = null,
                Method = PrayerMethod.UmmAlQura
            };
            ComputedSchedule schedule = ComputedSchedule.Create(config);
            DateTime cursor = new DateTime(2026, 1, 1, 0, 0, 0);
            string[] expectedNames = { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };
            DateTime previous = DateTime.MinValue;
            for (int i = 0; i < 5; i++)
            {
                PrayerOccurrence occurrence = schedule.FindNext(cursor);
                Assert(occurrence.When > previous,
                    "Computed occurrences must be in increasing time order; " + occurrence.Name + " at " +
                    occurrence.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " is not after " +
                    previous.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
                Assert(IsPrayerName(occurrence.Name),
                    "Unexpected prayer name \"" + occurrence.Name + "\".");
                previous = occurrence.When;
                cursor = occurrence.When.AddSeconds(1);
            }
            // The first five occurrences from a fixed point must cycle through the five prayers.
            DateTime cursor2 = new DateTime(2026, 1, 1, 0, 0, 0);
            for (int i = 0; i < 5; i++)
            {
                PrayerOccurrence occurrence = schedule.FindNext(cursor2);
                Assert(occurrence.Name == expectedNames[i],
                    "Expected the " + (i + 1) + "th occurrence to be " + expectedNames[i] + ", got " + occurrence.Name + ".");
                cursor2 = occurrence.When.AddSeconds(1);
            }
        }

        private static bool IsPrayerName(string name)
        {
            return name == "Fajr" || name == "Dhuhr" || name == "Asr" || name == "Maghrib" || name == "Isha";
        }

        private static void ExpectNext(PrayerSchedule schedule, string after, string expectedName, string expectedWhen)
        {
            PrayerOccurrence actual = schedule.FindNext(Parse(after));
            DateTime expectedDate = Parse(expectedWhen);
            Assert(actual.Name == expectedName && actual.When == expectedDate,
                "After " + after + ", expected " + expectedName + " at " + expectedWhen +
                ", got " + actual.Name + " at " + actual.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
        }

        private static DateTime Parse(string value)
        {
            return DateTime.ParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None);
        }

        private static void Assert(bool condition, string message)
        {
            if (condition)
                return;
            failures++;
            Console.Error.WriteLine(message);
        }
    }
}
