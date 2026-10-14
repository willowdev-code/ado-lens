---
name: po-sprint
description: Summarizes a sprint or a period for a Product Owner and stakeholders - progress, what is at risk, what was delivered and where it is deployed - and writes release notes. Use when someone asks "how is the sprint going", "prepare the sprint review", "what did we deliver", "release notes", "status for stakeholders", or "what is new today". Needs the ado-lens MCP server.
---

# Sprint summary and release notes

The reader is a Product Owner or a stakeholder: plain business language, in the user's language; ids and technical
names only in brackets.

## How is the sprint going

1. `ado_sprint` (the team's current sprint; `sprint=next|previous|<name>` when asked). If the sprint has ended, say so
   plainly and ask whether they mean the next one or a date range.
2. Answer in this order:
   - one sentence: on track or not, and why ("8 of 12 done, 2 days left, remaining work fits the capacity"),
   - **needs attention** — the items from the attention list, each with what to do about it (who to ask, what to
     decide),
   - **in progress / not started** only when asked for details.
3. `ado_board summary` when they ask about the board or WIP limits; `ado_my_day` for "what is new for me".

## Sprint review, delivered work, release notes

1. Find the items: the sprint's done items (`ado_sprint detail=full`), or `ado_query` with a WIQL for a date range
   (state category Completed, changed in the range), using field names from `ado_fields`.
2. For the most important items, `ado_trace id=<id> detail=summary` tells where each one is (DEV / UAT / PROD).
3. Write the notes grouped by what users notice (new, changed, fixed), one line each, no internal jargon:
   "Orders above the credit limit are now blocked at confirmation (#4821)". Add where it is available.
4. Offer to save them as a wiki page (`ado_wiki_write`, mode=create or append) — only after the user agrees.
