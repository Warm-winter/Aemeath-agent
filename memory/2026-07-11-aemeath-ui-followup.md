# Debug Report - 2026-07-11

## Symptoms

The pet follow command was nested unnecessarily; the chat session pane did not share the main layout baseline; custom-title-bar windows retained a white native frame and an invisible minimize glyph; settings initially showed an empty page or stale selection; the unsaved dialog duplicated Cancel with a close button; and settings notifications used the default Fluent appearance and timing.

## Root Causes

The pane and content owned separate margins. The minimize glyph used a zero-height stretched path. `BorderOnly` left Win32 non-client pixels visible even after setting the DWM border color. Settings navigation had three competing state sources: XAML selection, a `SelectedItem.Tag` binding, and code-behind. Dialog and notification shells did not expose flow-specific presentation.

## Fix

Flattened the follow command, centralized `SplitView` spacing, replaced the minimize glyph, and introduced `WindowsWindowFrame` hooks for `WM_NCCALCSIZE` and `WM_NCHITTEST` while retaining `BorderOnly`. Settings now synchronizes navigation and content through one method. The unsaved dialog hides its redundant close button, and `AemeathToastHost` provides themed success/error feedback with reduced-motion support.

## Evidence

- Debug and Release builds: 0 warnings, 0 errors.
- Avalonia/xUnit suite: 67/67 passed.
- `build.bat --no-pause`: publish succeeded.
- `git diff --check`: passed.
- Real Windows 11 HWND smoke: top pixels were `#FFF8FB` with no white frame; edge hit tests returned `HTTOP`/`HTLEFT`; maximized client bounds exactly matched the monitor work area.

## Regression Tests

Coverage includes menu hierarchy, 720/940/1200 px chat alignment, title-bar glyph state, resize hit testing, initial settings content/selection, dialog close-button variants, toast timing/replacement, reduced motion, and live announcements.

## Status

DONE
