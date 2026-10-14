# Security policy

## Supported versions

Only the latest release receives fixes. Please check the
[Releases](https://github.com/willowdev-code/ado-lens/releases) page and upgrade before reporting.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report them privately through GitHub: open the **Security** tab of this repository and choose
**Report a vulnerability** (or use [this link](https://github.com/willowdev-code/ado-lens/security/advisories/new)).
You will get an answer within a few days; once a fix is released the report can be published with credit to you,
if you wish.

When describing the problem, **do not include work items, wiki pages, code, names or any other data from your own or
your customers' Azure DevOps organizations**. A minimal reproduction with neutral names (for example a `Contoso`
organization and a `Demo` project) is enough.

## How the personal access token is handled

- **What it can do:** ADO Lens does exactly what the PAT and the account allow — it adds no restrictions and no
  extra rights. Create the PAT for the one organization you need, with the scopes you want Claude to use and a short
  expiry. A read-only PAT (Work Items / Code / Wiki / Build: Read) makes the whole server read-only.
- **Storage:** `adolens login` stores the PAT in **Windows Credential Manager** as a generic credential of the
  current Windows user (`ado-lens:<connection>`), kept on this machine only (not roamed) and encrypted by Windows with
  the user's logon secret. It can be viewed or removed in *Control Panel → Credential Manager → Windows Credentials*,
  or with `adolens logout --connection <name>`.
- **Order:** Credential Manager is read first. Only when it holds no PAT for the connection are the environment
  variables `ADOLENS_PAT_<CONNECTION>` and then `AZURE_DEVOPS_PAT` used (CI, testing). `adolens status` shows where the
  PAT came from and warns when a variable holds a different one.
- **Never in files or command lines:** the PAT is typed with hidden input (or piped through standard input with
  `--pat-stdin`). It is not accepted as a command-line argument, so it does not end up in shell history or process
  listings. It is not written to `adolens.json`, to the Claude configuration, to the metadata or usage files, or to logs.
  The installer EXE checks the PAT by running `adolens login --pat-env` with the PAT in an environment variable of that
  one process, cleared right after.
- **Claude Desktop extension (`.mcpb`):** there the PAT is a setting of the extension marked as sensitive; Claude
  Desktop keeps it in the operating system's secure storage and hands it to the server as `AZURE_DEVOPS_PAT`. A PAT in
  Credential Manager still wins. Prefer the installer when Claude Code is used too.
- **Other servers:** `adolens status` reports other MCP entries in the Claude configuration that keep a PAT in plain
  text (for example an older Azure DevOps server). It never changes them; remove the PAT there yourself.
- **Network:** the server talks only to `https://dev.azure.com/<organization>` (and the `vsrm` / `vssps` hosts of the
  same organization) over HTTPS, with the PAT in the `Authorization` header. It opens no network ports.

## Scope

ADO Lens runs locally and talks to Claude over stdin/stdout (MCP). It keeps settings, the copy of the work item
structure and the usage log in `%LOCALAPPDATA%\ado-lens`; nothing is sent anywhere except the Azure DevOps requests
that tools make. Text read from work items, wiki pages and code is returned to Claude as data, and the server
instructions tell Claude to treat it as data, not as instructions. Relevant reports are, for example, ways to make it
leak the PAT, to write outside its own folders, to call hosts other than Azure DevOps, or to make a read tool change data.
