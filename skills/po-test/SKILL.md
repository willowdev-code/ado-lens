---
name: po-test
description: Writes acceptance test cases from a work item's acceptance criteria and shows how its tests stand, for a Product Owner or tester. Use when someone asks to "write test cases", "prepare UAT scenarios", "how do I test this", "is this tested", or "what failed in testing". Needs the ado-lens MCP server.
---

# Acceptance tests

Plain language, in the user's language; steps a business tester can follow on UAT.

## Steps

1. `ado_work_item ids=<id>` — the acceptance criteria (from the parent for a task). No criteria: look at the
   specification in `ado_attachments`, else ask the user what has to work.
2. `ado_tests ids=<id>` — test cases that already exist and their last results; do not duplicate them.
3. One test case per criterion (more only for clearly different situations, e.g. limit exceeded / not exceeded):
   - title: what is checked, as a short sentence,
   - 3–8 steps, each `action => expected result`, with concrete example data (an item, a store, a quantity),
   - where to run it (environment, company) when it matters.
4. Show the cases as markdown in exactly the form `ado_test_create` takes:
   ```
   ## Order above the credit limit is blocked
   1. Open a sales order for customer US-001 => the order opens
   2. Add a line of 120 pcs => the total is above the limit
   3. Confirm the order => a message about the credit limit, the order stays unconfirmed
   ```

## Afterwards

Create them only after the user agrees: `ado_test_create id=<id> cases=<markdown>`, with `plan` and `suite` when the
user names the test plan. For "is it tested", answer from `ado_tests` (sprint: without ids): passed / failed / not
run per item, failures first, with who ran them and when.
