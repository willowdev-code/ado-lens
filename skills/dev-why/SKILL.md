---
name: dev-why
description: Explains why a piece of code is the way it is - the history of a file or X++ object, the changes that shaped it, the work items and decisions behind them. Use when a developer asks "why is this like this", "who changed this and why", "what was this for", or "can I remove this". Needs the ado-lens MCP server.
---

# Why the code is like this

First load the tools this needs: `ado_tools enable="code"` (one call; skip it when they are already there).

## Steps

1. `ado_file path=<file or object name> history=true` - changes newest first, with pull requests and work items.
   With xpp-lens on the machine, use it for callers and references of the method in question.
2. Pick the changes that touched the part asked about: `ado_changes change=<id> file=<object>` shows the method-level
   diff of each.
3. Read the work items behind those changes (`ado_work_item`, comments for decisions; `ado_attachments` for
   specifications) - that is the "why".

## Answer

1. One or two sentences: why it is like this, with the work item ("added in #4821 to block orders over the credit
   limit").
2. A short timeline of the changes that matter: date, author, change, work item, what it did.
3. For "can I remove it": what still depends on it (callers, settings, data) and who to ask.
4. "Simple version" on request: two sentences for a PO, without code.
