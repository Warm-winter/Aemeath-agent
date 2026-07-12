# Chat Scroll, Tool Streaming, and Transient Failure Follow-up

Date: 2026-07-12

## Root causes

- The chat window stopped its fixed 12-frame bottom-scroll stabilizer before sidebar, scrollbar, and wrapped message measurements finished.
- Message rows bound their width to `MessagesPanel.Bounds.Width`, creating a first-layout feedback loop.
- SSE normalization treated partial tool-call frames as complete objects, replacing empty argument fragments, inventing IDs, and removing later fragments without a function name.
- An empty assistant reply only changed a pending `TextBlock`, so the failure had no actions and disappeared after reopening.

## Fixes

- Chat content now stretches horizontally and row limits are refreshed from viewport layout events without a live width binding.
- Extent, viewport, and content-size changes keep following the latest message only while the user remains within the 96 px threshold; a manual offset change cancels a queued pin immediately.
- Chat scrollbars retain a 12 px hit target with a 3 px visual track and 5 px thumb.
- Streaming normalization changes only blank `finish_reason` to JSON null and otherwise preserves every delta and tool-call fragment.
- Empty visible replies use a non-persistent failure bubble with copy, retry, and delete actions; retry reuses the original attachments.
- Diagnostics record counts and lengths instead of user text, history content, or prompts.
- Thumbnail source streams allow delete sharing, preventing background preview decoding from locking attachment files during window shutdown.

## Verification

Regression coverage includes delayed post-layout growth, manual scroll detachment, narrow chat scrollbar styling, complete rename labels, multipart tool calls, reasoning-only chunks, and transient failure actions/persistence.
