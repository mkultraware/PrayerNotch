using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PrayerReminder
{
    internal static class Program
    {
        private const string InstanceMutexName = @"Local\PrayerReminder";
        private static Mutex instanceMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            bool ownsMutex;
            instanceMutex = new Mutex(true, InstanceMutexName, out ownsMutex);
            if (!ownsMutex)
            {
                if (ContainsArgument(args, "--stop"))
                    SignalRunningInstanceToStop();
                instanceMutex.Dispose();
                return;
            }

            if (ContainsArgument(args, "--stop"))
            {
                instanceMutex.ReleaseMutex();
                instanceMutex.Dispose();
                return;
            }

            NativeMethods.EnablePerMonitorDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs)
            {
                CrashLog.Write("UI thread", eventArgs.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs)
            {
                CrashLog.Write("Background thread", eventArgs.ExceptionObject as Exception);
            };

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string stopRequestPath = Path.Combine(baseDirectory, ".stop-request");
            try
            {
                if (File.Exists(stopRequestPath))
                    File.Delete(stopRequestPath);

                ISchedule schedule;
                string cityName;
                LocationConfig config = LocationStore.Load(baseDirectory);
                if (config != null && config.IsValid)
                {
                    schedule = ComputedSchedule.Create(config);
                    cityName = config.City;
                }
                else if (Directory.GetFiles(baseDirectory, "prayer_time_table-*.txt").Length > 0)
                {
                    string timetablePath = FindTimetablePath(baseDirectory);
                    schedule = PrayerSchedule.Load(timetablePath);
                    cityName = ResolveCityName(baseDirectory, timetablePath);
                }
                else
                {
                    using (LocationDialog dialog = new LocationDialog(null))
                    {
                        if (dialog.ShowDialog() != DialogResult.OK || dialog.Config == null)
                            return;
                        config = dialog.Config;
                        LocationStore.Save(baseDirectory, config);
                    }
                    schedule = ComputedSchedule.Create(config);
                    cityName = config.City;
                }

                using (ReminderContext context = new ReminderContext(schedule, cityName, baseDirectory, args, stopRequestPath))
                {
                    Application.Run(context);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "Prayer Reminder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                instanceMutex.ReleaseMutex();
                instanceMutex.Dispose();
            }
        }

        internal static void RestartApp()
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            try
            {
                Process.Start(exePath);
            }
            catch (Exception exception)
            {
                CrashLog.Write("Restart", exception);
            }
            Environment.Exit(0);
        }

        private static bool ContainsArgument(string[] args, string expected)
        {
            foreach (string argument in args)
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void SignalRunningInstanceToStop()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".stop-request");
                File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                CrashLog.Write("Stop request", exception);
            }
        }

        private static string FindTimetablePath(string baseDirectory)
        {
            string[] files = Directory.GetFiles(baseDirectory, "prayer_time_table-*.txt");
            if (files.Length == 0)
                throw new FileNotFoundException(
                    "No prayer timetable was found. Place a file named prayer_time_table-{CITY}.txt next to the app.",
                    "prayer_time_table-*.txt");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files[0];
        }

        private static string ResolveCityName(string baseDirectory, string timetablePath)
        {
            string locationPath = Path.Combine(baseDirectory, "location.txt");
            if (File.Exists(locationPath))
            {
                string[] lines = File.ReadAllLines(locationPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length > 0)
                        return line;
                }
            }
            string fileName = Path.GetFileNameWithoutExtension(timetablePath);
            int dash = fileName.LastIndexOf('-');
            string city = dash >= 0 ? fileName.Substring(dash + 1) : fileName;
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(city.ToLowerInvariant());
        }
    }

    internal sealed class ReminderContext : ApplicationContext, IDisposable
    {
        private readonly ISchedule schedule;
        private readonly string cityName;
        private readonly string baseDirectory;
        private readonly NotificationWindow notification;
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem todayItem;
        private readonly PrayerScheduler scheduler;
        private readonly FileSystemWatcher stopWatcher;
        private int stopQueued;
        private bool disposed;

        public ReminderContext(ISchedule schedule, string cityName, string baseDirectory, string[] args, string stopRequestPath)
        {
            this.schedule = schedule;
            this.cityName = cityName;
            this.baseDirectory = baseDirectory;
            notification = new NotificationWindow(cityName);
            IntPtr notificationHandle = notification.Handle;

            todayItem = new ToolStripMenuItem { Enabled = false };
            ToolStripMenuItem changeLocationItem = new ToolStripMenuItem("Change location…");
            ToolStripMenuItem testItem = new ToolStripMenuItem("Show test reminder");
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            changeLocationItem.Click += delegate { OnChangeLocation(); };
            testItem.Click += delegate { ShowReminder(new PrayerOccurrence("Dhuhr", DateTime.Now, true, false)); };
            exitItem.Click += delegate { ExitApplication(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(todayItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(changeLocationItem);
            menu.Items.Add(testItem);
            menu.Items.Add(exitItem);
            menu.Opening += delegate { todayItem.Text = schedule.FormatToday(DateTime.Now); };

            tray = new NotifyIcon
            {
                Icon = VectorArtwork.CreateTrayIcon(),
                Text = "Prayer Reminder · " + cityName,
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.DoubleClick += delegate { ShowReminder(schedule.FindNext(DateTime.Now).AsPreview()); };

            scheduler = new PrayerScheduler(schedule, notification, ShowReminder);
            scheduler.Start();

            stopWatcher = new FileSystemWatcher(
                Path.GetDirectoryName(stopRequestPath),
                Path.GetFileName(stopRequestPath));
            stopWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
            stopWatcher.Created += OnStopRequested;
            stopWatcher.Changed += OnStopRequested;
            stopWatcher.EnableRaisingEvents = true;
            if (File.Exists(stopRequestPath))
                QueueStop(stopRequestPath);

            if (HasArgument(args, "--test"))
            {
                string prayer = GetPrayerArgument(args) ?? "Dhuhr";
                string snapshotPath = GetArgumentValue(args, "--snapshot=");
                ShowReminder(new PrayerOccurrence(prayer, DateTime.Now, true, false));
                if (!string.IsNullOrEmpty(snapshotPath))
                    notification.SaveSnapshot(snapshotPath);
            }
            else if (HasArgument(args, "--next"))
            {
                string snapshotPath = GetArgumentValue(args, "--snapshot=");
                PrayerOccurrence next = schedule.FindNext(DateTime.Now);
                ShowReminder(next.AsPreview());
                if (!string.IsNullOrEmpty(snapshotPath))
                    notification.SaveSnapshot(snapshotPath);
            }
        }

        private void OnChangeLocation()
        {
            LocationConfig current = LocationStore.Load(baseDirectory);
            using (LocationDialog dialog = new LocationDialog(current))
            {
                if (dialog.ShowDialog() != DialogResult.OK || dialog.Config == null)
                    return;
                LocationStore.Save(baseDirectory, dialog.Config);
                Program.RestartApp();
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            foreach (string argument in args)
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string GetPrayerArgument(string[] args)
        {
            string value = GetArgumentValue(args, "--prayer=");
            if (!string.IsNullOrEmpty(value))
                return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
            return null;
        }

        private static string GetArgumentValue(string[] args, string prefix)
        {
            foreach (string argument in args)
            {
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string value = argument.Substring(prefix.Length).Trim();
                    if (value.Length > 0)
                        return value;
                }
            }
            return null;
        }

        private void ShowReminder(PrayerOccurrence occurrence)
        {
            if (notification.IsDisposed)
                return;

            if (notification.InvokeRequired)
            {
                notification.BeginInvoke(new Action<PrayerOccurrence>(ShowReminder), occurrence);
                return;
            }

            notification.ShowPrayer(occurrence);
        }

        private void ExitApplication()
        {
            Dispose();
            ExitThread();
        }

        private void OnStopRequested(object sender, FileSystemEventArgs eventArgs)
        {
            QueueStop(eventArgs.FullPath);
        }

        private void QueueStop(string requestPath)
        {
            if (Interlocked.Exchange(ref stopQueued, 1) != 0)
                return;

            try
            {
                File.Delete(requestPath);
            }
            catch
            {
            }

            if (!disposed && notification.IsHandleCreated)
                notification.BeginInvoke(new Action(ExitApplication));
        }

        public new void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            stopWatcher.EnableRaisingEvents = false;
            stopWatcher.Dispose();
            scheduler.Dispose();
            tray.Visible = false;
            tray.Dispose();
            notification.Dispose();
            base.Dispose();
        }
    }

    internal static class CrashLog
    {
        private static readonly object Sync = new object();

        public static void Write(string source, Exception exception)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrayerReminder-error.log");
                string details = exception == null ? "Unknown managed exception" : exception.ToString();
                string entry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "  " + source + Environment.NewLine + details + Environment.NewLine + Environment.NewLine;
                lock (Sync)
                    File.AppendAllText(path, entry);
            }
            catch
            {
            }
        }
    }

    internal sealed class PrayerScheduler : IDisposable
    {
        private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(10);
        private readonly ISchedule schedule;
        private readonly Control dispatcher;
        private readonly Action<PrayerOccurrence> onPrayer;
        private readonly object sync = new object();
        private readonly HashSet<string> delivered = new HashSet<string>(StringComparer.Ordinal);
        private System.Threading.Timer timer;
        private bool disposed;

        public PrayerScheduler(ISchedule schedule, Control dispatcher, Action<PrayerOccurrence> onPrayer)
        {
            this.schedule = schedule;
            this.dispatcher = dispatcher;
            this.onPrayer = onPrayer;
        }

        public void Start()
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.TimeChanged += OnTimeChanged;
            timer = new System.Threading.Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            Evaluate();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
        {
            if (eventArgs.Mode == PowerModes.Resume)
                Evaluate();
        }

        private void OnTimeChanged(object sender, EventArgs eventArgs)
        {
            Evaluate();
        }

        private void OnTimer(object state)
        {
            Evaluate();
        }

        private void Evaluate()
        {
            lock (sync)
            {
                if (disposed)
                    return;

                DateTime now = DateTime.Now;
                PrayerOccurrence recent = schedule.FindMostRecent(now);
                if (recent != null && now - recent.When <= GracePeriod)
                {
                    string key = recent.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ":" + recent.Name;
                    if (delivered.Add(key))
                    {
                        dispatcher.BeginInvoke(new Action(delegate { onPrayer(recent); }));
                    }
                }

                PrayerOccurrence next = schedule.FindNext(now);
                TimeSpan delay = next.When - now;
                if (delay < TimeSpan.FromMilliseconds(250))
                    delay = TimeSpan.FromMilliseconds(250);

                timer.Change(delay, Timeout.InfiniteTimeSpan);
                RemoveOldDeliveryKeys(now.Date.AddDays(-2));
            }
        }

        private void RemoveOldDeliveryKeys(DateTime oldestDate)
        {
            List<string> expired = new List<string>();
            foreach (string key in delivered)
            {
                DateTime date;
                if (DateTime.TryParseExact(key.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date) && date < oldestDate)
                {
                    expired.Add(key);
                }
            }
            foreach (string key in expired)
                delivered.Remove(key);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.TimeChanged -= OnTimeChanged;
                if (timer != null)
                    timer.Dispose();
            }
        }
    }

    internal interface ISchedule
    {
        PrayerOccurrence FindMostRecent(DateTime now);
        PrayerOccurrence FindNext(DateTime now);
        string FormatToday(DateTime now);
    }

    internal sealed class PrayerSchedule : ISchedule
    {
        private static readonly string[] PrayerNames = { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };
        private static readonly string[] CsvNames = { "fajr", "sunrise", "dhuhr", "asr", "maghrib", "isha" };
        private readonly Dictionary<string, Dictionary<string, TimeSpan>> standardTimes;
        private readonly TimeZoneInfo locationTimeZone;

        private PrayerSchedule(Dictionary<string, Dictionary<string, TimeSpan>> standardTimes)
        {
            this.standardTimes = standardTimes;
            locationTimeZone = FindLocationTimeZone();
        }

        public static PrayerSchedule Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("The prayer timetable was not found.", path);

            Dictionary<string, Dictionary<string, TimeSpan>> rows =
                new Dictionary<string, Dictionary<string, TimeSpan>>(StringComparer.Ordinal);
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2)
                throw new InvalidDataException("The prayer timetable is empty.");

            string[] headers = SplitCsvLine(lines[0]);
            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
                columns[headers[i].Trim()] = i;

            RequireColumn(columns, "date");
            foreach (string name in CsvNames)
                RequireColumn(columns, name);

            for (int lineNumber = 1; lineNumber < lines.Length; lineNumber++)
            {
                if (string.IsNullOrWhiteSpace(lines[lineNumber]))
                    continue;

                string[] values = SplitCsvLine(lines[lineNumber]);
                DateTime sourceDate;
                if (!DateTime.TryParseExact(ValueAt(values, columns["date"]), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out sourceDate))
                {
                    throw new InvalidDataException("Invalid date on timetable line " + (lineNumber + 1) + ".");
                }

                Dictionary<string, TimeSpan> day = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in CsvNames)
                {
                    TimeSpan civilTime;
                    if (!TimeSpan.TryParseExact(ValueAt(values, columns[name]), @"hh\:mm",
                        CultureInfo.InvariantCulture, out civilTime))
                    {
                        throw new InvalidDataException("Invalid " + name + " time on timetable line " + (lineNumber + 1) + ".");
                    }

                    day[name] = RemoveSourceDst(sourceDate, civilTime);
                }

                rows[sourceDate.ToString("MM-dd", CultureInfo.InvariantCulture)] = day;
            }

            if (rows.Count != 365)
                throw new InvalidDataException("Expected 365 timetable days, found " + rows.Count + ".");

            return new PrayerSchedule(rows);
        }

        public PrayerOccurrence FindMostRecent(DateTime now)
        {
            PrayerOccurrence mostRecent = null;
            foreach (PrayerOccurrence occurrence in GetPrayerOccurrences(now.Date))
            {
                if (occurrence.When <= now && (mostRecent == null || occurrence.When > mostRecent.When))
                    mostRecent = occurrence;
            }
            return mostRecent;
        }

        public PrayerOccurrence FindNext(DateTime now)
        {
            for (int dayOffset = 0; dayOffset < 3; dayOffset++)
            {
                foreach (PrayerOccurrence occurrence in GetPrayerOccurrences(now.Date.AddDays(dayOffset)))
                {
                    if (occurrence.When > now)
                        return occurrence;
                }
            }
            throw new InvalidOperationException("Could not determine the next prayer time.");
        }

        public string FormatToday(DateTime now)
        {
            List<string> parts = new List<string>();
            foreach (PrayerOccurrence occurrence in GetPrayerOccurrences(now.Date))
                parts.Add(occurrence.Name + " " + occurrence.When.ToString("HH:mm", CultureInfo.InvariantCulture));
            return string.Join("   ", parts.ToArray());
        }

        private IEnumerable<PrayerOccurrence> GetPrayerOccurrences(DateTime date)
        {
            Dictionary<string, TimeSpan> day = GetStandardDay(date);
            foreach (string prayerName in PrayerNames)
            {
                string csvName = prayerName.ToLowerInvariant();
                TimeSpan time = day[csvName];
                DateTime local = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
                if (locationTimeZone.IsDaylightSavingTime(local))
                    local = local.AddHours(1);
                yield return new PrayerOccurrence(prayerName, local, false, false);
            }
        }

        private Dictionary<string, TimeSpan> GetStandardDay(DateTime date)
        {
            string key = date.ToString("MM-dd", CultureInfo.InvariantCulture);
            Dictionary<string, TimeSpan> day;
            if (standardTimes.TryGetValue(key, out day))
                return day;

            if (date.Month == 2 && date.Day == 29)
                return InterpolateLeapDay();

            throw new InvalidDataException("No timetable data exists for " + key + ".");
        }

        private Dictionary<string, TimeSpan> InterpolateLeapDay()
        {
            Dictionary<string, TimeSpan> before = standardTimes["02-28"];
            Dictionary<string, TimeSpan> after = standardTimes["03-01"];
            Dictionary<string, TimeSpan> result = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CsvNames)
            {
                long midpointTicks = before[name].Ticks + ((after[name].Ticks - before[name].Ticks) / 2);
                result[name] = TimeSpan.FromMinutes(Math.Round(TimeSpan.FromTicks(midpointTicks).TotalMinutes));
            }
            return result;
        }

        private static TimeSpan RemoveSourceDst(DateTime date, TimeSpan time)
        {
            DateTime firstShiftedDay = new DateTime(date.Year, 3, 28);
            DateTime firstStandardDay = new DateTime(date.Year, 10, 31);
            if (date.Date >= firstShiftedDay && date.Date < firstStandardDay)
                return time - TimeSpan.FromHours(1);
            return time;
        }

        private static TimeZoneInfo FindLocationTimeZone()
        {
            return TimeZoneInfo.Local;
        }

        private static string[] SplitCsvLine(string line)
        {
            return line.Split(',');
        }

        private static string ValueAt(string[] values, int index)
        {
            return index >= 0 && index < values.Length ? values[index].Trim() : string.Empty;
        }

        private static void RequireColumn(Dictionary<string, int> columns, string name)
        {
            if (!columns.ContainsKey(name))
                throw new InvalidDataException("The timetable is missing the '" + name + "' column.");
        }
    }

    internal sealed class PrayerOccurrence
    {
        public PrayerOccurrence(string name, DateTime when, bool isTest, bool isPreview)
        {
            Name = name;
            When = when;
            IsTest = isTest;
            IsPreview = isPreview;
        }

        public string Name { get; private set; }
        public DateTime When { get; private set; }
        public bool IsTest { get; private set; }
        public bool IsPreview { get; private set; }

        public PrayerOccurrence AsPreview()
        {
            return new PrayerOccurrence(Name, When, IsTest, true);
        }

        public string Subtitle(string cityName)
        {
            if (IsTest)
                return "TEST REMINDER  ·  " + cityName.ToUpperInvariant();
            if (IsPreview)
                return "NEXT PRAYER  ·  " + cityName.ToUpperInvariant();
            return "PRAYER TIME  ·  " + cityName.ToUpperInvariant();
        }
    }

    internal enum PrayerMethod
    {
        UmmAlQura,
        MWL,
        ISNA,
        Egyptian,
        Karachi,
        Dubai,
        Tehran
    }

    internal sealed class SolarTimes
    {
        private const double StandardTime = 96.0;
        private const double SunRadius = 0.833;

        public static double[] Compute(double latitude, double longitude, DateTime date, PrayerMethod method)
        {
            double julian = JulianDay(date.Year, date.Month, date.Day);
            double[] sun = SunPosition(julian);
            double declination = sun[0];
            double equationOfTime = sun[1];
            double midday = 12.0 - equationOfTime - longitude / 15.0;

            double sunrise = midday - TimeAngle(SunRadius, declination, latitude);
            double maghrib = midday + TimeAngle(SunRadius, declination, latitude);
            double dhuhr = midday;
            double asr = midday + AsrOffset(method, declination, latitude);

            double fajr = midday - TimeAngleRaw(PrayerMethodParams.FajrAngle(method), declination, latitude);
            if (double.IsNaN(fajr))
                fajr = sunrise - (sunrise - maghrib + 24.0) / 7.0;

            double isha;
            if (PrayerMethodParams.IshaMinutes(method) > 0.0)
                isha = maghrib + PrayerMethodParams.IshaMinutes(method) / 60.0;
            else
            {
                isha = midday + TimeAngleRaw(PrayerMethodParams.IshaAngle(method), declination, latitude);
                if (double.IsNaN(isha))
                    isha = maghrib + (sunrise + 24.0 - maghrib) / 7.0;
            }

            return new double[] { fajr, sunrise, dhuhr, asr, maghrib, isha };
        }

        private static double JulianDay(int year, int month, int day)
        {
            int a = (14 - month) / 12;
            int y = year + 4800 - a;
            int m = month + 12 * a - 3;
            return day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
        }

        private static double[] SunPosition(double julian)
        {
            double d = julian - 2451545.0;
            double g = FixAngle(357.529 + 0.98560028 * d);
            double q = FixAngle(280.459 + 0.98564736 * d);
            double l = FixAngle(q + 1.915 * Math.Sin(Radians(g)) + 0.020 * Math.Sin(Radians(2.0 * g)));
            double e = 23.439 - 0.00000036 * d;
            double ra = FixHour(Degrees(Math.Atan2(Math.Cos(Radians(e)) * Math.Sin(Radians(l)), Math.Cos(Radians(l)))) / 15.0);
            double declination = Math.Asin(Math.Sin(Radians(e)) * Math.Sin(Radians(l)));
            double equationOfTime = q / 15.0 - ra;
            return new double[] { declination, equationOfTime };
        }

        private static double TimeAngle(double angle, double declination, double latitude)
        {
            return TimeAngleRaw(angle, declination, latitude);
        }

        private static double TimeAngleRaw(double angle, double declination, double latitude)
        {
            double cosT = (-Math.Sin(Radians(angle)) - Math.Sin(declination) * Math.Sin(Radians(latitude))) /
                (Math.Cos(declination) * Math.Cos(Radians(latitude)));
            if (cosT > 1.0 || cosT < -1.0)
                return double.NaN;
            return Degrees(Math.Acos(cosT)) / 15.0;
        }

        private static double AsrOffset(PrayerMethod method, double declination, double latitude)
        {
            double altitude = Math.Atan(1.0 / (PrayerMethodParams.AsrFactor(method) + Math.Abs(Math.Tan(Radians(latitude) - declination))));
            double cosT = (Math.Sin(altitude) - Math.Sin(declination) * Math.Sin(Radians(latitude))) /
                (Math.Cos(declination) * Math.Cos(Radians(latitude)));
            cosT = Math.Max(-1.0, Math.Min(1.0, cosT));
            return Degrees(Math.Acos(cosT)) / 15.0;
        }

        private static double Radians(double degrees) { return degrees * Math.PI / 180.0; }
        private static double Degrees(double radians) { return radians * 180.0 / Math.PI; }
        private static double FixAngle(double value) { return (value % 360.0 + 360.0) % 360.0; }
        private static double FixHour(double value) { return (value % 24.0 + 24.0) % 24.0; }

    }

    internal static class PrayerMethodParams
    {
        public static double FajrAngle(PrayerMethod method)
        {
            switch (method)
            {
                case PrayerMethod.UmmAlQura: return 18.5;
                case PrayerMethod.MWL: return 18.0;
                case PrayerMethod.ISNA: return 15.0;
                case PrayerMethod.Egyptian: return 19.5;
                case PrayerMethod.Karachi: return 18.0;
                case PrayerMethod.Dubai: return 18.2;
                default: return 17.7;
            }
        }

        public static double IshaAngle(PrayerMethod method)
        {
            switch (method)
            {
                case PrayerMethod.MWL: return 17.0;
                case PrayerMethod.ISNA: return 15.0;
                case PrayerMethod.Egyptian: return 17.5;
                case PrayerMethod.Karachi: return 18.0;
                case PrayerMethod.Dubai: return 18.2;
                default: return 14.0;
            }
        }

        public static double IshaMinutes(PrayerMethod method)
        {
            return method == PrayerMethod.UmmAlQura ? 90.0 : 0.0;
        }

        public static double AsrFactor(PrayerMethod method)
        {
            return 1.0;
        }
    }

    internal sealed class ComputedSchedule : ISchedule
    {
        private readonly double latitude;
        private readonly double longitude;
        private readonly TimeZoneInfo timeZone;
        private readonly PrayerMethod method;

        private ComputedSchedule(double latitude, double longitude, TimeZoneInfo timeZone, PrayerMethod method)
        {
            this.latitude = latitude;
            this.longitude = longitude;
            this.timeZone = timeZone;
            this.method = method;
        }

        public static ComputedSchedule Create(LocationConfig config)
        {
            TimeZoneInfo zone;
            if (!string.IsNullOrEmpty(config.TimeZoneId))
            {
                try
                {
                    zone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
                }
                catch (TimeZoneNotFoundException)
                {
                    zone = TimeZoneInfo.Local;
                }
            }
            else
            {
                zone = TimeZoneInfo.Local;
            }
            return new ComputedSchedule(config.Latitude, config.Longitude, zone, config.Method);
        }

        public PrayerOccurrence FindMostRecent(DateTime now)
        {
            PrayerOccurrence mostRecent = null;
            foreach (PrayerOccurrence occurrence in GetOccurrences(now.Date))
            {
                if (occurrence.When <= now && (mostRecent == null || occurrence.When > mostRecent.When))
                    mostRecent = occurrence;
            }
            return mostRecent;
        }

        public PrayerOccurrence FindNext(DateTime now)
        {
            for (int dayOffset = 0; dayOffset < 3; dayOffset++)
            {
                foreach (PrayerOccurrence occurrence in GetOccurrences(now.Date.AddDays(dayOffset)))
                {
                    if (occurrence.When > now)
                        return occurrence;
                }
            }
            throw new InvalidOperationException("Could not determine the next prayer time.");
        }

        public string FormatToday(DateTime now)
        {
            List<string> parts = new List<string>();
            foreach (PrayerOccurrence occurrence in GetOccurrences(now.Date))
                parts.Add(occurrence.Name + " " + occurrence.When.ToString("HH:mm", CultureInfo.InvariantCulture));
            return string.Join("   ", parts.ToArray());
        }

        private IEnumerable<PrayerOccurrence> GetOccurrences(DateTime date)
        {
            double[] utcHours = SolarTimes.Compute(latitude, longitude, date, method);
            string[] names = { "Fajr", "Sunrise", "Dhuhr", "Asr", "Maghrib", "Isha" };
            for (int i = 0; i < utcHours.Length; i++)
            {
                if (names[i] == "Sunrise")
                    continue;
                long totalMinutes = (long)Math.Round(utcHours[i] * 60.0);
                DateTime utc = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).AddMinutes(totalMinutes);
                DateTime local = TimeZoneInfo.ConvertTime(utc, timeZone);
                yield return new PrayerOccurrence(names[i], local, false, false);
            }
        }
    }

    internal sealed class LocationConfig
    {
        public string City;
        public double Latitude;
        public double Longitude;
        public string TimeZoneId;
        public PrayerMethod Method;

        public bool IsValid
        {
            get
            {
                return !double.IsNaN(Latitude) && !double.IsNaN(Longitude)
                    && Math.Abs(Latitude) <= 90.0 && Math.Abs(Longitude) <= 180.0;
            }
        }
    }

    internal static class LocationStore
    {
        private static string ConfigPath(string baseDirectory)
        {
            return Path.Combine(baseDirectory, "location.json");
        }

        public static LocationConfig Load(string baseDirectory)
        {
            string path = ConfigPath(baseDirectory);
            if (!File.Exists(path))
                return null;
            try
            {
                string json = File.ReadAllText(path);
                LocationConfig config = new LocationConfig();
                config.City = Geocoder.JsonValue(json, "city");
                config.TimeZoneId = Geocoder.JsonValue(json, "timezone");
                config.Latitude = ParseDouble(Geocoder.JsonValue(json, "lat"));
                config.Longitude = ParseDouble(Geocoder.JsonValue(json, "lon"));
                config.Method = ParseMethod(Geocoder.JsonValue(json, "method"));
                return config;
            }
            catch
            {
                return null;
            }
        }

        public static void Save(string baseDirectory, LocationConfig config)
        {
            string path = ConfigPath(baseDirectory);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"city\": ").Append(JsonEscape(config.City)).Append(",\n");
            sb.Append("  \"lat\": ").Append(config.Latitude.ToString("R", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"lon\": ").Append(config.Longitude.ToString("R", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"timezone\": ").Append(JsonEscape(config.TimeZoneId)).Append(",\n");
            sb.Append("  \"method\": ").Append(JsonEscape(config.Method.ToString())).Append("\n");
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString());
        }

        private static double ParseDouble(string value)
        {
            double result;
            if (value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return result;
            return double.NaN;
        }

        private static PrayerMethod ParseMethod(string value)
        {
            PrayerMethod method;
            if (value != null && Enum.TryParse(value, true, out method))
                return method;
            return PrayerMethod.UmmAlQura;
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
                return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }
    }

    internal static class Geocoder
    {
        private const string UserAgent = "PrayerReminder/1.0 (personal prayer-times app)";

        public static bool TryGeocode(string query, out double latitude, out double longitude, out string city, out string timeZoneId, out string error)
        {
            latitude = 0;
            longitude = 0;
            city = null;
            timeZoneId = null;
            error = null;
            try
            {
                string searchUrl = "https://nominatim.openstreetmap.org/search?format=json&limit=1&q=" + Uri.EscapeDataString(query);
                string json = HttpGet(searchUrl);
                string lat = JsonValue(json, "lat");
                string lon = JsonValue(json, "lon");
                if (lat == null || lon == null)
                {
                    error = "No location found for \"" + query + "\".";
                    return false;
                }
                latitude = double.Parse(lat, CultureInfo.InvariantCulture);
                longitude = double.Parse(lon, CultureInfo.InvariantCulture);
                city = JsonValue(json, "display_name");
                if (string.IsNullOrEmpty(city))
                    city = query;

                string reverseUrl = "https://nominatim.openstreetmap.org/reverse?format=json&lat=" +
                    latitude.ToString("R", CultureInfo.InvariantCulture) + "&lon=" +
                    longitude.ToString("R", CultureInfo.InvariantCulture);
                try
                {
                    string reverseJson = HttpGet(reverseUrl);
                    timeZoneId = JsonValue(reverseJson, "timezone");
                }
                catch
                {
                    timeZoneId = null;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Location lookup failed: " + exception.Message;
                return false;
            }
        }

        private static string HttpGet(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        public static string JsonValue(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
                return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0)
                return null;
            int i = colon + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t'))
                i++;
            if (i >= json.Length)
                return null;
            if (json[i] == '"')
            {
                int end = json.IndexOf('"', i + 1);
                if (end < 0)
                    return null;
                return json.Substring(i + 1, end - i - 1);
            }
            int valueEnd = i;
            while (valueEnd < json.Length && json[valueEnd] != ',' && json[valueEnd] != '}' && json[valueEnd] != ']' &&
                   json[valueEnd] != ' ' && json[valueEnd] != '\n' && json[valueEnd] != '\r')
                valueEnd++;
            return json.Substring(i, valueEnd - i).Trim();
        }
    }

    internal sealed class LocationDialog : Form
    {
        private readonly TextBox placeBox;
        private readonly ComboBox methodBox;
        private readonly Label statusLabel;
        private readonly Button okButton;

        public LocationConfig Config { get; private set; }

        public LocationDialog(LocationConfig current)
        {
            Text = "Prayer Reminder — Location";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 196);

            Label title = new Label();
            title.Text = "Set your location";
            title.Font = new Font("Segoe UI", 11.0f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(16, 14);
            Controls.Add(title);

            Label placeLabel = new Label();
            placeLabel.Text = "Place name, or \"latitude, longitude\":";
            placeLabel.AutoSize = true;
            placeLabel.Location = new Point(16, 48);
            Controls.Add(placeLabel);

            placeBox = new TextBox();
            placeBox.Location = new Point(16, 70);
            placeBox.Width = 398;
            if (current != null && !string.IsNullOrEmpty(current.City))
                placeBox.Text = current.City;
            Controls.Add(placeBox);

            Label methodLabel = new Label();
            methodLabel.Text = "Calculation method:";
            methodLabel.AutoSize = true;
            methodLabel.Location = new Point(16, 104);
            Controls.Add(methodLabel);

            methodBox = new ComboBox();
            methodBox.DropDownStyle = ComboBoxStyle.DropDownList;
            methodBox.Location = new Point(16, 126);
            methodBox.Width = 200;
            methodBox.Items.AddRange(new object[] { "Umm al-Qura", "MWL", "ISNA", "Egyptian", "Karachi", "Dubai", "Tehran" });
            methodBox.SelectedIndex = 0;
            if (current != null)
                methodBox.SelectedIndex = MethodIndex(current.Method);
            Controls.Add(methodBox);

            statusLabel = new Label();
            statusLabel.Text = "";
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.AutoSize = true;
            statusLabel.MaximumSize = new Size(300, 0);
            statusLabel.Location = new Point(16, 160);
            Controls.Add(statusLabel);

            okButton = new Button();
            okButton.Text = "Save";
            okButton.Location = new Point(258, 152);
            okButton.Width = 72;
            okButton.Click += OnOk;
            Controls.Add(okButton);

            Button cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Location = new Point(342, 152);
            cancelButton.Width = 72;
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static int MethodIndex(PrayerMethod method)
        {
            switch (method)
            {
                case PrayerMethod.MWL: return 1;
                case PrayerMethod.ISNA: return 2;
                case PrayerMethod.Egyptian: return 3;
                case PrayerMethod.Karachi: return 4;
                case PrayerMethod.Dubai: return 5;
                case PrayerMethod.Tehran: return 6;
                default: return 0;
            }
        }

        private static PrayerMethod MethodFromIndex(int index)
        {
            switch (index)
            {
                case 1: return PrayerMethod.MWL;
                case 2: return PrayerMethod.ISNA;
                case 3: return PrayerMethod.Egyptian;
                case 4: return PrayerMethod.Karachi;
                case 5: return PrayerMethod.Dubai;
                case 6: return PrayerMethod.Tehran;
                default: return PrayerMethod.UmmAlQura;
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            string input = placeBox.Text.Trim();
            if (input.Length == 0)
            {
                statusLabel.Text = "Please enter a place or coordinates.";
                return;
            }

            PrayerMethod method = MethodFromIndex(methodBox.SelectedIndex);

            double lat, lon;
            if (TryParseCoordinates(input, out lat, out lon))
            {
                Config = new LocationConfig
                {
                    City = input,
                    Latitude = lat,
                    Longitude = lon,
                    TimeZoneId = null,
                    Method = method
                };
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            okButton.Enabled = false;
            statusLabel.ForeColor = Color.DimGray;
            statusLabel.Text = "Looking up \"" + input + "\"…";
            Update();

            double latitude, longitude;
            string city, timeZoneId, error;
            bool ok = Geocoder.TryGeocode(input, out latitude, out longitude, out city, out timeZoneId, out error);

            if (!ok)
            {
                statusLabel.ForeColor = Color.Firebrick;
                statusLabel.Text = error;
                okButton.Enabled = true;
                return;
            }

            Config = new LocationConfig
            {
                City = city,
                Latitude = latitude,
                Longitude = longitude,
                TimeZoneId = timeZoneId,
                Method = method
            };
            DialogResult = DialogResult.OK;
            Close();
        }

        private static bool TryParseCoordinates(string input, out double latitude, out double longitude)
        {
            latitude = 0;
            longitude = 0;
            string[] parts = input.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                return false;
            double lat, lon;
            if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
                double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
            {
                if (Math.Abs(lat) <= 90.0 && Math.Abs(lon) <= 180.0)
                {
                    latitude = lat;
                    longitude = lon;
                    return true;
                }
            }
            return false;
        }
    }

    internal sealed class NotificationWindow : Form
    {
        private enum Phase { Hidden, Expanding, Holding, Collapsing, Lingering, FadingOut }

        private const int BaseWidth = 440;
        private const int BasePanelHeight = 86;
        private const int BaseCanvasHeight = 110;
        private const int CollapsedWidth = 132;
        private const int CollapsedHeight = 46;
        private const int CollapsedCanvasHeight = 70;
        private const int AutoHideMilliseconds = 14000;
        private const int ContentFadeInMilliseconds = 320;
        private const int ContentFadeOutMilliseconds = 160;
        private const int PillLingerMilliseconds = 450;
        private const int FadeOutMilliseconds = 220;

        private readonly System.Windows.Forms.Timer animationTimer;
        private readonly System.Windows.Forms.Timer hideTimer;
        private readonly System.Diagnostics.Stopwatch animationClock = new System.Diagnostics.Stopwatch();
        private Bitmap surface;
        private IntPtr surfaceHandle;
        private GraphicsPath hitPath;
        private RectangleF closeBounds;
        private Phase phase = Phase.Hidden;
        private double springPosition;
        private double springVelocity;
        private double springTarget;
        private double springStiffness;
        private double springDampingRatio;
        private double springLimitMilliseconds;
        private int screenTop;
        private int screenCenterX;
        private PrayerOccurrence occurrence;
        private ThemePalette palette;
        private readonly string cityName;

        public NotificationWindow(string cityName)
        {
            this.cityName = cityName;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Prayer Reminder";

            animationTimer = new System.Windows.Forms.Timer { Interval = 15 };
            hideTimer = new System.Windows.Forms.Timer { Interval = AutoHideMilliseconds };
            animationTimer.Tick += Animate;
            hideTimer.Tick += delegate { BeginHide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                // WS_EX_TOPMOST must be set at creation: the TopMost property alone
                // is not enough once the window is shown without activation.
                parameters.ExStyle |= NativeMethods.WS_EX_TOPMOST |
                                      NativeMethods.WS_EX_LAYERED |
                                      NativeMethods.WS_EX_TOOLWINDOW |
                                      NativeMethods.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        public void ShowPrayer(PrayerOccurrence occurrence)
        {
            this.occurrence = occurrence;
            palette = ThemePalette.ReadCurrent();
            PositionWindow();

            hideTimer.Stop();

            if (phase == Phase.Expanding || phase == Phase.Holding)
            {
                // A new reminder while one is on screen: refresh the content in place.
                springTarget = 1.0;
                if (phase == Phase.Holding)
                {
                    RenderSurface(1.0, 1.0);
                    ApplyFrame();
                    Present(255, 1.0f, 1.0f);
                    hideTimer.Start();
                }
                return;
            }

            animationTimer.Stop();
            animationClock.Restart();
            RenderSurface(0.0, 0.0);
            ApplyFrame();
            phase = Phase.Expanding;
            springVelocity = 0.0;
            springTarget = 1.0;
            springStiffness = 190.0;
            springDampingRatio = 0.62;
            springLimitMilliseconds = 1400.0;

            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, Left, Top, Width, Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            Visible = true;
            Present(255, 1.0f, 1.0f);
            animationTimer.Start();
        }

        public void SaveSnapshot(string path)
        {
            if (occurrence == null || palette == null)
                throw new InvalidOperationException("The reminder has not been rendered yet.");

            float scale = GetScale();
            Size previousSize = Size;
            Size = new Size(ScaleValue(BaseWidth), ScaleValue(BaseCanvasHeight));
            Bitmap snapshot = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(snapshot))
                    DrawSurface(graphics, 1.0, 1.0, scale);
                snapshot.Save(Path.GetFullPath(path), ImageFormat.Png);
            }
            finally
            {
                snapshot.Dispose();
                Size = previousSize;
            }
        }

        private void PositionWindow()
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            screenTop = bounds.Top;
            screenCenterX = bounds.Left + (bounds.Width / 2);
        }

        private void ApplyFrame()
        {
            Left = screenCenterX - (Width / 2);
            Top = screenTop;
        }

        private void Animate(object sender, EventArgs eventArgs)
        {
            double elapsed = animationClock.Elapsed.TotalMilliseconds;
            StepSpring(15.0);

            double morph = springPosition;
            if (morph < 0.0)
                morph = 0.0;
            if (morph > 1.12)
                morph = 1.12;

            byte opacity = 255;
            if (phase == Phase.FadingOut)
            {
                double fadeProgress = elapsed / FadeOutMilliseconds;
                if (fadeProgress > 1.0)
                    fadeProgress = 1.0;
                opacity = (byte)(255.0 * (1.0 - fadeProgress));
            }

            RenderSurface(morph, CurrentContentFade(elapsed));
            ApplyFrame();
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, Left, Top, Width, Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            Present(opacity, CalculateSquashScale(), CalculateStretchScale());

            bool settled = Math.Abs(springPosition - springTarget) < 0.002 &&
                Math.Abs(springVelocity) < 0.01;

            if (phase == Phase.Expanding && (settled || elapsed >= springLimitMilliseconds))
            {
                springPosition = springTarget;
                springVelocity = 0.0;
                RenderSurface(1.0, 1.0);
                ApplyFrame();
                Present(255, 1.0f, 1.0f);
                phase = Phase.Holding;
                animationTimer.Stop();
                animationClock.Stop();
                hideTimer.Start();
            }
            else if (phase == Phase.Collapsing && (settled || elapsed >= springLimitMilliseconds))
            {
                springPosition = springTarget;
                springVelocity = 0.0;
                RenderSurface(0.0, 0.0);
                ApplyFrame();
                Present(255, 1.0f, 1.0f);
                phase = Phase.Lingering;
                animationClock.Restart();
            }
            else if (phase == Phase.Lingering && elapsed >= PillLingerMilliseconds)
            {
                phase = Phase.FadingOut;
                animationClock.Restart();
            }
            else if (phase == Phase.FadingOut && elapsed >= FadeOutMilliseconds)
            {
                animationTimer.Stop();
                animationClock.Stop();
                phase = Phase.Hidden;
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
                Visible = false;
            }
        }

        private double CurrentContentFade(double elapsed)
        {
            switch (phase)
            {
                case Phase.Expanding:
                    return Clamp01(elapsed / ContentFadeInMilliseconds);
                case Phase.Holding:
                    return 1.0;
                case Phase.Collapsing:
                    return Clamp01(1.0 - (elapsed / ContentFadeOutMilliseconds));
                default:
                    return 0.0;
            }
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0)
                return 0.0;
            if (value > 1.0)
                return 1.0;
            return value;
        }

        private static double Lerp(double from, double to, double t)
        {
            return from + (to - from) * t;
        }

        private static Color WithAlpha(Color color, double alpha)
        {
            return Color.FromArgb((int)(color.A * alpha), color.R, color.G, color.B);
        }

        private void StepSpring(double stepMilliseconds)
        {
            double stepSeconds = stepMilliseconds / 1000.0;
            double damping = 2.0 * Math.Sqrt(springStiffness) * springDampingRatio;
            double acceleration = springStiffness * (springTarget - springPosition) - damping * springVelocity;
            springVelocity += acceleration * stepSeconds;
            springPosition += springVelocity * stepSeconds;
        }

        private float CalculateStretchScale()
        {
            double velocity = Math.Abs(springVelocity);
            if (velocity < 0.05)
                return 1.0f;
            double stretch = Math.Min(0.03, velocity * 0.004);
            return (float)(1.0 + stretch);
        }

        private float CalculateSquashScale()
        {
            double velocity = Math.Abs(springVelocity);
            if (velocity < 0.05)
                return 1.0f;
            double squash = Math.Min(0.04, velocity * 0.005);
            return (float)(1.0 - squash);
        }

        private void BeginHide()
        {
            if (phase != Phase.Holding && phase != Phase.Expanding)
                return;

            hideTimer.Stop();
            animationTimer.Stop();
            animationClock.Restart();
            phase = Phase.Collapsing;
            springTarget = 0.0;
            springStiffness = 150.0;
            springDampingRatio = 0.85;
            springLimitMilliseconds = 1200.0;
            animationTimer.Start();
        }

        private void RenderSurface(double morph, double contentFade)
        {
            ReleaseSurface();

            float scale = GetScale();
            Size = new Size(
                (int)Math.Round(Lerp(CollapsedWidth, BaseWidth, morph) * scale),
                (int)Math.Round(Lerp(CollapsedCanvasHeight, BaseCanvasHeight, morph) * scale));
            surface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);

            using (Graphics graphics = Graphics.FromImage(surface))
                DrawSurface(graphics, morph, contentFade, scale);

            using (GraphicsPath panelPath = CreatePanelPath(morph, scale))
                hitPath = (GraphicsPath)panelPath.Clone();

            surfaceHandle = surface.GetHbitmap(Color.FromArgb(0));
        }

        private void DrawSurface(Graphics graphics, double morph, double contentFade, float scale)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            using (GraphicsPath panelPath = CreatePanelPath(morph, scale))
            {
                DrawShadow(graphics, panelPath, scale);
                float panelBottom = (float)(Lerp(CollapsedHeight, BasePanelHeight, morph) * scale);
                using (LinearGradientBrush panelBrush = new LinearGradientBrush(
                    new RectangleF(0, 0, Width, panelBottom),
                    Color.FromArgb(255, 0, 0, 0),
                    Color.FromArgb(255, 16, 16, 20),
                    LinearGradientMode.Vertical))
                    graphics.FillPath(panelBrush, panelPath);
                using (GraphicsPath borderPath = CreatePanelBorderPath(morph, scale))
                using (Pen borderPen = new Pen(palette.Border, Math.Max(1.0f, scale)))
                    graphics.DrawPath(borderPen, borderPath);
            }

            DrawContent(graphics, morph, contentFade, scale);
        }

        private GraphicsPath CreatePanelPath(double morph, float scale)
        {
            float sideMargin = (float)(Lerp(0.0, 8.0, morph) * scale);
            float radius = (float)(Lerp(CollapsedHeight / 2.0, 24.0, morph) * scale);
            float left = sideMargin;
            float right = Width - sideMargin;
            float bottom = (float)(Lerp(CollapsedHeight, BasePanelHeight, morph) * scale);
            const float kappa = 0.55228475f;

            GraphicsPath path = new GraphicsPath();
            path.StartFigure();
            path.AddLine(left, 0, right, 0);
            path.AddLine(right, 0, right, bottom - radius);
            path.AddBezier(right, bottom - radius,
                right, bottom - radius + (radius * kappa),
                right - radius + (radius * kappa), bottom,
                right - radius, bottom);
            path.AddLine(right - radius, bottom, left + radius, bottom);
            path.AddBezier(left + radius, bottom,
                left + radius - (radius * kappa), bottom,
                left, bottom - radius + (radius * kappa),
                left, bottom - radius);
            path.CloseFigure();
            return path;
        }

        private GraphicsPath CreatePanelBorderPath(double morph, float scale)
        {
            float sideMargin = (float)(Lerp(0.0, 8.0, morph) * scale);
            float radius = (float)(Lerp(CollapsedHeight / 2.0, 24.0, morph) * scale);
            float left = sideMargin;
            float right = Width - sideMargin;
            float bottom = (float)(Lerp(CollapsedHeight, BasePanelHeight, morph) * scale);
            const float kappa = 0.55228475f;

            GraphicsPath path = new GraphicsPath();
            path.StartFigure();
            path.AddLine(right, 0, right, bottom - radius);
            path.AddBezier(right, bottom - radius,
                right, bottom - radius + (radius * kappa),
                right - radius + (radius * kappa), bottom,
                right - radius, bottom);
            path.AddLine(right - radius, bottom, left + radius, bottom);
            path.AddBezier(left + radius, bottom,
                left + radius - (radius * kappa), bottom,
                left, bottom - radius + (radius * kappa),
                left, bottom - radius);
            path.AddLine(left, bottom - radius, left, 0);
            return path;
        }

        private void DrawShadow(Graphics graphics, GraphicsPath panelPath, float scale)
        {
            for (int step = 10; step >= 2; step -= 2)
            {
                int alpha = (int)(palette.ShadowAlpha * (1.0 - (step / 13.0)));
                using (Matrix transform = new Matrix())
                using (GraphicsPath shadowPath = (GraphicsPath)panelPath.Clone())
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                {
                    transform.Translate(0, step * scale);
                    shadowPath.Transform(transform);
                    graphics.FillPath(brush, shadowPath);
                }
            }
        }

        private void DrawContent(Graphics graphics, double morph, double contentFade, float scale)
        {
            Color accent = VectorArtwork.GetPrayerColor(occurrence.Name, palette.IsLight);

            double crescentFade = 1.0 - contentFade;
            if (crescentFade > 0.01)
            {
                float iconSize = 20.0f * scale;
                float panelHeight = (float)(Lerp(CollapsedHeight, BasePanelHeight, morph) * scale);
                RectangleF crescentBounds = new RectangleF(
                    (Width - iconSize) / 2.0f,
                    (panelHeight - iconSize) / 2.0f,
                    iconSize,
                    iconSize);
                VectorArtwork.DrawPrayerIcon(graphics, "Fajr", crescentBounds, WithAlpha(accent, crescentFade));
            }

            if (contentFade <= 0.01)
            {
                closeBounds = RectangleF.Empty;
                return;
            }

            GraphicsState savedState = graphics.Save();
            graphics.TranslateTransform((Width - (BaseWidth * scale)) / 2.0f, 0);
            try
            {
                VectorArtwork.DrawPrayerIcon(graphics, occurrence.Name, ScaleRectangle(53, 31, 24, 24, scale),
                    WithAlpha(accent, contentFade));

                string fontName = FontFamilyExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
                using (Font titleFont = new Font(fontName, 17.0f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font labelFont = new Font(fontName, 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font timeFont = new Font(fontName, 17.0f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush primary = new SolidBrush(WithAlpha(palette.PrimaryText, contentFade)))
                using (SolidBrush secondary = new SolidBrush(WithAlpha(palette.SecondaryText, contentFade)))
                {
                    graphics.DrawString(occurrence.Name, titleFont, primary, 101 * scale, 21 * scale,
                        StringFormat.GenericTypographic);
                    string subtitle = occurrence.Subtitle(cityName);
                    graphics.DrawString(subtitle, labelFont, secondary, 102 * scale, 47 * scale,
                        StringFormat.GenericTypographic);

                    using (Pen divider = new Pen(WithAlpha(palette.Divider, contentFade), Math.Max(1.0f, scale)))
                        graphics.DrawLine(divider, 276 * scale, 22 * scale, 276 * scale, 64 * scale);

                    string time = occurrence.When.ToString("HH:mm", CultureInfo.InvariantCulture);
                    graphics.DrawString(time, timeFont, primary, 294 * scale, 30 * scale,
                        StringFormat.GenericTypographic);
                }

                if (morph >= 0.995 && contentFade >= 0.995)
                {
                    closeBounds = ScaleRectangle(362, 28, 30, 30, scale);
                    using (SolidBrush closeBackground = new SolidBrush(palette.ControlBackground))
                        graphics.FillEllipse(closeBackground, closeBounds);
                    VectorArtwork.DrawClose(graphics, ScaleRectangle(370, 36, 14, 14, scale), palette.SecondaryText);
                }
                else
                {
                    closeBounds = RectangleF.Empty;
                }
            }
            finally
            {
                graphics.Restore(savedState);
            }
        }

        private void Present(byte opacity, float verticalScale, float horizontalScale)
        {
            if (surface == null || !IsHandleCreated)
                return;

            Bitmap source = surface;
            Bitmap stretched = null;
            if (Math.Abs(verticalScale - 1.0f) > 0.002f || Math.Abs(horizontalScale - 1.0f) > 0.002f)
            {
                int targetWidth = (int)Math.Round(Width * horizontalScale);
                int targetHeight = (int)Math.Round(Height * verticalScale);
                stretched = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppPArgb);
                using (Graphics graphics = Graphics.FromImage(stretched))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(surface, new Rectangle(0, 0, targetWidth, targetHeight));
                }
                source = stretched;
            }

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr oldBitmap = NativeMethods.SelectObject(memoryDc, source.GetHbitmap(Color.FromArgb(0)));
            try
            {
                NativeMethods.POINT destination = new NativeMethods.POINT(
                    Left + ((Width - source.Width) / 2), Top);
                NativeMethods.SIZE size = new NativeMethods.SIZE(source.Width, source.Height);
                NativeMethods.POINT sourceOrigin = new NativeMethods.POINT(0, 0);
                NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    SourceConstantAlpha = opacity,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };
                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                    memoryDc, ref sourceOrigin, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                NativeMethods.SelectObject(memoryDc, oldBitmap);
                NativeMethods.DeleteObject(oldBitmap);
                NativeMethods.DeleteDC(memoryDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                if (stretched != null)
                    stretched.Dispose();
            }
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            base.OnMouseUp(eventArgs);
            if (eventArgs.Button == MouseButtons.Left && closeBounds.Contains(eventArgs.Location))
                BeginHide();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                message.Result = new IntPtr(NativeMethods.MA_NOACTIVATE);
                return;
            }

            if (message.Msg == NativeMethods.WM_NCHITTEST && hitPath != null)
            {
                long packed = message.LParam.ToInt64();
                Point clientPoint = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                if (!hitPath.IsVisible(clientPoint))
                {
                    message.Result = new IntPtr(NativeMethods.HTTRANSPARENT);
                    return;
                }
            }

            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Dispose();
                hideTimer.Dispose();
                ReleaseSurface();
            }
            base.Dispose(disposing);
        }

        private void ReleaseSurface()
        {
            if (surfaceHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(surfaceHandle);
                surfaceHandle = IntPtr.Zero;
            }
            if (surface != null)
            {
                surface.Dispose();
                surface = null;
            }
            if (hitPath != null)
            {
                hitPath.Dispose();
                hitPath = null;
            }
        }

        private float GetScale()
        {
            uint dpi = NativeMethods.GetWindowDpi(Handle);
            return dpi / 96.0f;
        }

        private int ScaleValue(int value)
        {
            return (int)Math.Round(value * GetScale());
        }

        private static RectangleF ScaleRectangle(float x, float y, float width, float height, float scale)
        {
            return new RectangleF(x * scale, y * scale, width * scale, height * scale);
        }

        private static bool FontFamilyExists(string name)
        {
            try
            {
                using (FontFamily family = new FontFamily(name))
                    return string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    internal sealed class ThemePalette
    {
        public bool IsLight;
        public Color Border;
        public Color PrimaryText;
        public Color SecondaryText;
        public Color Divider;
        public Color ControlBackground;
        public int ShadowAlpha;

        public static ThemePalette ReadCurrent()
        {
            // The notch is always OLED dark, regardless of the system theme.
            return new ThemePalette
            {
                IsLight = false,
                Border = Color.FromArgb(255, 47, 47, 52),
                PrimaryText = Color.FromArgb(255, 246, 246, 247),
                SecondaryText = Color.FromArgb(255, 158, 158, 166),
                Divider = Color.FromArgb(255, 44, 44, 48),
                ControlBackground = Color.FromArgb(255, 31, 31, 34),
                ShadowAlpha = 82
            };
        }
    }

    internal static class VectorArtwork
    {
        public static Color GetPrayerColor(string prayer, bool lightTheme)
        {
            switch ((prayer ?? string.Empty).ToLowerInvariant())
            {
                case "fajr": return Color.FromArgb(255, 102, 112, 230);
                case "dhuhr": return Color.FromArgb(255, 226, 166, 42);
                case "asr": return Color.FromArgb(255, 218, 132, 55);
                case "maghrib": return Color.FromArgb(255, 215, 91, 76);
                case "isha": return Color.FromArgb(255, 139, 116, 203);
                default: return lightTheme ? Color.FromArgb(255, 85, 85, 92) : Color.FromArgb(255, 205, 205, 212);
            }
        }

        public static void DrawPrayerIcon(Graphics graphics, string prayer, RectangleF bounds, Color color)
        {
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 24.0f, bounds.Height / 24.0f);

            using (Pen pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (SolidBrush brush = new SolidBrush(color))
            {
                switch ((prayer ?? string.Empty).ToLowerInvariant())
                {
                    case "fajr":
                    case "isha":
                        using (GraphicsPath crescent = new GraphicsPath())
                        {
                            crescent.AddEllipse(4, 3, 16, 18);
                            crescent.AddEllipse(9, 1, 15, 16);
                            graphics.FillPath(brush, crescent);
                        }
                        if (string.Equals(prayer, "Isha", StringComparison.OrdinalIgnoreCase))
                        {
                            PointF[] star = { new PointF(19, 3), new PointF(20, 6), new PointF(23, 7), new PointF(20, 8), new PointF(19, 11), new PointF(18, 8), new PointF(15, 7), new PointF(18, 6) };
                            graphics.FillPolygon(brush, star);
                        }
                        break;
                    case "dhuhr":
                        graphics.FillEllipse(brush, 8, 8, 8, 8);
                        DrawSunRays(graphics, pen, 12, 12, 7.5f);
                        break;
                    case "asr":
                        graphics.FillEllipse(brush, 9, 10, 7, 7);
                        DrawSunRays(graphics, pen, 12.5f, 13.5f, 7.5f);
                        graphics.DrawLine(pen, 3, 20, 21, 20);
                        break;
                    case "maghrib":
                        graphics.DrawArc(pen, 7, 9, 10, 10, 180, 180);
                        graphics.DrawLine(pen, 3, 14, 21, 14);
                        graphics.DrawLine(pen, 5, 18, 19, 18);
                        graphics.DrawLine(pen, 8, 21, 16, 21);
                        break;
                    default:
                        graphics.FillEllipse(brush, 8, 8, 8, 8);
                        break;
                }
            }
            graphics.Restore(state);
        }

        private static void DrawSunRays(Graphics graphics, Pen pen, float cx, float cy, float radius)
        {
            for (int i = 0; i < 8; i++)
            {
                double angle = (Math.PI * 2.0 * i) / 8.0;
                float x1 = cx + ((float)Math.Cos(angle) * (radius - 2));
                float y1 = cy + ((float)Math.Sin(angle) * (radius - 2));
                float x2 = cx + ((float)Math.Cos(angle) * radius);
                float y2 = cy + ((float)Math.Sin(angle) * radius);
                graphics.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        public static void DrawClose(Graphics graphics, RectangleF bounds, Color color)
        {
            using (Pen pen = new Pen(color, Math.Max(1.5f, bounds.Width / 8.0f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            })
            {
                graphics.DrawLine(pen, bounds.Left + (bounds.Width * 0.2f), bounds.Top + (bounds.Height * 0.2f),
                    bounds.Right - (bounds.Width * 0.2f), bounds.Bottom - (bounds.Height * 0.2f));
                graphics.DrawLine(pen, bounds.Right - (bounds.Width * 0.2f), bounds.Top + (bounds.Height * 0.2f),
                    bounds.Left + (bounds.Width * 0.2f), bounds.Bottom - (bounds.Height * 0.2f));
            }
        }

        public static Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 232, 177, 50)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (GraphicsPath crescent = new GraphicsPath())
                {
                    crescent.AddEllipse(5, 4, 22, 24);
                    crescent.AddEllipse(12, 1, 21, 21);
                    graphics.FillPath(brush, crescent);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
        }
    }

    internal static class NativeMethods
    {
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int SWP_NOACTIVATE = 0x0010;
        public const int SWP_SHOWWINDOW = 0x0040;
        public const int SW_HIDE = 0;
        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int HTTRANSPARENT = -1;
        public const int MA_NOACTIVATE = 3;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;
            public SIZE(int width, int height) { Width = width; Height = height; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref POINT destination,
            ref SIZE size, IntPtr sourceDc, ref POINT source, int colorKey, ref BLENDFUNCTION blend, int flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr objectHandle);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr objectHandle);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static uint GetWindowDpi(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                return dpi == 0 ? 96u : dpi;
            }
            catch (EntryPointNotFoundException)
            {
                return 96;
            }
        }

        public static void EnablePerMonitorDpi()
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }
}
