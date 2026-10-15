---
name: dev-pr
description: Writes a pull request description from its work item, or reviews a pull request against the work item's acceptance criteria and the project's checks. Use when a developer asks to "write the PR description", "open a PR for #123", "review PR 482", or "is this PR ready". Needs the ado-lens MCP server.
---

# Pull requests

## Writing a description (work item given)

1. `ado_work_item ids=<id>` for the goal and criteria; `ado_changes` of the branch's commits or the PR for what
   changed.
2. Description, ≤ ~150 words:
   - **What and why** - 1–2 sentences with `#<id>`,
   - **Changes** - bullets by area (not by file),
   - **How to check** - numbered test steps with data,
   - **Risks** - only if any.
3. Create it only after the user agrees: `ado_pr_update action=create repo=… source=… target=… title=… text=…
   workItems=<id>`.

## Reviewing (PR given)

1. `ado_pr id=<n>` - status, reviewers, checks, open threads; `ado_changes change="PR <n>"` - files and X++ methods.
2. `ado_review change="PR <n>"` - work item linked, labels in every language, messages from labels, TODO.
3. Read the diffs that matter (`ado_changes … file=a;b;c`), compare with each acceptance criterion of the work item.
4. Findings, most important first: **blocking** (a criterion not met, a bug), **should fix**, **nit**. Each with
   file and method and a one-line reason.

Comment or vote only when asked: `ado_pr_update action=comment file=… line=…` / `action=vote vote=…`.
