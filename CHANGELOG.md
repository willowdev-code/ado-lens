# Changelog

## 0.2.0 — unreleased

Checking work against what was asked, and where it is.

- `ado_trace` (tool set `trace`): the acceptance criteria (from the parent for a task) and whether they changed after
  coding started; changesets, commits and pull requests linked to the item and its tasks, with the X++ objects they
  touch; builds, also those deleted by retention; every release stage of the code's branches with YES / NOT YET, in all
  projects the code lives in; for Git, whether the change is merged into each mapped branch; tests, tasks, signals.
- `ado_changes`: files of a changeset, pull request or commit with added / changed / removed X++ methods per object;
  the diff of one file — AOT XML per method plus the metadata without raw XML noise; other files as unified diffs.
- Environments per connection: `adolens config --connection <c> --env staging=UAT` maps branches, release definitions or
  single release stages (`"Definition/Stage=UAT + SCM"`) to environment names; any number of steps, listed in that order.
- Git flows with a PR per step (feature -> dev, -> staging, -> release branch -> master): a stage has the work when any
  of its PRs reached it, later changes not there yet are named; a PR and its merge commit are one change; release
  merges are one line.
- Skill `po-verify` (`skills/po-verify`): guides Claude through checking a work item for a Product Owner.
- Server instructions keep only advice the tool descriptions do not give (fewer tokens per session); the tool size
  check measures the JSON Claude really receives and only guards against accidental growth.
- CLI: `trace`, `changes`.

## 0.1.0 — 2026-10-14

First release: work items, sprints, boards, backlogs and the wiki for Claude Desktop and Claude Code.

- `adolens.exe`: MCP server over stdio and CLI with the same answers.
- Connections (several organizations): `login`, `logout`, `connections`, `config`. PATs in Windows Credential Manager
  (`ado-lens:<connection>`); without one, `ADOLENS_PAT_<CONNECTION>` and then `AZURE_DEVOPS_PAT`.
- Registration in Claude Desktop and Claude Code (`register` / `unregister`), never with a PAT in the Claude configuration;
  `status` warns about other entries that keep a PAT in plain text.
- Local metadata (SQLite) per connection: work item types by backlog level, states, transitions, fields, required flags,
  defaults and allowed values; history of structure changes between refreshes.
- Tools: `ado_status`, `ado_fields`.
- Work items (tool set `work`): `ado_work_item` (compact item with markdown text, links, code links; comments and
  history on request), `ado_query` (WIQL or saved query), `ado_create` and `ado_update` (fields by name, allowed values,
  people by name, state only along the transitions, markdown → HTML, soft length notes; updates of more than
  `confirmBulkAbove` items are previewed and written with the preview's token), `ado_comment` (`@[name]` mentions),
  `ado_link`. Same commands in the CLI (`item`, `query`, `create`, `update`, `comment`, `link`).
- Boards (tool set `boards`): `ado_sprint` (dates and working day, progress by state, estimates, remaining work vs
  capacity left with days off, attention list, task progress; ended sprints said plainly), `ado_board` (columns, WIP
  limits, cards; moving a card sets the column and the state mapped for its type), `ado_backlog` (priority order,
  reordering), `ado_my_day` (mentions, own and followed items changed by others, PRs waiting for review). CLI: `sprint`,
  `board`, `backlog`, `myday`.
- Wiki (tool set `wiki`): `ado_wiki` (page tree, a page as markdown with its version and subpages, search with
  snippets) and `ado_wiki_write` (create, append, prepend, or replace with the version from the last read — a page
  changed meanwhile is never overwritten). CLI: `wiki`, `wiki-write`.
- Modes `po` / `dev` / `full` choose the tool sets Claude sees; `config --add-toolset`, `--confirm-above`.
- Inherited process names, bugs shown on the requirement backlog, disabled types listed by name.
- The PAT is only ever sent to Azure DevOps hosts. `adolens api <path>` for development.
- Releases: `release.ps1` (test or regular release from `dev`, private names and broken characters scan, CI, merge
  to `main`, tag, published package check), Release workflow with the package's SHA-256.
- Usage log and `adolens stats` (answer sizes in tokens, times, empty answers); `stats --live` follows the calls as
  Claude makes them; `--tokens` on any command and `config --show-tokens true` show the size of each answer.
