---
name: po-explain
description: Explains how a feature works and why it works that way, in business language, from work items, the wiki and the code in Azure DevOps. Use when someone asks "how does X work", "what happens when…", "why does the system do…", "where is this documented", or needs a feature described for users, testers or a new colleague. Needs the ado-lens MCP server.
---

# Explaining a feature

The reader is a Product Owner or a business user: answer in plain language, in the language the user writes in.
Technical names (objects, methods, files) only in brackets, or when asked for "details".

First load the tools this needs: `ado_tools enable="code;wiki"` (one call; skip it when they are already there).

## Steps

1. `ado_search text=<feature in the user's words>` — work items, wiki pages and code at once. Try a second wording
   (a synonym, the English or the local term) when the first finds little.
   A message from the screen ("why do I see …"): `ado_label text=<message as seen>` finds where it comes from.
2. Read the strongest sources, stop when the question is answered:
   - wiki pages: `ado_wiki path=<page>`,
   - work items: `ado_work_item ids=<ids>` — the requirement and its acceptance criteria say what was intended;
     specifications and screenshots attached to them: `ado_attachments`,
   - code: `ado_file path=<file or name>` (X++ objects show their methods; `part=<method>` one method) — the code says
     what really happens.
3. "Why is it like this": `ado_file path=<file> history=true` — who changed it, when and for which work items; read the
   work item behind the change that matters.
4. With xpp-lens available (developer machine), its tools give callers and references; without it, stay with the above.

## Answer

Start with one or two sentences that answer the question. Then, as fits:
- **How it works** — numbered steps "what happens when…", with an example ("an order for 120 pcs → …").
- A small Mermaid diagram when there is a flow with decisions.
- **Why** — the decision and its work item ("added in #4821 to stop orders over the credit limit").
- **Sources** — links to the work items, wiki pages and files used.

Always separate what the **code** does from what the **documentation** says; when they disagree, say so plainly —
that is worth a work item or a wiki fix. Say "not found" rather than guess.

## Afterwards

Offer, do not do unasked:
- a wiki page with the explanation (`ado_wiki_write`, ask where),
- a work item when code and documentation disagree.
