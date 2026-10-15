---
name: po-wiki
description: Writes or updates a documentation page on the project wiki for a Product Owner - how a feature works, what a delivered change means for users, a process description - from work items, attachments and code. Use when someone asks to "document this", "write a wiki page", "update the documentation after this story", or "describe the process for new colleagues". Needs the ado-lens MCP server.
---

# A wiki page

Write for the people who will read the page (users, testers, new colleagues): plain language, in the language the
wiki is written in (look at its other pages), short sections, examples.

## Steps

1. Facts first:
   - the feature: `ado_search`, then the requirement and its criteria (`ado_work_item`), its specification and
     screenshots (`ado_attachments`), what the code does (`ado_file`),
   - a delivered change: `ado_trace id=<id>` (what was asked, where it is) and `ado_release_notes`.
2. Where it goes: `ado_wiki` shows the page tree; find the section where similar pages live, or an existing page to
   update (read it whole with `detail=full` first).
3. Draft the page:
   - **What it is for** (2–3 sentences),
   - **How it works** — numbered steps, with an example; a Mermaid diagram when there are decisions,
   - **Settings** — where it is switched on or configured,
   - **Good to know** — limits, exceptions, messages the user may see,
   - **History** — the work items behind it (links).
   Say what comes from the code and what from documents when they differ.

## Afterwards

Show the draft and the path. Save only after the user agrees: `ado_wiki_write mode=create` for a new page; for an
existing page `mode=append`, or `mode=replace` with the version from the last full read.
