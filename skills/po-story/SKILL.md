---
name: po-story
description: Writes a work item for a Product Owner - a requirement (user story, product backlog item, requirement), a bug or a task - short and complete, with testable acceptance criteria, using the project's real types and fields. Use when someone asks to "create a story", "write a requirement", "report a bug", "split this into tasks", or describes a need that should become a work item in Azure DevOps. Needs the ado-lens MCP server.
---

# Writing a work item

The reader of the item is a developer and a tester; the writer is a Product Owner. Goal: after reading it, everyone
knows what to do and how to check it — nothing more. Write in the language the user writes in.

## Steps

1. Find out what is needed — ask only what is missing, at most 3 short questions at once:
   - who needs it and why (the value), what exactly should happen, what is out of scope,
   - for a bug: what happens, what should happen, steps, where (environment, company, sample data).
2. `ado_fields` (no type) — which type is the *requirement* level of this process (User Story, Product Backlog Item,
   Requirement, or a company type) and which is the bug level. Then `ado_fields type=<type>` — required fields and
   their allowed values. Never guess field names or values.
3. Draft it and show the draft to the user before creating anything:
   - **Requirement** — description: goal in 1–2 sentences, scope as bullets (and "out of scope" when it matters);
     acceptance criteria in their own field: each one testable, Given/When/Then or one sentence; at most 7 — more means
     the item should be split. About 120 words for the description.
   - **Bug** — what happens / what should happen / numbered steps / where; about 100 words plus the steps.
   - **Task** — one sentence: what to change (dev) or what and how to check (test).
   - No filler, no repeating the title, concrete words ("credit limit check when the order is confirmed", not
     "validation fixes"). Technical details belong in tasks, not in the requirement.
4. After the user agrees: `ado_create` (with `parent` for a task or a story under a feature). Fill required fields the
   process asks for; if one is unknown, ask instead of inventing.
5. Optional, when asked: split into tasks for development and testing (`ado_create` type=Task, parent=<id>), or link
   related items (`ado_link`).

## Answer

One line with the created item and its link, then any notes ADO Lens returned (e.g. a description that is too long).
