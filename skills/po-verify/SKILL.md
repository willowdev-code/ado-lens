---
name: po-verify
description: Checks whether a work item was done as asked and where the change is (DEV / UAT / PROD), for a Product Owner. Use when someone asks "did they do what I asked", "is #123 done right", "is it on UAT yet", "can I close this", or wants an acceptance check of a story, bug or task in Azure DevOps. Needs the ado-lens MCP server.
---

# Checking work for a Product Owner

The reader is a Product Owner, not a developer: answer in plain business language, in the language the user writes in.
Technical names (objects, methods, files, ids of commits) only in brackets or when asked for "details".

First load the tools this needs: `ado_tools enable="code;write"` (one call; skip it when they are already there).

## Steps

1. `ado_trace id=<id>` — what was asked (acceptance criteria), what code is linked, builds, where it is deployed, signals.
   For a task, the criteria come from its parent; say so. No criteria in the item: look for the specification in
   its files (`ado_attachments id=<id>`, also on the parent) - a Word document or an e-mail - and screenshots.
2. For every criterion, look for evidence in the code — never assume:
   - `ado_changes change=<changeset | PR n | commit + repo>` lists the changed objects with their methods,
   - `ado_changes … file=<object>` shows what exactly changed (X++ per method, metadata such as fields and controls).
   Read only the files that can prove or disprove a criterion; stop when each one has an answer.
   `ado_review id=<id>` checks every change at once: work item linked, labels in every language, messages from
   labels, TODO left - mention what it finds under "Watch out".
3. Judge each criterion:
   - ✅ **done** — the code clearly does it (name the place in brackets),
   - ⚠️ **to check by hand** — partly there, or only testable in the system (data, configuration, UI behaviour),
   - ❌ **missing** — nothing in the changes does it.

## Answer

Start with one sentence: is it done, and where is it ("Done as asked, on UAT since Tuesday, not yet on production").
Then:

| Criterion | Status | Why |
|---|---|---|
| short criterion text | ✅ / ⚠️ / ❌ | one plain sentence (technical place in brackets) |

Then, only when they apply:
- **Where it is** — each environment: on it since … / not yet.
- **Watch out** — the signals from `ado_trace` in plain words (criteria changed after coding started, closed with open
  tasks, no criteria at all, build failed, PR not merged).
- **How to try it** — 2–4 steps the PO can do on UAT to see it working, with example data.

Keep it short: no file lists, no code unless asked ("show details" → add the technical part).

## Afterwards

Offer, do not do unasked:
- a comment on the work item with the result (`ado_comment`, ≤ 60 words, decision first; `@[name]` the developer when
  something is missing),
- closing the item when everything is ✅ and it is where the PO tests (`ado_update state=…`).
