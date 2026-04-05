# Beet's Backup — AI Support Agent Reference

**Purpose:** This document is the authoritative reference for an AI support agent answering customer emails about Beet's Backup. Every feature, setting, behavior, file path, and known quirk is documented here. When a user asks a question, locate the relevant section and paraphrase the answer in a friendly, plain-English tone.

**Last updated:** 2026-04-04

---

## Table of Contents

1. [Application Overview](#1-application-overview)
2. [System Requirements](#2-system-requirements)
3. [Installation & First Launch](#3-installation--first-launch)
4. [User Interface Guide](#4-user-interface-guide)
5. [File Operations](#5-file-operations)
6. [Transfer Modes](#6-transfer-modes)
7. [Backup Wizard](#7-backup-wizard)
8. [Scheduled Backups](#8-scheduled-backups)
9. [Settings & Options](#9-settings--options)
10. [Advanced Features](#10-advanced-features)
11. [Troubleshooting](#11-troubleshooting)
12. [Log Files & Diagnostics](#12-log-files--diagnostics)
13. [Updates](#13-updates)
14. [Contact & Support](#14-contact--support)

---

## 1. Application Overview

**Beet's Backup** is a lightweight, portable Windows application that combines a dual-pane file manager with a full backup workflow engine. It is designed strictly for transferring and backing up files — not for launching or opening them.

**What it does:**
- Provides a side-by-side (source/destination) view of your drives and folders
- Copies, moves, and deletes files with multiple conflict-handling strategies
- Schedules automatic backups that run in the background while the app is open
- Verifies file integrity using SHA-256 checksums
- Handles locked/in-use files transparently using Windows Volume Shadow Copy (VSS)
- Maintains a full history log of every backup operation
- Strips NTFS permissions so backup files work cleanly on other machines

**Who it is for:** Home users and small office users who want a reliable, straightforward backup tool without cloud subscriptions, installer bloat, or complicated configuration. It ships as a single `.exe` file with no installer and no dependencies to manage.

**What it is not:** Beet's Backup cannot open, preview, or launch files. Double-clicking a file shows a reminder that this is a backup tool, not a file explorer.

---

## 2. System Requirements

| Requirement | Details |
|-------------|---------|
| **Operating System** | Windows 10 or later (64-bit / x64 only) |
| **.NET Runtime** | Not required — .NET 8 is bundled inside the `.exe` (self-contained build) |
| **Disk space** | Minimal for the app itself; destination drive must have enough space for the files being backed up |
| **Administrator rights** | Not required for most operations. Required only for the VSS Shadow Copy fallback (locked file handling). If VSS fails, try running as Administrator. |
| **Internet connection** | Not required. Used only for optional update checks (GitHub Releases API). |

---

## 3. Installation & First Launch

### Installing

Beet's Backup requires no installation. The steps are:

1. Download `BeetsBackup.exe`.
2. Save it anywhere you like (Desktop, Documents, a USB drive, etc.).
3. Double-click to run it. Windows may show a SmartScreen prompt the first time — click **More info** then **Run anyway**.

No installer, no registry changes, no dependencies.

### First launch

On first launch the app opens directly to the main window in **dark mode** with the toolbar set to **Simple mode**. No setup wizard is required to start browsing files.

To start a guided backup setup, click the **Backup Wizard** button in the toolbar (visible in Simple mode).

### System tray behavior

Beet's Backup lives in the **system tray** (bottom-right area of the Windows taskbar). Closing the window with the X button **does not quit the app** — it hides the window to the tray so scheduled backups keep running. To fully quit, right-click the tray icon and choose **Quit**.

### Single-instance behavior

If you try to launch a second copy of the app, the already-running instance will be brought to the foreground (or unhidden from tray) and the new copy will exit immediately. This is by design.

---

## 4. User Interface Guide

### Simple mode vs. Advanced mode

The toolbar has two modes, switchable via the **Simple/Advanced toggle** on the left side of the toolbar. The chosen mode is saved and restored every time the app starts.

**Simple mode** shows five buttons:
- **Visual** — toggle the donut pie chart view
- **Theme** — switch between dark and light themes
- **Split Pane** — open or close the second file pane
- **Refresh** — reload the current folder and recalculate folder sizes
- **Backup Wizard** — open the step-by-step backup setup wizard (purple badge button)

**Advanced mode** shows the full toolbar including:
- **Schedule** — open the schedule dialog to create/edit backup jobs
- **Jobs** — view scheduled jobs
- **Log** — view backup history
- **Options** — settings menu (theme, startup, update check)
- **Pause / Stop** — controls for active transfers
- **Remove Permissions** checkbox — strip NTFS ACLs on copy
- **Verify Checksums** checkbox — SHA-256 integrity check after every copy
- **Limit Speed** toggle — cap transfer bandwidth at 10 MB/s

### Dual-pane file browser

The main window is divided into **top pane** (source) and **bottom pane** (destination) when split pane mode is active.

- Each pane has an independent **drive selector** (sidebar with circular drive usage rings showing used/free/total space).
- Each pane has a **navigation bar** with Back, Forward, Up, path display, search box, and Refresh.
- Panes are resizable by dragging the **splitter** between them.
- In single-pane mode, only the top pane is visible.

### Navigation

- **Double-click a folder** to navigate into it.
- **Back / Forward / Up** buttons in the nav bar.
- **Path bar** — the current folder path is displayed; can be edited directly.
- **Hidden files** are visible and behave normally.

### Search

There are two search mechanisms:

**Live filter (secondary filter):**
- The text box in the navigation bar applies an instant name filter on the current folder listing.
- Typing in it narrows down what is visible — it does not recurse into subfolders.

**Deep recursive search:**
- Type a search term in the search box and press **Enter** or click the **magnifying glass** icon.
- The app searches the current folder and all its subfolders recursively.
- A **Path column** appears in results showing each file's parent directory.
- Extension-aware: `.exe` matches by extension, `exe` matches by name substring.
- A live status message shows search progress and result count.
- Click the **X** button in the search box to cancel search results and return to the normal folder view.
- Right-click any search result and choose **Open in Explorer** to jump to its location in Windows Explorer.

### Visual mode (pie/donut chart)

Click the **Visual** button to toggle between List view and a **donut pie chart** of the top 10 largest items in the current folder.

- Colors are distinct for each of the top 10 items, with a muted "Other" slice for the rest.
- The legend shows item name, icon, size, and percentage.
- **Hovering a slice highlights** the matching legend entry and vice versa.
- The chart rebuilds automatically when folder size calculations complete.
- Works in both single-pane and split-pane modes.

### Theme switching

Click the **Theme** button (or use **Options > Theme** in Advanced mode) to toggle between dark and light themes. The chosen theme is saved and applied on next launch.

---

## 5. File Operations

### Navigating folders and drives

- Click a drive in the left sidebar to load its root directory.
- Double-click folders to navigate into them.
- Use the Back, Forward, and Up buttons in the navigation bar.
- The drive sidebar shows circular usage rings indicating used vs. free space for each drive.

### Copying files

- **Top to bottom:** select items in the top pane, then use the toolbar copy button or right-click > Copy, then Paste in the bottom pane — or use drag-and-drop.
- **Bottom to top:** same process in reverse.
- The **transfer mode** (Skip Existing, Keep Both, Replace, Mirror) controls what happens when a file of the same name already exists at the destination. See Section 6.

### Moving files

- Use right-click > Cut, then Paste, or drag-and-drop with the appropriate keyboard modifier.
- During a move, the app copies the file first; it only deletes the source if the copy succeeded with no errors. If any files fail to copy, the source is preserved.

### Deleting files

- Right-click > Delete sends files to the **Windows Recycle Bin**. Files are not permanently deleted immediately.
- Mirror mode cleanup also sends removed files to the Recycle Bin (see Section 6).

### Renaming files and folders

- Right-click any file or folder and choose **Rename**. A rename dialog appears.

### Drag and drop

- Drag files or folders from one pane to the other.
- Works in both single-pane and split-pane modes.

### Creating new folders

- Right-click in an empty area of the file list and choose **New Folder**.

### Open in Explorer

- Right-click any file or folder and choose **Open in Explorer**.
- For folders: opens the folder directly in Windows Explorer.
- For files: opens the parent folder with the file pre-selected.
- Also works on deep search results, making it easy to locate a file found deep in a directory tree.

### Double-clicking files

- Double-clicking a file shows a reminder that Beet's Backup is a backup tool, not a file launcher. Files cannot be opened from within the app.

### Folder sizes

- Folder sizes are calculated asynchronously in the background.
- Progress indicators show while sizes are computing.
- Click **Refresh** to reload the pane and recalculate all folder sizes.

---

## 6. Transfer Modes

The transfer mode determines what happens when a file being copied **already exists** at the destination. Select the mode before starting a transfer.

### Skip Existing (default)

- If a file already exists at the destination and it is **identical** (same size and the source is not newer), it is skipped.
- If the source file is **different** (different size or the source has a newer modification date), it is overwritten with the updated version.
- Best for: regular incremental backups where you want to add new files and update changed ones without touching anything else.

### Keep Both

- If a file already exists at the destination, the incoming copy is saved with a new name (e.g. `report-1.docx`, `report-2.docx`).
- The original destination file is never touched.
- Best for: situations where you want to preserve every version of a file.

### Replace

- If a file already exists at the destination, it is always deleted and replaced with the source copy, regardless of whether the content is the same.
- Best for: when you want a fresh identical copy and do not care about preserving destination files.

### Mirror (Sync)

**Important — this mode deletes files from the destination.**

- First, all new and changed files are copied from source to destination (using the same identical-file check as Skip Existing).
- Then, any files or folders at the destination that do **not exist** in the source are **deleted** (sent to Recycle Bin).
- The result is that the destination becomes an exact mirror of the source.
- A **prominent confirmation warning** is shown before the cleanup phase begins.
- **Safety guard:** if the source folder is detected as empty (e.g. a disconnected drive), the cleanup phase is automatically skipped to prevent accidental mass deletion.
- Best for: keeping an external backup drive perfectly in sync with your source, where you want deletions to be reflected in the backup.

**If Mirror mode deleted files you did not intend to remove:** check the Windows Recycle Bin — Mirror deletes go there, not permanent deletion.

---

## 7. Backup Wizard

The Backup Wizard is a step-by-step guided flow for setting up a backup job. It is the recommended starting point for new users.

### How to access it

The **Backup Wizard** button is visible only in **Simple toolbar mode** (the purple badge button at the right end of the toolbar). Switch to Simple mode using the toggle on the left of the toolbar if it is not visible.

### Wizard steps overview

The wizard walks through up to 7 steps depending on choices made. A dot indicator at the top of each page shows progress.

---

#### Step 1 — Type: What kind of backup?

Three options:

- **One-time backup (starting now)** — runs immediately when you finish the wizard. No scheduling.
- **Scheduled (one-time)** — runs once at a specific date and time you choose.
- **Recurring** — runs automatically on a repeating schedule (Every 6 Hours, Every 12 Hours, Daily, Weekly, or Monthly).

*Default selection: Recurring.*

If **One-time backup (starting now)** is selected, the Schedule step is skipped.

---

#### Step 2 — When: Schedule (skipped for "one-time now")

- **Job Name** — give the backup a name (default: "My Backup").
- **Date and time picker** — choose the first run date and time.
- **Recurrence** (shown for Recurring only) — select the repeat interval: Every 6 Hours, Every 12 Hours, Daily, Weekly, or Monthly.

---

#### Step 3 — Source: What to back up?

Three source selection modes:

**Quick Pick** — checkboxes for common Windows folders:
- Documents
- Pictures and Videos
- Desktop
- Downloads (unchecked by default)

**Entire Drive** — a drive picker; backs up the entire selected drive root.

**Custom** — add specific folders using a folder browser dialog. Multiple folders can be added. Remove individual folders from the list as needed.

---

#### Step 4 — Destination: Where to save it?

- Select a destination **drive** from the list and optionally specify a **subfolder** name within it.
- Or click **Browse** to pick any specific folder on any drive.
- **Same-drive warning:** if the destination is on the same physical drive as the source, a warning is shown. Backing up to the same drive is not recommended (if the drive fails, both source and backup are lost).

---

#### Step 5 — Mode: What if a file already exists?

Select the transfer mode (see Section 6 for full explanations):

- **Skip files already there** (Skip Existing)
- **Keep both copies** (Keep Both)
- **Replace with newer version** (Replace)
- **Mirror (make destination identical)** — with a note about deletions

*Default: Skip Existing.*

---

#### Step 6 — Options: Advanced settings (optional)

All options are off by default. Most users can skip this step.

- **Verify Checksums** — enables SHA-256 verification after each file copy (slower but confirms data integrity).
- **Remove Permissions** — strips NTFS access control lists (ACLs) from copied files so they are fully accessible on other machines or user accounts.
- **Speed Limit (throttle)** — caps transfer speed at a selected value: 1, 5, 10, 25, 50, or 100 MB/s.
- **Exclusion Filters** — skip files matching a pattern. Type a pattern and click Add:
  - Extension patterns: `*.tmp`, `*.log`, `*.bak`
  - Exact name matches: `Thumbs.db`, `node_modules`, `desktop.ini`

---

#### Step 7 — Review: Summary

A final summary shows all chosen settings:
- Backup type and schedule
- Source paths
- Destination path
- Transfer mode
- Options (checksum, permissions, throttle, exclusions)
- **Estimated size** — automatically calculated from the source paths; shown as total data volume

If anything looks wrong, click **Back** to return to the relevant step.

#### Finishing the wizard

- For **One-time now:** the finish button reads "Start Backup Now". The backup starts immediately and appears in the log.
- For **Scheduled or Recurring:** the finish button reads "Schedule Backup". The job is saved and appears in the Jobs list. The app must be running for it to execute at the scheduled time.

---

## 8. Scheduled Backups

### How scheduled backups work

Beet's Backup runs a **background scheduler** (checking every minute) while the app is open. When a job's scheduled time arrives, it runs automatically without any user interaction. Jobs persist to disk and survive app restarts.

**Critical:** The app must be running (at minimum in the system tray) for scheduled backups to execute. If the app is fully closed, jobs will not run.

**Recommended:** Enable **Launch at Startup** in the Options menu so the app starts automatically with Windows and hides to the tray.

### Creating a scheduled job via the Wizard

See Section 7 (Backup Wizard) — the easiest way for most users.

### Creating a scheduled job via the Schedule dialog (Advanced mode)

In Advanced toolbar mode, click **Schedule** to open the schedule dialog directly. Configure:
- Source folder(s)
- Destination folder
- Date, time, and recurrence
- Transfer mode
- Strip permissions toggle
- Verify checksums toggle
- Exclusion filters
- Per-job speed limit (1–100 MB/s)
- **Estimate Size** button — calculates total source data volume respecting active filters

### Recurring backup intervals

| Option | Interval |
|--------|----------|
| Every 6 Hours | Repeats every 6 hours |
| Every 12 Hours | Repeats every 12 hours |
| Daily | Repeats every 24 hours |
| Weekly | Repeats every 7 days |
| Monthly | Repeats every 30 days |

### Missed backup detection

When the app starts, it checks whether any scheduled jobs were due to run while the app was closed. If missed jobs are found:

- A **Missed Backups dialog** opens automatically.
- The user can choose **Run All Now** to execute all missed jobs immediately, or **Skip** to dismiss them (the jobs will reschedule to their next due time).

If the app was launched via the Windows startup shortcut (`--startup` flag) and there are missed backups, the main window opens at full size (rather than hiding to tray) to ensure the dialog is visible.

### Viewing and managing jobs

In Advanced toolbar mode, click **Jobs** to open the Jobs dialog. From here:
- View all scheduled jobs with their name, source, destination, next run time, and status.
- Delete a job.
- Enable or disable individual jobs.

### Pausing a running scheduled job

Open the **Log** dialog (Advanced mode > Log). Find the running job entry. Click the **Pause** button to pause it. Click again to resume.

---

## 9. Settings & Options

Settings are accessed via the **Options** menu in Advanced toolbar mode. All settings persist in `settings.json` (see Section 12 for path).

### Launch at Startup

- **What it does:** Creates a Windows shortcut in the startup folder so the app launches automatically every time Windows starts.
- **How it works:** The shortcut uses a `--startup` flag. When the app sees this flag, it launches silently to the system tray (no window shown), unless there are missed backups.
- **To enable/disable:** Options menu > Launch at Startup toggle. Or in the wizard if prompted.
- **Shortcut location:** `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Beet's Backup.lnk`

### Theme preference

- Toggle between **Dark** (default) and **Light** using the Theme button or Options menu.
- The chosen theme is saved and applied on every launch.

### Simple / Advanced mode

- Toggle using the switch on the left side of the toolbar.
- The preference is saved and restored on next launch.
- Simple mode: 5 toolbar buttons (Visual, Theme, Split Pane, Refresh, Backup Wizard).
- Advanced mode: full toolbar with all controls.

### Check for Updates

- Options menu > Check for Updates runs an immediate update check.
- The app also checks automatically in the background 3 seconds after launch.
- If a newer version is found, a banner appears in the status bar with **Download** and **Dismiss** buttons.
- Clicking **Dismiss** permanently suppresses that specific version (saved to settings as `SkippedVersion`). The user will still be notified about future versions.

---

## 10. Advanced Features

### VSS Shadow Copy (locked file handling)

**What it is:** Windows Volume Shadow Copy Service (VSS) allows reading a file even when another program has it open and locked — for example, an Outlook `.pst` file, a live database, or any file in use by a running application.

**How it works in Beet's Backup:**
1. When a file copy fails because the file is locked, the app **retries up to 3 times** with 500 ms delays between attempts.
2. If all retries fail, the app **creates a VSS snapshot** of the volume containing that file.
3. The file is then read from the shadow copy instead of the live file — no interruption to the program using it.
4. **Snapshots are cached per volume** for the duration of the transfer session — if multiple locked files are on the same volume, only one snapshot is created.
5. All snapshots are **automatically deleted** when the transfer completes.
6. The transfer summary reports how many files required the shadow copy fallback.

**No action is required from the user.** This happens transparently.

**If VSS fails:** VSS may require administrator rights on some Windows configurations. Try right-clicking `BeetsBackup.exe` and choosing **Run as administrator**. Any VSS errors are recorded in `operational.log`.

### SHA-256 Checksum Verification

- Enabled via the **Verify Checksums** checkbox in Advanced toolbar mode, or in the wizard's Options step.
- After every file is copied, the app computes a SHA-256 hash of both the source and destination files and compares them.
- If they do not match, a **"Checksum mismatch"** error is recorded for that file in the log.
- **Performance note:** verification reads every file twice (once during copy, once after), so transfers take longer. Recommended for critical backups where data integrity must be confirmed.

### NTFS Permission Stripping (Remove Permissions)

- Enabled via the **Remove Permissions** checkbox in Advanced toolbar mode, or in the wizard's Options step.
- Strips the NTFS Access Control List (ACL) from every copied file so it inherits permissions from its new parent folder.
- Useful when backing up to a drive that will be used on a different machine or under a different user account — without this, files may appear as inaccessible or "locked" to other users.
- Does not modify the source files, only the destination copies.

### Transfer Throttling (Speed Limit)

- **Manual throttle (Advanced mode):** Enable the **Limit Speed** toggle on the toolbar. This caps transfer speed at exactly **10 MB/s**.
- **Per-job throttle (Schedule dialog / Wizard):** A speed picker lets you choose 1, 5, 10, 25, 50, or 100 MB/s for a specific job.
- **When to use:** Enable throttling when running a large backup and you need to keep the computer or network responsive for other tasks.
- Throttling works by pacing file copy chunks with timed delays — it also supports Pause/Resume and cancellation during throttled transfers.

### Exclusion Filters

- Available in the schedule dialog and wizard Advanced Options step.
- Skip files matching patterns. Two pattern types:
  - **Extension patterns** (e.g. `*.tmp`, `*.log`, `*.bak`) — matches any file ending with that extension.
  - **Exact name matches** (e.g. `Thumbs.db`, `node_modules`, `desktop.ini`) — matches files or folders with that exact name.
- Exclusions are applied during the file count, size estimation, and the transfer itself.
- Each scheduled job has its own independent exclusion list.

### Mirror Mode Cleanup

- After copying files, Mirror mode scans the destination for files and folders that do not exist in the source and sends them to the **Windows Recycle Bin**.
- **Safety guard:** if the source folder is empty (e.g. the source drive was disconnected), the cleanup phase is skipped entirely and a warning is logged. This prevents accidentally wiping the destination when the source is not actually there.

### Size Estimation

- The **Estimate Size** button in the schedule dialog and wizard calculates the total byte count and file count of all source paths, applying any active exclusion filters.
- This runs automatically at the start of every scheduled job so the log can show estimated vs. actual data transferred.
- The wizard summary page shows the estimate automatically before finishing.

---

## 11. Troubleshooting

### "The app won't open" / "Nothing happens when I double-click"

**Most likely cause:** The app is already running in the system tray.

- Look for the Beet's Backup icon in the system tray (bottom-right of the taskbar, near the clock). You may need to click the up arrow to see hidden tray icons.
- Click the tray icon to show the window, or right-click it and choose **Show**.
- If the app is definitely not running, try right-clicking `BeetsBackup.exe` and selecting **Run as administrator**.

### "I see a Windows SmartScreen warning"

This appears the first time you run a downloaded executable that is not code-signed. Click **More info**, then **Run anyway**. The app is safe.

### "My scheduled backup didn't run"

Scheduled backups only run while Beet's Backup is **open** (even if minimized to the tray). If the app was closed, the backup did not run.

**Solutions:**
1. Enable **Launch at Startup** in the Options menu so the app starts automatically with Windows.
2. When the app starts after a missed backup, it will show a **Missed Backups dialog** offering to run them immediately.
3. Check the backup log (Advanced mode > Log) to confirm whether the job ran or was recorded as interrupted.

### "A scheduled backup shows 'Interrupted' in the log"

This means the app was closed while the backup was actively running. The log marks it as interrupted to distinguish it from a clean failure. Check how much data was transferred before the interruption — partial transfers are visible in the stats.

### "Some files were skipped" / "Transfer completed with errors"

Several reasons a file might be skipped or fail:

- **File is locked/in use:** The app retries 3 times and then tries VSS Shadow Copy. If VSS also fails (requires admin rights), the file is recorded as locked. The solution is to run the app as administrator, or close the program using that file.
- **Disk full:** The destination drive ran out of space mid-transfer. The log records "Disk full" errors for affected files. Free up space on the destination and retry.
- **Checksum mismatch:** If Verify Checksums is enabled and a mismatch is found, the file is flagged as failed. This indicates a read error or hardware issue. Retry the transfer; if it persists, check the source drive's health.
- **Access denied:** The app does not have permission to read the source file. Run as administrator.

To see the full list of which files failed and why: open the **Log** dialog, select the entry, and click **View Errors**.

### "Transfer seems slow"

Check whether **Limit Speed** is enabled on the toolbar (Advanced mode). If the toggle is on, transfer speed is capped at 10 MB/s. Turn it off if you want full-speed transfers.

Also check whether **Verify Checksums** is enabled — verification reads each file twice, roughly halving effective throughput.

### "Mirror mode deleted files I didn't expect it to"

Mirror mode is designed to delete files from the destination that don't exist in the source. These files go to the **Windows Recycle Bin** — they are not permanently deleted. Open the Recycle Bin and restore the files you need. Before using Mirror mode again, double-check that the source path is correct and the drive is connected.

### "The app disappeared from the taskbar"

It moved to the system tray. Click the tray icon (or the up arrow) in the bottom-right of your taskbar.

### "A second window opened briefly and closed"

You launched a second copy of the app. The second copy detects the first instance already running, signals it to come to the front, and exits. This is normal behavior.

### "Scheduler error appears in the status bar"

The status bar in the main window reports scheduler errors immediately. Common causes:
- Destination drive is disconnected or full.
- Source folder no longer exists.

Check the source and destination paths in the Jobs dialog, and review `operational.log` for the detailed error message.

### "The update banner appeared — should I update?"

An update banner in the status bar means a newer version of Beet's Backup is available. Click **Download** to go to the release page. Click **Dismiss** to permanently suppress that version (you will still be notified about future updates).

---

## 12. Log Files & Diagnostics

All log files and data files are stored in a single folder. To open it: in Advanced toolbar mode, click **Log** > **Open Log Folder**. Or navigate there manually:

**Log folder path:**
```
%LOCALAPPDATA%\Beet's Backup\
```

**To navigate there manually:**
1. Press `Win + R` to open the Run dialog.
2. Type `%localappdata%\Beet's Backup` and press Enter.
3. Windows Explorer opens the folder directly.

On most systems this resolves to something like:
```
C:\Users\YourName\AppData\Local\Beet's Backup\
```

---

### Files in this folder

#### `operational.log`
- **What it contains:** A timestamped log of all app activity — transfers started and completed, files copied/skipped/failed, scheduler events, VSS snapshot creation, startup/shutdown events, update checks.
- **Format:** Plain text. Each line: `[YYYY-MM-DD HH:mm:ss.fff] [LEVEL] message`
- **Levels:** INFO, WARN, ERROR, FATAL
- **Size limit:** Automatically rotated at 10 MB. When the log reaches 10 MB, it is renamed `operational.log.1` and a fresh log is started.
- **When to send to support:** For any unexplained behavior — slow transfers, scheduler not running, VSS errors.

#### `crash_dump.log`
- **What it contains:** Detailed crash reports written when the app encounters an unhandled exception. Includes: app version, OS version, .NET version, process memory usage, thread count, full stack trace, and all inner exceptions.
- **Format:** Plain text with structured sections.
- **Size limit:** Same 10 MB rotation as operational.log.
- **When to send to support:** If the app crashes, closes unexpectedly, or becomes unresponsive. This file is the most useful for diagnosing crashes.

#### `backup_log.json`
- **What it contains:** The complete history of all backup operations — job names, source/destination paths, timestamps, files copied/skipped/failed, bytes transferred, and per-file error details (up to 200 error entries per job). Capped at 500 total log entries.
- **Format:** JSON array. Viewable from inside the app via Advanced mode > Log.
- **When to send to support:** If a backup job shows unexpected results and the in-app log view is not sufficient detail.

#### `settings.json`
- **What it contains:** User preferences — dark/light mode, Simple/Advanced toolbar mode, Launch at Startup flag, and the version tag for any dismissed update notification.
- **Format:** JSON. Can be deleted to reset all settings to defaults (the app will recreate it on next launch).
- **When to send to support:** If settings appear to not be saving or are behaving unexpectedly.

#### `scheduled_jobs.json`
- **What it contains:** All configured scheduled backup jobs — names, source paths, destination paths, schedules, transfer modes, options, and enabled/disabled state.
- **Format:** JSON. Can be deleted to remove all scheduled jobs (the app will recreate an empty file).
- **When to send to support:** If scheduled jobs appear corrupt or won't load.

---

### How to send logs to support

**Preferred:** Open the Log folder (see above) and attach the relevant files to your support email. For crashes, always include `crash_dump.log`. For unexpected behavior, include `operational.log`.

**Alternative:** Open the file in Notepad, select all (`Ctrl+A`), copy (`Ctrl+C`), and paste into the email body.

---

## 13. Updates

### Automatic update checking

On every launch, the app checks for a newer release 3 seconds after startup (to allow the UI to fully load first). The check queries the **GitHub Releases API** — it requires a brief internet connection.

If a newer version is found:
- An **accent-colored banner** appears in the status bar at the bottom of the window.
- The banner shows the new version number and has two buttons: **Download** and **Dismiss**.
- **Download** opens the release page in your browser.
- **Dismiss** suppresses notifications for that specific version permanently (the app will still notify about future versions).

If no internet connection is available, or the check fails for any reason, it fails silently — no error is shown to the user.

### Manual update check

In Advanced toolbar mode: **Options > Check for Updates**. This triggers an immediate check and shows the result in the status bar.

### Where to download updates

Updates are available on the GitHub Releases page for the project. The Download button in the update banner will take you directly there. There is no auto-installer — download the new `.exe`, replace the old one, and run it.

---

## 14. Contact & Support

[**Support email:** *(placeholder — add support email address here)*]

[**Website / documentation:** *(placeholder — add URL here)*]

When contacting support, please include:
- A description of what you were trying to do and what happened instead.
- Your Windows version (Settings > System > About).
- The `operational.log` file from `%LOCALAPPDATA%\Beet's Backup\`.
- The `crash_dump.log` file if the app crashed.
- Screenshots if the issue is visual.

---

*End of support reference document.*
