# AGENTS.md — Prayer Reminder

Guidance for AI coding agents (and humans) working in this repo.

## 30-second map

- Single-file C# WinForms app: `PrayerReminder.cs` (~2150 lines, .NET Framework 4.x,
  no project file, no NuGet, no async/await).
- `build.bat` compiles with the `csc.exe` shipped in Windows and runs the regression
  tests. Green = `All prayer schedule checks passed.`
- The app is **location-agnostic**. Two ways to set it: a typed location
  (`location.json`, geocoded via Nominatim, times computed locally with the solar
  algorithm) or a CSV timetable (`prayer_time_table-*.txt`). A typed location takes
  priority. Never hardcode a city.

## Commands

| Task | Command |
|---|---|
| Build + test | `build.bat` |
| Tests only (after build) | `PrayerReminder.Tests.exe` |
| Preview the panel | `PrayerReminder.exe --test --prayer=maghrib --snapshot=preview.png` (process keeps running — kill it after the PNG appears) |
| Stop a running instance | `PrayerReminder.exe --stop` |

⚠️ **Tests need the timetable.** `PrayerReminder.Tests.cs` loads
`prayer_time_table-MALMO.txt` from the repo root. That file is gitignored (private
mosque data), so a fresh clone cannot run the tests until someone supplies a
365-day CSV with the expected header.

## Architecture (`PrayerReminder.cs`)

Line numbers are approximate and drift as the file changes — grep for the name.

| Class | ~line | Role |
|---|---|---|
| `Program` | 16 | Entry point: mutex, `--stop`/`--test`/`--prayer=`/`--snapshot=` args, timetable discovery, city resolution |
| `ReminderContext` | 137 | `ApplicationContext`: tray icon + menu, double-click → next prayer, `.stop-request` file watcher |
| `CrashLog` | 291 | Appends unhandled exceptions to `PrayerReminder-error.log` |
| `PrayerScheduler` | 312 | One-shot timer to the next prayer; re-evaluates on power resume + clock change; 10-min grace window with per-`(date, prayer)` dedupe |
| `ISchedule` | 668 | Interface: `FindNext`/`FindMostRecent`/`FormatToday` (both schedule types implement it) |
| `PrayerSchedule` | 413 | Loads/validates the CSV; `FindNext`/`FindMostRecent`/`FormatToday`; DST + leap-day logic |
| `PrayerOccurrence` | 584 | Name + When + IsTest value object |
| `SolarTimes` | 682 | Solar algorithm: Julian day, sun position, sunrise/sunset, Asr, Fajr/Isha + high-latitude fallback |
| `PrayerMethodParams` | 768 | Per-method Fajr/Isha angles + Isha-minutes (Umm al-Qura = Maghrib + 90) |
| `ComputedSchedule` | 813 | `ISchedule` from coordinates + method; converts UTC hours to the local time zone |
| `LocationConfig` | 870 | City + lat/lon + timezone + method value object |
| `LocationStore` | 890 | Reads/writes `location.json` (hand-rolled JSON, no dependencies) |
| `Geocoder` | 950 | Nominatim search + reverse (timezone); minimal JSON value extraction |
| `LocationDialog` | 1010 | "Change location…" form: place/coords + method dropdown |
| `NotificationWindow` | 598 | The notch panel: layered window, spring animation, all rendering |
| `ThemePalette` | 1206 | Fixed dark palette (intentionally ignores system theme) |
| `VectorArtwork` | 1232 | Procedural vector icons (per-prayer colors, tray crescent, close ✕) |
| `NativeMethods` | 1352 | P/Invoke: `UpdateLayeredWindow`, DC plumbing, `SetWindowPos`, DPI |

Key methods: `FindTimetablePath` (106), `ResolveCityName` (117),
`PrayerSchedule.Load` (426), `RemoveSourceDst` (553), `InterpolateLeapDay` (540),
`ShowPrayer` (664), `SaveSnapshot` (704), `RenderSurface` (882), `DrawSurface`
(901), `CreatePanelPath` (928), `DrawContent` (995), `Present` (1066), `WndProc`
(1124), `GetScale` (1176).

## Key invariants — read before editing

1. **Timetable contract.** CSV header must contain `date,fajr,sunrise,dhuhr,asr,
   maghrib,isha`; exactly 365 rows; times `hh:mm`; keyed by `MM-dd`. `Load` throws
   on any violation — keep it strict; the error messages are user-facing.
2. **DST convention.** `RemoveSourceDst` assumes the source table carries summer
   time from Mar 28 through Oct 31 (EU convention, hardcoded day-of-month). The
   tests pin 2026/2027 transition behavior. If you change this, update the tests
   in the same commit.
3. **City name is dynamic.** `ResolveCityName`: `location.txt` (first non-empty
   line) overrides; otherwise the filename part after the last dash, title-cased.
   It feeds the panel subtitle (`PRAYER TIME · CITY` / `TEST REMINDER · CITY`) and
   the tray tooltip. Don't hardcode a city anywhere.
4. **The panel is a layered window.** Every pixel goes through
   `RenderSurface → DrawSurface → Present` (`UpdateLayeredWindow`). WinForms
   painting (`OnPaint`) will not appear. Hit-testing is the panel `GraphicsPath`
   via `WM_NCHITTEST` (outside the panel = `HTTRANSPARENT`).
5. **Never steal focus.** `WS_EX_NOACTIVATE`, `ShowWithoutActivation`,
   `MA_NOACTIVATE` on `WM_MOUSEACTIVATE`, `SWP_NOACTIVATE` on `SetWindowPos`.
   Preserve all of these if you touch window code.
6. **Always dark.** `ThemePalette.ReadCurrent` is intentionally static (OLED
   notch) regardless of system theme.
7. **Geometry is 96-DPI base units.** Multiply by `GetScale()` (dpi/96). Base
   panel: 440×86 on a 440×110 canvas; collapsed pill: 132×46. Snapshot at 96 DPI
   is 440×110.
8. **Single instance.** Mutex `Local\PrayerReminder`. `--stop` writes a
   `.stop-request` file that the running instance's `FileSystemWatcher` picks up
   (it deletes the file, then exits).
9. **Scheduler dedupe.** Late reminders are keyed `yyyy-MM-dd:PrayerName` in the
   `delivered` set; keys older than 2 days are pruned. Don't deliver the same
   prayer twice per day.
10. **Computed schedule + timezone.** `ComputedSchedule` uses `SolarTimes.Compute`
    (UTC hours) converted via `TimeZoneInfo`. IANA ids (e.g. `Europe/Stockholm`) do
    NOT resolve on Windows — `FindSystemTimeZoneById` throws, so it falls back to
    `TimeZoneInfo.Local`. The regression tests validate the solar math via
    `SolarTimes.Compute` UTC hours (timezone-independent), not a specific zone.
    Umm al-Qura Isha = Maghrib + 90 min; high-latitude Fajr/Isha fall back to 1/7
    of the night.

## Verification workflow (no vision model needed)

1. Render: `PrayerReminder.exe --test --prayer=dhuhr --snapshot=snap.png`
   (kill the process once the PNG exists). `--next --snapshot=snap.png` renders the
   real next computed prayer (needs a `location.json` or CSV present).
2. Check pixels with Python/PIL — e.g. notch flat top, gradient
   `#000000 → #101014`, subtitle text region.
3. A/B diff: render two variants and compare the pixel diff; confirm only the
   intended region changed (this is how the dynamic city name was verified).
4. Tray gestures (double-click, right-click menu) live in the overflow flyout —
   desktop automation can't reach them; verify manually.

## Legacy files

The original Python/WebView2 prototype, the HTML design mockup, the old pixel-check
scripts, the old design snapshots, and the `icons/` SVG references were removed from
the folder on 2026-11-09 (housekeeping). `screenshot.png` is the current README
screenshot; regenerate it with `--next --snapshot=` (see Verification workflow).

## Style

- C# 4.0-era WinForms: explicit types, no `var`, no async/await, `internal`
  classes, one namespace (`PrayerReminder`).
- Keep it single-file; don't add project files or NuGet packages.
- Match existing naming (PascalCase, descriptive) and the existing comment style
  (sparse, only where behavior is non-obvious).

## Don't touch

- `prayer_time_table-MALMO.txt` — private mosque data (gitignored; required by the tests).
- `location.json`, `location.txt` — user-specific location (gitignored; created by the app).
- Test expectations in `PrayerReminder.Tests.cs` encode DST/leap/rollover
  behavior; change them only with a concrete reason.
