---
name: dev-start
description: Starts a developer's work on a work item - what is asked (criteria from the item and its parent, attachments), where in the code it lands, a plan per criterion, open questions. Use when a developer says "start #123", "what do I have to do in this task", "plan this story", or "where do I change this". Needs the ado-lens MCP server.
---

# Starting work on a work item

The reader is a developer: technical and short - objects, methods, paths, ids.

## Steps

1. `ado_work_item ids=<id> include=comments` and its parent (for a task the criteria are there); specifications,
   e-mails and screenshots: `ado_attachments id=<id>` (and on the parent).
2. Where it lands: `ado_search scope=code` with the domain words, `ado_file` for the objects found (X++: methods;
   `part=<method>` to read one), `ado_file history=true` for who touched them last and why. With xpp-lens on the
   machine, prefer it for callers, references and extensions.
3. Earlier work on the same feature: `ado_search scope=work` and its `ado_trace` - reuse what exists.

## Answer

1. **Goal** in one sentence.
2. **Plan** per acceptance criterion: what to change, where (object.method), new objects, labels in every language.
3. **Open questions** - anything the criteria leave undecided, as precise questions to the PO (offer `ado_comment`).
4. **Risks** - shared code touched, data migration, security, performance - only if any.

Change the state or the assignment only when asked (`ado_update state=… assignedTo=…`).
