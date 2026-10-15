---
name: dev-build
description: Diagnoses a failed build or release for a developer - the failing step, the real error lines, the code behind them, and a proposed fix. Use when someone says "the build is red", "why did build 900 fail", "the pipeline failed", or "what broke the build". Needs the ado-lens MCP server.
---

# A red build

## Steps

1. `ado_pipeline` (recent runs; `definition` / `branch` to narrow) → the failed run; `ado_pipeline build=<id>` for the
   failed stage, job and steps with the error lines of the log (`detail=full` for more).
2. Separate the cause from the noise: the first real error, not the follow-up failures.
3. Find the code: the object or file named in the error (`ado_file`, X++: `part=<method>`), and what changed since the
   last green build of the same pipeline (`ado_pipeline` lists them; `ado_changes` of the changes between).
4. Who and what: `ado_file history=true` for the last change of that object and its work item.
5. Logs, packages and traces kept as build artifacts: `ado_pipeline build=<id> artifacts=true`, then `artifact=<name>
   part=<file>` to read one file, or `save=true` to put it on disk for another tool (a trace parser, a diff tool) -
   only the path comes back, not the content.

## Answer

1. One sentence: what broke and why.
2. The error lines (short), the place in the code, the change that caused it (id, author, work item).
3. The fix - concrete code or setting; for data or environment problems, who has to act.
4. Offer to re-run after the fix: `ado_pipeline_run definition=… branch=…` - only when asked.
