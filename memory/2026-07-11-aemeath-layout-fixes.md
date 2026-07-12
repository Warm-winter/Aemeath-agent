# Debug Report - 2026-07-11

## Symptoms

The chat session pane left a tall gray strip in its 12 px gap; long assistant bubbles extended beyond the message viewport when the pane was open; the AI provider page left excessive vertical space instead of filling the content area; and pet context-menu labels were not centered.

## Root Causes

Avalonia Fluent supplied `SplitView.PaneBackground` as `#F2F2F2`, which was exposed by the intentional pane gap. Dynamically built message grids measured wider than `MessagesPanel` because the 720 px bubble cap did not include the avatar column. The provider grid used an auto-height wide row and a 500 px list cap. Pet menu headers retained the Fluent template's stretched header presenter.

## Fix

Made the chat pane background transparent and bound each message row width to the live `MessagesPanel.Bounds.Width`. Wide provider panes now use a star row and an uncapped provider list, while narrow mode retains a 220 px cap. Pet menu items receive a recursive `pet-menu-item` class whose theme selector centers `PART_HeaderPresenter`. The supplied screenshot was removed after verification.

## Evidence

- Debug and Release builds: 0 warnings, 0 errors.
- Avalonia/xUnit suite: 69/69 passed.
- `build.bat --no-pause`: publish succeeded.
- `git diff --check`: passed.
- Headless layout checks cover transparent pane background, message right-edge containment during resize, provider top-to-bottom fill, narrow fallback, and centered nested pet-menu headers.

## Status

DONE
