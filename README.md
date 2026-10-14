# ADO Lens for Azure DevOps

[English](README.md) | [Polski](README.pl.md)

MCP server that gives Claude (Desktop and Code) compact, process-aware access to Azure DevOps: work items, boards,
wiki, Git and TFVC, pipelines. Built for developers and for Product Owners: a PO mode helps with writing and checking
work, a dev mode adds code, pull requests and pipelines. Answers are short text instead of raw JSON, so they cost few tokens.

> Status: **0.1 — skeleton**. Connections, Claude registration and the local work item structure (`ado_fields`) work;
> the rest follows the plan in [docs/PLAN.md](docs/PLAN.md).

Not affiliated with or endorsed by Microsoft. Azure DevOps is a trademark of Microsoft.
© 2026 WillowDev, [MIT license](LICENSE).

## Install

Windows x64; nothing else is needed (the package is self-contained). In a regular PowerShell window (not as administrator):

```powershell
powershell -ExecutionPolicy Bypass -File .\ado-lens\install.ps1
```

The installer copies the program to `%LOCALAPPDATA%\Programs\ADO Lens`, asks for the Azure DevOps address (paste any
link from the browser), the PAT (hidden input) and the default project, and registers the `ado-lens` server in Claude
Desktop and Claude Code. Running it again from a newer package updates the program and keeps everything else.

### PAT

Create it at `https://dev.azure.com/<organization>/_usersSettings/tokens`. ADO Lens does not restrict anything: what it
can do is what the PAT and your account allow. Suggested scopes: Work Items (Read & write), Code (Read), Wiki (Read &
write), Build (Read), Release (Read), Test Management (Read & write), Project and Team (Read), Identity (Read).

The PAT is stored in **Windows Credential Manager** (`ado-lens:<connection>`), never in a file or in the Claude
configuration. Without a stored PAT the variables `ADOLENS_PAT_<CONNECTION>` and then `AZURE_DEVOPS_PAT` are used.

## Commands

```powershell
adolens login [--org <url>] [--project <name>] [--connection <name>] [--default]   # add / update a connection
adolens connections                          # list connections (* = default)
adolens config --connection contoso --project Demo --team "Demo Team"
adolens config --default-connection contoso --mode po|dev|full
adolens status                               # connection, signed-in user, metadata, Claude registrations
adolens fields [--type Bug] [--field Severity] [--changes 30d] [--all] [--refresh]
adolens refresh [--project Demo]             # fetch the work item structure now
adolens stats --days 7                       # answer sizes (tokens), times, empty answers
adolens register | unregister
```

Settings live in `%LOCALAPPDATA%\ado-lens\adolens.json`; metadata in `%LOCALAPPDATA%\ado-lens\meta\<connection>.db`.

## Tools

| Tool | Answers |
|---|---|
| `ado_status` | version, connection, signed-in user, PAT source, metadata age |
| `ado_fields` | work item types by backlog level (portfolio / requirement / task / bug); for a type: states, transitions, fields, required flags, allowed values; for a field: where it is used and its values; structure changes over time |

## Development

```powershell
dotnet test                  # tests (no network: Azure DevOps is simulated)
.\build.ps1                  # build into .\build
.\build.ps1 -Test -Deploy    # replace the installed binaries when tests pass
.\pack.ps1                   # dist\ado-lens-X.Y.Z.zip (+ .sha256)
```

Documentation is bilingual: every change to `README.md` goes to `README.pl.md` in the same commit.
