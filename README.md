# ADO Lens for Azure DevOps — an MCP server for Claude

[![CI](https://github.com/willowdev-code/ado-lens/actions/workflows/ci.yml/badge.svg)](https://github.com/willowdev-code/ado-lens/actions/workflows/ci.yml)

**English** | [Polski](README.pl.md)

Gives Claude (Desktop and Code) compact, process-aware access to Azure DevOps: work items, boards, wiki, Git and TFVC,
pipelines. Built for developers and for Product Owners: the **PO mode** helps write and check work, the **dev mode** adds
code, pull requests and pipelines. Answers are short text instead of raw JSON, so a work item costs a few hundred tokens,
not thousands. Changes between versions: [CHANGELOG.md](CHANGELOG.md). The road map: [docs/PLAN.md](docs/PLAN.md).

© 2026 WillowDev. Released under the [MIT License](LICENSE). Not affiliated with or endorsed by Microsoft;
Azure DevOps is a trademark of Microsoft.

> Status: **0.x — in development.** Connections, Claude registration, the local work item structure, work items,
> sprints, boards, backlogs and the wiki work; tracing work to code, pull requests and pipelines follow the plan.

## What it does differently

- **Knows the process of each project.** Types by backlog level (portfolio / requirement / task / bug), the real states
  and transitions, required fields, allowed values and custom fields are read from Azure DevOps into a local copy and
  checked *before* anything is written — whatever the process calls its types (User Story, Product Backlog Item,
  Requirement, or a company's own).
- **Small answers.** Empty fields left out, people as names, rich text as markdown, links summarized
  (`code (2): changeset 523 "Fix …"; build 213864`), board bookkeeping hidden.
- **Safe for many changes at once.** Updating more than a few items returns a preview first; the change is written only
  when the call is repeated with the preview's token.
- **Git and TFVC** in the same project, several organizations side by side.
- **Never limits you:** what ADO Lens can do is what your PAT and account allow.

## Installing

The install package is not part of the repository (`dist\` is excluded by `.gitignore`). There are two ways:

**A. From a release package** — nothing needs to be installed on the target machine, not even .NET.

1. Download `ado-lens-X.Y.Z.zip` from the **Releases** tab of this repository.
2. Unzip it and run in a regular PowerShell window (not as administrator):

```powershell
powershell -ExecutionPolicy Bypass -File .\ado-lens\install.ps1
```

**B. From source** — requires the .NET 9 SDK.

```powershell
git clone https://github.com/willowdev-code/ado-lens.git C:\Dev\ado-lens
cd C:\Dev\ado-lens
.\pack.ps1                     # creates dist\ado-lens\ and dist\ado-lens-X.Y.Z.zip (+ .sha256)
powershell -ExecutionPolicy Bypass -File .\dist\ado-lens\install.ps1
```

The installer copies the program to `%LOCALAPPDATA%\Programs\ADO Lens` (no administrator rights), asks for the Azure
DevOps address (paste any link from the browser — the organization and project are taken from it), the PAT (hidden
input) and the default project (picked from a list), and registers the `ado-lens` server in Claude Desktop and Claude
Code. When the registration has to change while Claude Desktop runs, it asks you to quit Claude, waits and starts it
again. Running `install.ps1` from a newer package updates the program and keeps settings, PATs and metadata; an
`adolens.exe` used by an open session is renamed to `*.old`, so that session keeps working.

Useful parameters:

```powershell
.\install.ps1 -Organization https://dev.azure.com/contoso/Demo   # skip the address question
.\install.ps1 -Mode dev                                           # po (default) | dev | full
.\install.ps1 -DesktopOnly | -CodeOnly | -NoRegister | -NoRestart
```

Uninstall: `uninstall.ps1` in the installation folder (removes the Claude registrations of this installation, asks
about the stored PATs and the local data).

### The PAT

Create it at `https://dev.azure.com/<organization>/_usersSettings/tokens` for the one organization, with the scopes you
want Claude to use and a short expiry. Suggested for a PO: Work Items (Read & write), Code (Read), Wiki (Read & write),
Build (Read), Release (Read), Test Management (Read & write), Project and Team (Read), Identity (Read). A read-only PAT
makes the whole server read-only.

The PAT goes to **Windows Credential Manager** (`ado-lens:<connection>`), never to a file or the Claude configuration.
Without a stored PAT the variables `ADOLENS_PAT_<CONNECTION>` and then `AZURE_DEVOPS_PAT` are used. Details:
[SECURITY.md](SECURITY.md).

## Configuration after installing

```powershell
adolens login --org https://dev.azure.com/fabrikam/Shop --connection fabrikam   # another organization
adolens connections                          # list (* = default)
adolens config                               # show settings
adolens config --connection contoso --project Demo --team "Demo Team"
adolens config --default-connection fabrikam
adolens config --mode dev                    # po | dev | full
adolens config --add-toolset pipelines       # one more tool set on top of the mode
adolens config --confirm-above 3             # updates of more items are previewed first (0: always)
adolens config --usage-log false
adolens config --show-tokens true            # end every answer with [~N tokens]
```

Settings live in `%LOCALAPPDATA%\ado-lens\adolens.json` (no secrets) — they can be edited by hand too.

### Modes and tool sets

A mode only chooses which tools Claude sees: fewer tools mean better choices and fewer tokens in every session. It is
never a permission.

| Tool set | Tools | po | dev | full |
|---|---|:-:|:-:|:-:|
| `core` | `ado_status`, `ado_fields` | ✓ | ✓ | ✓ |
| `work` | `ado_work_item`, `ado_query`, `ado_create`, `ado_update`, `ado_comment`, `ado_link` | ✓ | ✓ | ✓ |
| `boards` | `ado_sprint`, `ado_board`, `ado_backlog`, `ado_my_day` | ✓ | – | ✓ |
| `wiki` | `ado_wiki`, `ado_wiki_write` | ✓ | ✓ | ✓ |
| `trace`, `test`, `recipes` | planned | ✓ | (trace, recipes) | ✓ |
| `code`, `pr`, `pipelines` | planned | – | ✓ | ✓ |

## Tool map

What each MCP tool answers, when Claude should reach for it, and the CLI command to try the same yourself.

| Tool | Answers | When | CLI |
|---|---|---|---|
| `ado_status` | version, connection, signed-in user, PAT source, metadata age | checking the setup | `adolens status` |
| `ado_fields` | types by backlog level; for a type: states, transitions, fields, required flags, allowed values; for a field: where it is used and its values; structure changes over time | before writing fields or WIQL; "which fields does a Bug have", "what changed in our process this month" | `adolens fields --type Bug`, `adolens fields --field Severity`, `adolens fields --changes 30d` |
| `ado_work_item` | one or more items: state, people, paths, filled fields, description and acceptance criteria as markdown, parent / children / related, linked changesets, commits, PRs and builds; with `include`: comments, history | "what is #123 about", "who changed the criteria and when" | `adolens item 123 456 --include comments,history --detail summary` |
| `ado_query` | items found by WIQL (with `@Me`, `@CurrentIteration`…) or a saved query, one line each | lists, filters, "open bugs of the sprint" | `adolens query --wiql "SELECT …"`, `adolens query --query "Shared Queries/Open bugs"` |
| `ado_create` | creates an item from markdown; required fields, allowed values and people are checked first; soft length notes | writing stories, bugs, tasks | `adolens create --type Bug --title "…" --description "…" --field "Severity=2 - High"` |
| `ado_update` | changes one or many items (ids or WIQL): fields, state along the transitions, people, tags `+a; -b`, iteration `current`, a comment; many items → preview + `confirm` token | closing, moving to a sprint, mass changes | `adolens update 123 --state Done --comment "…"`, `adolens update --wiql "…" --tags "+reviewed"` |
| `ado_comment` | markdown comment; `@[name]` mentions notify the person | asking, deciding, reporting | `adolens comment 123 "@[Anna] please confirm"` |
| `ado_link` | parent, child, related, predecessor, successor, duplicate, duplicate-of, tested-by, tests; `remove` unlinks | structuring the backlog | `adolens link 123 456 --type child` |
| `ado_sprint` | a team's sprint (`current`, `next`, `previous` or by name): dates and working day, items by state, estimates, remaining work against the capacity left, items needing attention (no change for days, blocked, no estimate, no criteria, unassigned), task progress per item; says plainly when the sprint has ended | stand-up, sprint review, "will we make it" | `adolens sprint`, `adolens sprint next --team "Demo Team" --detail summary` |
| `ado_board` | a team's board: columns with WIP limits (over-limit flagged) and their cards; `move` + `column` moves a card — the state follows the column's mapping for the item's type | "what is in progress", moving cards | `adolens board`, `adolens board --move 123 --column "In Progress"` |
| `ado_backlog` | a team's backlog in priority order (requirement, portfolio, task, or by name); `move` + `above`/`below` reorders | refinement, priorities | `adolens backlog --top 20`, `adolens backlog --move 123 --above 456` |
| `ado_wiki` | the project wiki: page tree (`depth`), one page as markdown with its version and subpages, or search across the project's wikis with snippets; long pages are cut unless `detail=full` | "how does X work", finding documentation | `adolens wiki`, `adolens wiki "Team/Setup"`, `adolens wiki --search "dual write"` |
| `ado_wiki_write` | `create` a page, `append` / `prepend` to it, or `replace` it with the version from the last full read — a page changed by someone else in the meantime is never overwritten; optional change comment | release notes, keeping docs current | `adolens wiki-write "Team/Release notes" --file notes.md --mode append` |
| `ado_my_day` | since the previous working day (or `since`): where you were mentioned (all projects), your items and followed items changed by others, pull requests waiting for your review | the start of the day | `adolens myday --since 3d` |

### Writing rules built in

Created content is checked against soft limits (informative, never blocking): a requirement's description up to about
120 words with testable acceptance criteria in their own field (more than 7 criteria suggests splitting), a bug up to
about 100 words plus numbered steps, a task one sentence, a comment with the decision or question first. When a type has
no Acceptance Criteria field, the criteria become a section of the description.

## Usage statistics

Every MCP call is appended to `%LOCALAPPDATA%\ado-lens\usage\usage-YYYYMM.jsonl` (tool, arguments, answer size, time,
whether the answer was empty) — never the PAT, never sent anywhere. `adolens stats --days 7 --top 10` shows calls,
average / p95 / maximum answer size in tokens (characters / 4), times and empty answers per tool, plus the largest and
slowest calls. Turn it off with `adolens config --usage-log false`.

To watch the cost while you work:

- `adolens stats --live` in a second terminal prints every call Claude makes as it happens — time, tool, estimated
  tokens, milliseconds, arguments and a running total. It adds nothing to the conversation.
- `--tokens` after any CLI command prints the size of its answer (`[~230 tokens]`).
- `adolens config --show-tokens true` ends every answer with that line, for Claude too (so it can tell you); it costs
  a few tokens per answer, hence off by default.

Estimates are characters / 4; Claude's tokenizer differs a little (Polish text runs somewhat higher).

## CLI

```
adolens item|query|create|update|comment|link|fields|sprint|board|backlog|myday|wiki|wiki-write …
adolens login|logout|connections|config|status|register|unregister|refresh|stats|api|mcp|version
```

`adolens help` lists all options. `adolens api <path>` prints the raw JSON of a GET (for development).
Environment: `ADOLENS_CONFIG` (another settings file), `ADOLENS_DATA_DIR` (another data folder), `ADOLENS_CONNECTION`
(connection for this process), `ADOLENS_USAGE=0` (no usage log).

## Development

The source lives **apart from the installation**, e.g. in `C:\Dev\ado-lens`:

```
AdoLens.sln                 solution (Visual Studio 2022 17.12+ or dotnet CLI)
src\AdoLens\*.cs            code
tests\AdoLens.Tests\        xUnit tests; Azure DevOps is simulated (FakeAdo), no network
build.ps1                   compile; -Deploy replaces the installed binaries, -Test runs the tests first
pack.ps1                    release package (ZIP + SHA-256)
release.ps1 release-notes.ps1   releases (see below); .github\ CI and Release workflows, issue forms
install.ps1 uninstall.ps1   installation
docs\PLAN.md                road map
build\  dist\               output (keep nothing of your own there)
```

Work loop:

```powershell
.\build.ps1                          # build into .\build
.\build\adolens.exe item 123         # try a command without restarting Claude
dotnet test                          # tests (a few seconds)
.\build.ps1 -Test -Deploy            # replace the installed binaries when the tests pass
```

After `-Deploy`, restart Claude Desktop / start a new Claude Code session — MCP loads the binary at start.

### Releasing a new version

Work happens on the branch `dev`; `main` always holds the last regular release. Add a `## X.Y.Z — unreleased`
section to `CHANGELOG.md`, then on `dev`:

```powershell
.\release.ps1 -Version X.Y.Z -Pre     # test release X.Y.Z-beta.N (pre-release)
.\release.ps1 -Version X.Y.Z          # regular release: pull request dev -> main, merge, tag, publish
.\release.ps1 -Version X.Y.Z -DryRun  # checks, scan, build and tests only
```

Both stop at the first failure. They set `<Version>`, scan every file for private names and broken characters, build
and test, commit and push `dev` and wait for CI. A test release is tagged on `dev`; a regular one dates the CHANGELOG
section, merges `dev` into `main` through a pull request and is tagged on `main`, after which `dev` moves to `main`.
Both wait for the Release workflow and check the published package (SHA-256, layout, kind of release). Private names
(customers, partners, people) come from a local list that never enters the repository: `-ScanList <file>`,
`ADOLENS_SCAN_LIST`, `XPPLENS_SCAN_LIST` or `%USERPROFILE%\.claude\skills\xpp-lens-release\scan-names.txt` — one
regular expression per line, `!word` for allowed words. Without a list the release stops. The Release workflow builds
`ado-lens-X.Y.Z.zip` with `pack.ps1` and publishes it with its `.sha256` and the notes from `CHANGELOG.md`
(preview: `.\release-notes.ps1 -Version X.Y.Z`).

### Tests

| File | What it checks |
|---|---|
| `SetupTests.cs` | settings, connections and PAT order (Credential Manager first), command line, Claude configuration edits, Credential Manager, HTTP retries and errors, tool definition budget per mode |
| `MetadataTests.cs` | fetching the structure (levels, inherited process name, bugs on the requirement backlog), change history, compact `ado_fields` answers |
| `BoardTests.cs` | sprint progress, capacity left with days off, attention list, picking sprints, ended sprints, board columns and WIP, moving cards with the state mapping, backlog order and reordering, my day |
| `WikiTests.cs` | page tree, page with version, replace only with the current version (412 never overwrites), append, create over an existing page, search snippets |
| `WorkItemTests.cs` | work item answers, required fields and allowed values before writing, transitions, preview and confirm of bulk updates, mentions, links, queries, HTML ⇄ markdown, PAT never sent to other hosts |

Fixtures use neutral names only (`contoso`, `Demo`, `Anna Nowak`). Never put data from real organizations into tests,
issues or commits.

### Where to add what

| Change | File |
|---|---|
| a new MCP tool | a `*Tools` class with `[Toolset]` in `McpTools.cs` (or its own file) + the query in `*Queries.cs` + a CLI command in `Cli.cs`/`Program.cs`/`CommandLine.cs` + a test + a line in the instructions |
| work item reads, writes, checks | `WorkItems.cs`, `WorkQueries.cs`; output in `WorkItemFormat.cs` |
| sprints, capacity, boards, backlogs, my day | `Boards.cs` (API in `Boards`, answers in `BoardQueries`) |
| wiki | `Wiki.cs` |
| work item structure (types, fields, states) | `MetadataService.cs` (fetch), `MetaStore.cs` (SQLite; raise `SchemaVersion` to refetch), `Metadata.cs` (change detection) |
| rich text | `Html.cs` |
| people | `Identities.cs` |
| connections, PAT, Claude registration | `Settings.cs`, `CredentialStore.cs`, `ClaudeConfigs.cs`, `Cli.cs` |
| modes and tool sets | `Toolsets.cs` |
| HTTP, retries, errors | `AdoClient.cs` |

Documentation is bilingual: every change to `README.md` goes to `README.pl.md` in the same commit.

## Reporting problems

Bugs and ideas: open an issue (the form asks for the version and the command). Security problems:
**Security → Report a vulnerability**, see [SECURITY.md](SECURITY.md). Never paste work items, wiki pages, code, names or
other data from your own or your customers' organizations — reproduce the problem with neutral names.

## Limitations

- Azure DevOps Services (dev.azure.com) only; Azure DevOps Server (on-premises) is not tested.
- Windows x64 (Credential Manager, self-contained package).
- Transitions come from the work item type definition; extra process rules (e.g. a field required only in one state)
  are reported by Azure DevOps when writing, in plain words.
- Rich text is converted to a markdown subset (headings, lists, bold, links, code, simple tables); images become `[image]`.
