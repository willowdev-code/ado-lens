---
name: po-refine
description: Prepares backlog refinement for a Product Owner - what is not ready for planning (no acceptance criteria, no estimate, too big, unclear), concrete fixes and questions for the team, splits of large items. Use when someone asks to "prepare refinement", "clean up the backlog", "what is not ready for the sprint", "split this story", or "what should we ask the team". Needs the ado-lens MCP server.
---

# Backlog refinement

The reader is a Product Owner: plain language, in the user's language; ids in brackets.

First load the tools this needs: `ado_tools enable="write;boards;code"` (one call; skip it when they are already there).

## Steps

1. `ado_health` (the team's open backlog) — items without acceptance criteria, description, estimate or parent,
   stale ones, left in finished sprints, look-alike titles.
2. `ado_backlog top=20` — the top of the backlog is what planning takes next; focus there first.
3. For the top items that are not ready, read them (`ado_work_item ids=…`, files with `ado_attachments`) and decide:
   - **missing criteria** → propose 2–5 testable criteria from the description,
   - **too big** (more than ~7 criteria, several user goals, "and" in the title) → propose a split into smaller
     requirements, each with its own goal; `ado_create_tree` can create them later,
   - **unclear** → a precise question for the team or the requester (who decides, what exactly),
   - **duplicates** → which one to keep.

## Answer

1. One sentence: how many of the top items are ready for planning.
2. A table: item | what is missing | proposal (criteria, split, question).
3. Questions for the refinement meeting, grouped by person or topic.

Change nothing on your own. After the user agrees: `ado_update` for criteria, `ado_create_tree` for splits,
`ado_comment` with questions (`@[name]` to notify).
