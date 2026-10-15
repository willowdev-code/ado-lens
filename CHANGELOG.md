# Changelog

## 0.6.0 — 2026-10-15

Screenshots and documents for Claude, D365 labels, checks before testing, ready commands, a settings menu.

- `ado_attachments`: the files of a work item and the screenshots pasted into its text or comments; images are shown
  to Claude, Word, Excel and text files are read as text; `add` attaches a local file.
- `ado_label`: a D365 message pasted as seen (values in place of any %1 %2 …) or a label id → the label, its text in
  every language (missing ones flagged) and where it is used (object and X++ method, field or control).
- `ado_review`: checks of a change or of every change of a work item before testing - work item linked, labels in
  every language, X++ messages from labels, no TODO left.
- `ado_create_tree`: a tree of work items from an indented list, previewed, one undo for all of it.
- `ado_metrics`: velocity per sprint, cycle and lead time, what is in progress longest.
- `ado_changes file=a;b;c`: several diffs in one call.
- Outlook e-mails (.msg) attached to work items are read: subject, sender, recipients, date, the files attached to the
  e-mail and its text (plain, or HTML turned into text).
- Required label languages per connection (`adolens config --connection <c> --label-languages "en-US, pl"`, or the
  settings menu): `ado_review` and `ado_label` report missing texts only in those languages.
- Prompts (ready commands in Claude Desktop "+" and Claude Code "/"): verify, explain, story, sprint, refine, test, wiki,
  label, dev_start, dev_pr, dev_build, dev_why - each runs its skill, no skill upload needed.
- Skills `po-refine`, `po-test`, `po-wiki`, `dev-start`, `dev-pr`, `dev-build`, `dev-why`; `po-verify`, `po-explain`
  and `po-sprint` use the new tools. Claude Code gets only the skills of the mode (`adolens skills` after a change).
- Settings menu (`adolens setup`, Start menu "ADO Lens - settings"): mode, environments (change, add, remove, for you
  or the team), add or remove a connection, status, language.
- Installer: an environments page (stages and branches found, names suggested, saved for you or the team) and a
  language choice (Polish or English) kept for the settings.
- `ado_trace` leaves out release stages outside the environment map that have not deployed for half a year.
- The settings file keeps "UAT + SCM" and Polish names readable.
- `adolens logout --connection <name> --remove` removes the whole connection: the PAT, the settings entry and its
  metadata cache (a new default is picked when one connection is left).
- `adolens update` without anything to change says it changes work items and points to `adolens upgrade --install`
  for updating ADO Lens itself.

## 0.5.1 — 2026-10-15

- An icon: the program, the installer, Settings → Apps, the Start menu and the Claude Desktop extension.
- Start menu folder "ADO Lens": settings (`adolens setup`), status, skills for Claude Desktop, uninstall.
- The installer puts the program folder on the user's PATH: `adolens` works in any new terminal.
- Uninstall asks whether to remove the stored PATs and the settings and cache too (default: keep them); it also takes
  the program off the PATH. `adolens logout --all` removes every stored PAT.

## 0.5.0 — 2026-10-15

Tests, templates, and two more ways to install.

- `ado_tests` (tool set `test`): the Test Cases of work items or of a sprint's requirements and bugs, with the latest
  outcome per plan, suite and configuration; items without tests; steps with `detail=full`.
- `ado_test_create`: Test Cases from markdown (`## title`, `1. action => expected`), linked as tested-by, in the item's
  area and iteration, optionally added to a suite; recorded for `ado_undo`.
- `ado_create template=<name>`: a team's work item template fills the values not given; `ado_fields type=…` lists the
  team's templates and the custom rules of the type in an inherited process.
- Installer with a wizard (`ado-lens-setup-X.Y.Z.exe`, Inno Setup, per user, no administrator rights): Azure DevOps
  link and PAT checked before installing (the PAT goes to Credential Manager over an environment variable, never on a
  command line), mode, skills for Claude Code and ZIPs for Claude Desktop, registration (asks to quit Claude Desktop
  when needed); updates skip the questions; uninstall from Settings → Apps.
- Claude Desktop extension (`ado-lens-X.Y.Z.mcpb`): project link, PAT (kept by Claude Desktop in secure storage) and
  mode in the extension settings.
- `adolens login --pat-env <variable>` for unattended setups; without a project and with several in the organization it
  lists them and stops (exit code 4) instead of asking.
- Releases publish the installer and the extension next to the ZIP, each with its SHA-256; CI builds all three.
- `ado_upgrade` of an installation made with the installer EXE updates with the new installer EXE (SHA-256 checked,
  no questions), so the entry in Settings → Apps shows the new version.

## 0.4.0 — 2026-10-15

How it works and why, and repeatable changes.

- `ado_search`: one search over work items (full text, with the field that matched), wiki pages and code (Git and
  TFVC), grouped; without the search service work items fall back to titles, a missing part does not stop the rest.
- `ado_file`: a file by TFVC path, Git path or only its name; X++ objects as their methods, `part` shows one method;
  `history=true`: who changed it, when, through which pull request (also for squash and rebase flows) and for which work
  items.
- `ado_undo` and a local log of every change ADO Lens makes (`%LOCALAPPDATA%\ado-lens\audit`): field values back,
  fields changed by someone else since are kept and named, comments deleted, links removed or added back, created items
  set to their removed state; always previewed first.
- `ado_health`: backlog hygiene of a team (no criteria, description, estimate or parent; stale; left in a finished
  sprint; look-alike titles; done without code).
- `ado_release_notes`: requirements and bugs finished in a sprint or between dates as markdown, grouped by feature.
- Recipes (tool set `recipes`, `ado_recipe`, `adolens recipe`): saved steps with a WIQL query and changes, comments or
  child items from a template, with parameters; every run previewed; `--yes` for Windows Task Scheduler.
- `ado_update` and recipes take iteration `next`.
- Skill `po-explain`: "how does X work" in business language with sources, code told apart from documentation.
- Wiki search shows page paths with real hyphens.

## 0.3.0 — 2026-10-15

Easy to install and to keep current, for people who are not developers.

- One-line install: `irm …/get.ps1 | iex` downloads the latest release, checks its SHA-256 and runs the installer
  (no browser download, so no SmartScreen stop for the not yet signed program).
- Installer in six steps with defaults: connection, then who uses it (mode) and where code goes (environments found
  from release stages and branches, names suggested, saved for the user or the team), skills, Claude registration.
  Questions in Polish on a Polish Windows. `adolens setup` asks the same later.
- Team settings on the project wiki: a page "ADO Lens" with the environment table, read by everyone on the project;
  `ado_settings scope=team` writes it with the page version. A local map overrides it.
- `ado_settings` without arguments shows the flow in use or the one detected, so Claude can ask once.
- Updates: the server checks GitHub once a day in the background and mentions a newer version once per session;
  `ado_upgrade` / `adolens upgrade --install` downloads it, checks SHA-256 and runs its installer; `*.old` leftovers of
  updates are removed at start.
- Skills `po-story` (writing requirements, bugs and tasks) and `po-sprint` (sprint status, review, release notes) next
  to `po-verify`; installed into Claude Code and packed as ZIPs for Claude Desktop; removed on uninstall.

## 0.2.0 — 2026-10-14

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
- Pull requests (tool set `pr`): `ado_pr` (waiting for my review, mine, all active; one PR with votes, checks, work
  items and the open threads people wrote) and `ado_pr_update` (create, comment on a file and line, reply, resolve,
  vote, publish, complete, abandon; `@[name]` mentions).
- Pipelines (tool set `pipelines`): `ado_pipeline` (recent builds; a failed build with its failed steps, errors and the
  error lines of the log without MSBuild noise) and `ado_pipeline_run`.
- `ado_settings`: shows the connection and saves the environment map; when the map is missing, `ado_trace` ends with a
  SETUP note (the flow it found and a first guess) so Claude asks the user once and saves the answer.
- CLI: `trace`, `changes`, `pr`, `pipeline`.

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
