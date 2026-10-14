# Changelog

## 0.1.0 — unreleased

First skeleton.

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
- Modes `po` / `dev` / `full` choose the tool sets Claude sees; `config --add-toolset`, `--confirm-above`.
- Inherited process names, bugs shown on the requirement backlog, disabled types listed by name.
- The PAT is only ever sent to Azure DevOps hosts. `adolens api <path>` for development.
- Usage log and `adolens stats` (answer sizes in tokens, times, empty answers); `stats --live` follows the calls as
  Claude makes them; `--tokens` on any command and `config --show-tokens true` show the size of each answer.
