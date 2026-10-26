# Changelog

## 0.9.4 — 2026-10-26

- `ado_trace`: a branch earlier in the environment flow than the one a change was merged into is not reported as
  "NOT merged yet" (a change merged straight into master of a library repository says nothing about its dev branch).
- Before "not yet", one question per source branch: completed PRs from the same source branch into that branch that
  are not linked to the item ("merged by PRs not linked to the item: PR 31002 (2021-09-01, staging <- 243623_…)").

## 0.9.3 — 2026-10-19

- `ado_trace`: "is the change in this branch / deployment" asks the history of the target around the day the change
  was merged (one cheap call) instead of a diff between the commits, which took 20-30 seconds each in a large
  repository; the diff stays as the fallback when the date is unknown.
- Code moves forward through the environments of the map (dev -> staging -> master): a change merged into a later
  one is not looked for in an earlier one.
- "not yet" leaves out a change whose work is already there through another PR from the same source branch (the same
  work merged into each environment by its own PR).

## 0.9.2 — 2026-10-19

Fixes and additions from testing 0.9 on a real project.

- `ado_create`: fields with a default of the process (Value Area, Severity, Triage…) and fields Azure DevOps fills
  itself (Area ID, Iteration ID, State, Work Item Type) are no longer asked for - the rule check of 0.9.0 took the
  system rules of the process for fields to give. A rule "required while State was empty" is checked as "on create"
  instead of ending in a 400 from Azure DevOps.
- `ado_fields type=<type>`: fields required by rules are named as REQUIRED, and fields that are required but filled by
  Azure DevOps say so.
- `ado_trace` much faster on items with many pull requests: the files of a PR come from its last iteration instead of
  a diff of its merge commit, merge commits of linked PRs are not read twice, a completed PR into a branch answers
  "is it there" without asking, and only the newest change of each source -> target pair is checked. Reading the
  changes gets half of the time, so builds and deployments are still read.
- `ado_trace` with no code linked looks for configuration words in every text field of the item and its tasks.
- `ado_sprint`: a "Done" state that the process puts in the Resolved category counts as done.
- `ado_work_item`: names of up to 40 linked releases (was 10); a release deleted by retention or not readable says so.
- Numbered lists keep their numbering (`<ol start="3">`).
- `ado_pr_update`: `update` adds reviewers, `requiredReviewers` (on create and update) makes them required,
  `autocomplete` (merge strategy, delete source branch, text = merge message) and `cancel_autocomplete`.
- A parameter a tool does not have is reported with the tool's parameters instead of being dropped (a conversation
  started before an update still sees the old tool descriptions).

## 0.9.1 — 2026-10-19

- `ado_trace` always answers: on an item with many links (pull requests, commits, builds, releases) the slow parts
  stop after 40 seconds and an INCOMPLETE section says what was not checked, instead of the client giving up with a
  timeout. At most 8 branch and deployment checks run at the same time (more were throttled), and the TFVC branch of
  changesets is asked once per folder, all at once.
- `adolens trace <id> --timing`: how long each part took, to find what is slow on a given project.

## 0.9.0 — 2026-10-18

Improvements from testing on a real project.

- `ado_pr_update action=update`: change the title or description of a pull request and link work items to it after it
  was created (branch policies often require a linked work item), instead of abandoning it and opening a new one.
- People are found by name, e-mail or a display name with a company suffix ("Anna Nowak (Contoso)"); when the identity
  search finds nobody, the people picker of the web is asked too.
- Fields required by rules of an inherited process ("required when moving to Resolved") are checked before writing
  and listed in `ado_fields type=<type>`, instead of a 400 from Azure DevOps.
- `ado_sprint detail=full`: who has what - open items, tasks and hours left per person.
- `ado_file` on a table, table extension, view or map: a skeleton of properties, fields (type, label, mandatory),
  indexes, relations and field groups instead of only the methods.
- `ado_work_item`: links to release stages name the release, the stage, its status and date; commit links give the
  full commit id.
- `ado_trace`: with no code linked, an item that speaks of configuration (ER, parameters, setup…) is reported as a
  probable change in the system's setup that can be checked only there.

## 0.8.2 — 2026-10-18

Fewer false alarms in sprints and checks.

- `ado_sprint`: no "fits, N h spare" when most open tasks have no Remaining Work - it says how many have it instead;
  the attention count says how many are items and how many their tasks.
- "no acceptance criteria" only for types that have the field (a process that keeps criteria in the description is not
  flagged, in `ado_sprint` and `ado_trace` alike); "no estimate" and "no acceptance criteria" only for items not
  started or in the first state of the work, not for items in test or waiting for production.
- `ado_create` / `ado_update`: for a type without a criteria field the criteria are part of the description, so the
  length hint allows twice as many words and no "no acceptance criteria given" note.

## 0.8.1 — 2026-10-18

Fixes from testing on a real project.

- `adolens` works in any new terminal after an install or upgrade with the script (`get.ps1`, `install.ps1`,
  `adolens upgrade --install` on such an installation) too: the program folder is added to the user's PATH, as the
  setup EXE already did; `uninstall.ps1` takes it off.

- Tool arguments in another JSON shape are read instead of failing with a bare "An error occurred invoking":
  `ids=714800` (a number for text), `workItems=[1,2]`, `file=1`, `top="20"`, `draft="true"`. This was behind the failing
  `ado_update` (even with only a comment), `ado_pr_update action=create` with work items and `ado_attachments file=<n>`.
  An argument that still cannot be read says which and why.
- `ado_attachments`: two files with the same name are listed by number to pick one.
- `ado_file history=true`: full commit ids (they work in `ado_changes` and `ado_file version=`), changes that came in
  with a merge are listed too, and the branch read is named.
- `ado_file` without a version names the branch it read (the default one).
- `ado_sprint`: a sprint over the Azure DevOps limit of 20 000 work items says so instead of a raw server error; with
  no current sprint the answer starts with NO CURRENT SPRINT; items in a state this project's process does not know
  (e.g. from another project) are put in done / in progress by the state's usual name.

## 0.8.0 — 2026-10-18

Fewer tokens in every Claude Code session: about 1.6k instead of 5.5–7k.

- Tool groups loaded on demand: a session starts with the core (`ado_status`, `ado_settings`, `ado_upgrade`,
  `ado_work_item`, `ado_query`, `ado_search`, `ado_trace` and the new `ado_tools`), and Claude loads a group with
  `ado_tools enable=write` (or `"write;boards"`) when the conversation needs it; the client is told about the new tools
  (MCP `tools/list_changed`). Groups: `write`, `boards`, `wiki`, `code`, `tests`, `recipes`, `pr`, `pipelines`. The mode
  says which groups may be loaded. Skills and prompts load the groups they use.
- `adolens config --tool-loading auto|demand|all` (or *Tools at start* in the settings menu). `auto`, the default: on
  demand in Claude Code; every group of the mode at start in other clients (Claude Desktop), which may not take new
  tools during a conversation. `demand`: on demand everywhere. `all`: everything at start.
- Tool sets renamed to groups: `work` → `write`, `trace` → `code`, `test` → `tests` (old names in the settings still work).
- Shorter descriptions of `ado_update`, `ado_create` and `ado_pr_update`.
- Pull request tags (labels): `ado_pr_update action=tag tags="R349; -old"`, tags when creating a PR, and tags shown in
  PR lists, details and merged PRs.
- Product Owner mode: no pull request or pipeline tools and no developer prompts (`dev_*`); they stay in the dev and
  full modes.

## 0.7.3 — 2026-10-16

Fixes from a real question: "pull requests merged into staging after release X, with their work items and tags".

- `ado_pr` and `ado_pipeline` (reading: pull request lists and details, merged PRs, builds, deployments, artifacts) are
  in every mode now, the Product Owner one too; creating and completing PRs and running pipelines stay for developers.
- `ado_changes` of a pull request, changeset or commit lists its work items with type, state and tags.
- `ado_trace` checks the release stages in parallel and skips deployments older than the change: it no longer runs
  out of time on items with several pull requests.
- `ado_file path=/ repo=<r> history=true version=<branch>`: the history of a whole repository or folder on a branch,
  with pull requests and work items. An environment name given as the branch ("sbx03") is turned into its branch.
- The environment map reads "UAT (sbx03) = branch staging" instead of "UAT (sbx03) [staging]".
- The server instructions name the tools for "what went into a branch or an environment".

## 0.7.2 — 2026-10-16

- `ado_pr merged=<branch>` (`adolens pr --merged staging --since 7d`): pull requests completed into a branch, newest
  first, each with its work items, their type, state and tags - "what went into UAT this week"; `since` takes a time
  too ("2026-10-15 14:30").
- `ado_pipeline deployments=true` (`adolens pipeline --deployments --definition <release> --stage UAT`): recent release
  deployments with date and time, release and build - the moment to use as `since` ("PRs to staging after deployment
  XYZ").
- Dates given as `since=yyyy-MM-dd` are local days (before, a day earlier in Polish time was shown and used).

## 0.7.1 — 2026-10-15

Fixes from use in a process with many required fields.

- Creating a work item no longer asks for fields Azure DevOps fills itself (Iteration ID, Area ID and other system
  fields some processes mark as required); the message names each missing field with its reference name and allowed
  values.
- `fields` of `ado_create`, `ado_update` and recipe parameters take numbers and booleans (`{"Priority": 2}`), an object
  sent as a JSON string, or `Name=Value; …` - before, such calls failed with no message.
- An unexpected error inside a tool now says what happened instead of "An error occurred invoking …".
- A value that matches several allowed values (e.g. "2" for "2 - High" and "20 - …") names them instead of failing
  with a vague message.

## 0.7.0 — 2026-10-15

Files from Azure DevOps: read what can be read, save the rest.

- `ado_attachments` reads PDF (text page by page, `pages=2-5`; scanned pages shown as images), PowerPoint, saved e-mails
  (.eml) and ZIP files (the list, `part=` one file inside), next to Word, Excel, Outlook e-mails and text.
- The kind of a file comes from its bytes, not its name: binaries (traces, packages) are never sent as text.
- Long documents come as an outline and the start, with their size in tokens; `detail=full` for all.
- `save=true` streams a file to disk at any size; only its path, size and SHA-256 go to Claude. Files over 20 MB or not
  readable get a link and the hint to save them, instead of an error.
- Build artifacts: `ado_pipeline build=<id> artifacts=true` lists them, `artifact=<name>` shows the files in one
  (`part=` reads a file), `save=true` saves it.
- Download folder: `adolens config --download-dir <folder>` or the settings menu (default Downloads\ADO Lens).
- A required label language with no label file in the model is reported by `ado_review` and `ado_label`.
- THIRD-PARTY-NOTICES.md lists the bundled libraries and their licenses (PdfPig: Apache 2.0); shipped in every package.

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
