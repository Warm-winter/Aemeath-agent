# Debug Report - 2026-07-10

## Symptom

The desktop pet rendered with an opaque black square; settings navigation prompted about unsaved work without edits; native captions conflicted with the application theme; MCP/Skill lists changed height after selection; chat actions disappeared until hover; and attachments were stored as filename text instead of rendered media/cards.

## Root Cause

The idle GIF had been resized from an already damaged palette image, converting transparent corners to opaque black. Settings dirty flags were event-driven rather than derived from saved form state. Navigation depended on mutable indices and group labels were embedded in selectable items. MCP/Skill wide layouts used content-sized rows plus list minimum heights. Chat messages had no attachment persistence model, and action visibility was tied to pointer events.

## Fix

Regenerated the GIF from the Git baseline with preserved alpha, duration, and disposal metadata. Added themed custom title bars, snapshot-based dirty tracking, stable `SettingsPageId` navigation, three-way save/discard/cancel dialogs, fixed responsive list sizing, flattened pet menus, persistent message attachments, bounded ImageSharp thumbnails, unavailable-file cards, and permanent semantic action bars.

## Evidence

- Debug and Release builds: 0 warnings, 0 errors.
- Avalonia/xUnit suite: 49/49 passed.
- `build.bat --no-pause`: publish succeeded.
- `git diff --check`: passed (line-ending conversion notices only).

## Regression Tests

Coverage is under `tests/Aemeath.Desktop.Tests`, including pet transparency/menu order, title-bar state, settings decisions/snapshots, responsive panel sizing, attachment JSON compatibility, thumbnail/fallback rendering, and retry attachment forwarding.

## Status

DONE
