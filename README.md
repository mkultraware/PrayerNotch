# Prayer Reminder

A quiet, native Windows reminder for the five daily prayers. It lives in the system
tray, sleeps until the next prayer, and shows a brief dark "notch" panel at the
top-centre of the screen — no sound, no focus steal. Set your location once by
typing a city name (or pasting coordinates) and it computes the times locally — or
drop in a CSV timetable. It works for any location.

![The panel, previewing the next prayer](screenshot.png)

*The panel previewing the next prayer — double-click the tray icon to see it.*

## Quick start

| Action | File |
|---|---|
| Start for this session | `Start Prayer Reminder.bat` |
| Start + run at sign-in | `Enable Prayer Reminder.bat` |
| Stop + remove from sign-in | `Disable Prayer Reminder.bat` |

First run needs a build: double-click `build.bat`. It uses the .NET Framework
compiler included with Windows — no installer required.

## Using the tray

- **Double-click** the crescent to preview the actual next prayer (name + time).
- **Right-click** for today's times, "Change location…", "Show test reminder", or "Exit".
- The panel auto-hides after about 14 seconds; you can also click its ✕.

## Setting your location

There are two ways to tell the app where you are. Use whichever you prefer.

### Option A — type a location (recommended)

On first run, or any time via **Right-click → Change location…**, a small dialog
asks for a place. You can:

- Type a city name, e.g. `Malmö` or `Cairo`. The app looks it up with
  [OpenStreetMap Nominatim](https://nominatim.openstreetmap.org) — a free service
  that needs no account and sends only the place you type (no telemetry).
- Or paste coordinates directly, e.g. `55.605, 13.0038`.

You also choose a **calculation method** (Umm al-Qura is the default; MWL, ISNA,
Egyptian, Karachi, Dubai, and Tehran are available). The choice is saved to
`location.json` next to the app, and the times are computed locally from the
coordinates — no timetable file needed.

### Option B — drop in a CSV timetable

1. Put a CSV timetable named `prayer_time_table-{CITY}.txt` next to the app
   (e.g. `prayer_time_table-Malmö.txt`). The app auto-discovers the first
   `prayer_time_table-*.txt` it finds.
2. The city shown in the panel and tray tooltip is derived from the filename —
   the part after the last dash, title-cased.
3. Optional: create a `location.txt` next to the app with any city name you like
   (first non-empty line wins) to override the filename.

If both a `location.json` and a CSV are present, the typed location
(`location.json`) takes priority.

### Computed times

With a typed location, the app computes the times from the coordinates using the
standard solar algorithm (the same equations most prayer-time libraries use):
solar noon, sunrise/sunset (0.833° below the horizon), Asr (shadow factor 1), and
Fajr/Isha from the chosen method's angles. Umm al-Qura sets Isha to Maghrib + 90
minutes. At high latitudes in summer, where the sun never dips to the Fajr/Isha
angle, a 1/7-of-the-night fallback is used. Times are shown in your PC's local
time zone.

### Timetable format

```
date,fajr,sunrise,dhuhr,asr,maghrib,isha
2026-01-01,07:42,09:35,12:15,13:42,15:58,17:31
...
```

- 365 rows, one per day; times are `hh:mm`. `sunrise` is stored but never
  reminded on.
- The app expects exactly 365 days and a valid header; it refuses to start with a
  clear error otherwise.

## Time handling

- The table is keyed by **month/day**, so it is reused every year
  (Jan 1 → Jan 1, and so on).
- The table's own daylight-saving shift is removed at load, then Windows' local
  DST rule is applied for the active year — so times stay correct in any year,
  on any machine.
- February 29 is interpolated from February 28 and March 1.
- If the PC was asleep at a prayer time, a prayer within the last 10 minutes is
  shown once when it wakes.

## Build

`build.bat` compiles `PrayerReminder.exe` and the test harness with the .NET
Framework compiler included with Windows, then runs the schedule regression
checks (DST transitions, leap day, year rollover). No project file, no NuGet.

## Project layout

| File | Purpose |
|---|---|
| `PrayerReminder.cs` | The entire app (single file) |
| `PrayerReminder.Tests.cs` | Schedule regression checks |
| `location.json` | Your typed location + method (created by the app, git-ignored) |
| `build.bat` | Compile + run tests |
| `app.manifest` | Per-monitor DPI awareness |
| `Start/Enable/Disable Prayer Reminder.bat` | Session + sign-in management |
| `install_startup.ps1` / `disable_reminder.ps1` | Sign-in plumbing (HKCU Run key) |
| `screenshot.png` | Panel screenshot shown above |

## Notes

- Location lookup uses OpenStreetMap Nominatim (free, no account). It sends only
  the place you type; nothing else is transmitted.
- Single instance (named mutex); a second launch exits immediately.
- `PrayerReminder.exe --stop` asks the running instance to exit.
- Unhandled exceptions are appended to `PrayerReminder-error.log` next to the app.
- The panel is always dark (OLED style), regardless of the system theme.
- Fully DPI-aware (per-monitor v2).

See `AGENTS.md` for architecture notes aimed at developers and AI agents.

Last updated: 2026-11-09
