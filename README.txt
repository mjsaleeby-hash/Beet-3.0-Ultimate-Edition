================================================================================
  BEET'S BACKUP
  Version 4.0.0
  Beet Software
================================================================================

NOTE ON THIS FILE
-----------------
This is the plain-text summary, for reading without a Markdown viewer.
README.md is the CANONICAL, full reference — features, project structure,
tech stack, and data locations are documented there in full. If the two ever
disagree, README.md is correct.


WHAT IT IS
----------
A lightweight, portable dual-pane file manager and backup tool for Windows.
Ships as a single .exe with no installer required.

Built with WPF and .NET 8. Beet's Backup is designed strictly for managing,
backing up, and transferring files -- not launching them. It provides a
backup-health dashboard, side-by-side drive browsing, scheduled backups that
run through Windows Task Scheduler, SHA-256 checksum verification, file
versioning, and a full transfer log, all in one self-contained executable.


WHAT IT DOES (SUMMARY)
----------------------
Dashboard
  - Protected / At Risk status banner (green requires a successful backup
    within the last 7 days)
  - Protection Summary, Backup Schedule, and Recent Backups cards
  - Drive sidebar with usage rings and a Total Capacity tile
  - Crash recovery banner with one-click diagnostics export

File management
  - Dual-pane browser, single-pane mode, drag-and-drop, split pane
  - Right-click menu: Open in Explorer, Copy to Bottom / Top, Cut to
    Bottom / Top, Rename, Delete (to the Recycle Bin), Previous Versions,
    Extract Here, Extract To. Copy and Cut transfer straight to the other
    pane -- there is no clipboard paste step and no New Folder command.
  - Deep recursive search with a Path column, plus a live secondary filter
  - Previous Versions -- browse and restore archived copies of any file
  - Open in Explorer; async folder sizing; long path support
  - Files cannot be opened or launched from inside the app, by design

Transfers
  - Modes: Skip Existing, Keep Both, Replace, Mirror (Sync)
  - Keep Both writes "name-1.ext", "name-2.ext", ...
  - Mirror deletes destination extras to the Recycle Bin, with a confirmation
    warning and an empty-source safety guard
  - NTFS permission stripping; SHA-256 verification; pause / resume / stop
  - 10 MB/s throttle toggle; per-job speed limit of 1-100 MB/s
  - VSS shadow copy fallback for locked / in-use files
  - Pre-flight disk space preview (Sufficient / Tight / Insufficient / Unknown)
  - Archive Now (create a zip); Extract Here / Extract To... (zip-slip safe)

Backup wizard
  - 7 guided steps (6 when running immediately): Type, When, Source,
    Destination, Mode, Options, Review

Scheduled backups
  - One-time or recurring: Every 6 Hours, Every 12 Hours, Daily, Weekly,
    Monthly
  - Registered with Windows Task Scheduler as "BeetsBackup_{guid}" so backups
    run even when the app is closed
  - Headless CLI mode: BeetsBackup.exe --run-job {guid}
  - Missed backup detection on startup; toast notifications on completion
  - Per-job versioning (hidden .versions folder, timestamped copies) and
    compression (single timestamped .zip); the two are mutually exclusive
  - Exclusion filters by extension pattern or exact name

Backup log
  - Persistent JSON history with per-file error reasons, retry, CSV export

UI
  - Dark and light themes
  - Unified command bar: Back up now, Pause, Stop, Split Pane, Schedule,
    Backup Wizard, Jobs, Log, Options, Theme, Visual/List
  - Options menu: Remove Permissions, Verify Checksums, Throttle (10 MB/s),
    Archive Now, Launch at Startup, Start Minimized to Tray,
    Check for Updates, Export Diagnostics for Support..., View User Guide (FAQ)
  - Donut chart "visual mode" of the largest items in a folder, with a
    keyboard-accessible legend
  - System tray, single-instance enforcement, GitHub Releases update checker


REQUIREMENTS
------------
  Operating system    Windows 10 version 1607 or later, or Windows 11 (x64)
  .NET runtime        Not required -- .NET 8 is bundled in the self-contained
                      exe
  Administrator       Required. The manifest requests requireAdministrator, so
                      Windows shows a UAC prompt at launch. VSS shadow copies
                      of locked files need it.


INSTALLING
----------
Option A -- download the executable

  1. Download BeetsBackup.exe from the repository root (or the Releases page
     if one is published).
  2. Run it and accept the UAC prompt. No installer, no dependencies.

Option B -- build from source

  1. Install the .NET 8 SDK:
     https://dotnet.microsoft.com/download/dotnet/8.0

     global.json pins the SDK to 8.0.419 with rollForward "latestFeature",
     so you need an 8.0 SDK at 8.0.419 or newer. A .NET 9 or 10 SDK on its
     own will NOT satisfy it.

  2. Clone the repository:

       git clone https://github.com/mjsaleeby-hash/Beet-3.0-Ultimate-Edition.git
       cd Beet-3.0-Ultimate-Edition

  3. Publish. Every publish setting (single file, self-contained, win-x64,
     ReadyToRun, compression) already lives in the .csproj, so no extra flags
     are needed:

       dotnet publish BeetsBackup.csproj -c Release

  The exe lands in bin\Release\net8.0-windows\win-x64\publish\, and a
  post-publish target also copies BeetsBackup.exe and the user-guide PDF one
  level ABOVE the repository folder.

  For a step-by-step walkthrough aimed at non-developers, read
  "READ THIS FIRST. HOW TO BUILD.txt" in the repository root.

Running the tests

    dotnet test BeetsBackup.Tests/BeetsBackup.Tests.csproj

  244 tests, all passing as of 2026-08-18.


WHERE YOUR DATA LIVES
---------------------
Everything the app writes is in one folder:

    %LocalAppData%\Beet's Backup\

    operational.log       Timestamped activity log, rotated at 10 MB
    crash_dump.log        Unhandled-exception reports
    backup_log.json       Backup history (max 500 entries)
    settings.json         Theme, startup options, skipped update version
    scheduled_jobs.json   All configured backup jobs

Scheduled tasks appear in Windows Task Scheduler as "BeetsBackup_*".


MORE DOCUMENTATION
------------------
  README.md                        Full reference (canonical)
  READ THIS FIRST. HOW TO BUILD.txt  Step-by-step build walkthrough
  docs\user-guide.txt              End-user guide (also shipped as a PDF)
  docs\support-reference.md        Reference for support agents
  PerformanceMonitor\README.md     External performance monitoring tool


LICENSE
-------
This project is provided as-is for personal use.
