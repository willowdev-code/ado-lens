; ADO Lens installer: per user, no administrator rights, a wizard with windows.
; Built by pack.ps1:  ISCC /DVersion=X.Y.Z /DSource=<staged package> /DDesktopSkills=<skill zips> ado-lens.iss
; The wizard calls adolens.exe for the real work (login, mode, register), so the same steps stay scriptable.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\..\dist\ado-lens"
#endif
#ifndef DesktopSkills
  #define DesktopSkills "..\..\dist\desktop-skills"
#endif
#ifndef OutDir
  #define OutDir "..\..\dist"
#endif

[Setup]
AppId={{6F1C2B9E-4D7A-4E21-9B8C-3A5D0E7F1A42}
AppName=ADO Lens
AppVersion={#Version}
AppVerName=ADO Lens {#Version}
AppPublisher=WillowDev
AppPublisherURL=https://github.com/willowdev-code/ado-lens
AppSupportURL=https://github.com/willowdev-code/ado-lens/issues
DefaultDirName={localappdata}\Programs\ADO Lens
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=ado-lens-setup-{#Version}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=ADO Lens
UninstallDisplayIcon={app}\bin\adolens.exe
LicenseFile={#Source}\LICENSE
ShowLanguageDialog=auto
CloseApplications=no
VersionInfoVersion={#Version}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#DesktopSkills}\*.zip"; DestDir: "{app}\skills-for-claude-desktop"; Flags: ignoreversion skipifsourcedoesntexist
; Claude Code reads skills from %USERPROFILE%\.claude\skills (only when Claude Code is there); removed on uninstall.
Source: "{#Source}\skills\*"; DestDir: "{%USERPROFILE}\.claude\skills"; Flags: recursesubdirs ignoreversion; Check: HasClaudeCode

[UninstallRun]
Filename: "{app}\bin\adolens.exe"; Parameters: "unregister"; Flags: runhidden waituntilterminated; RunOnceId: "unregister"

[Run]
Filename: "{app}\skills-for-claude-desktop"; Description: "{cm:OpenSkills}"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "https://github.com/willowdev-code/ado-lens#readme"; Description: "{cm:OpenReadme}"; Flags: postinstall shellexec skipifsilent unchecked

[CustomMessages]
en.ConnTitle=Azure DevOps
en.ConnSub=Where your work items are
en.ConnText=Paste a link to your Azure DevOps project from the browser (for example https://dev.azure.com/contoso/Sales), or the organization name.
en.ConnOrg=Organization or project link:
en.ConnProject=Project (optional when the link has it):
en.PatTitle=Personal access token
en.PatSub=ADO Lens signs in with a personal access token (PAT)
en.PatText=Create one in Azure DevOps: your picture (top right) > Personal access tokens > New Token. Scopes: Work Items (Read and Write), Code (Read), Wiki (Read and Write), Build (Read), Release (Read), Test Management (Read and Write), Project and Team (Read), Identity (Read). The token is kept in Windows Credential Manager, never in a file.
en.PatLabel=Token:
en.PatOpen=Open the token page
en.ModeTitle=Who uses ADO Lens
en.ModeSub=This only chooses which tools Claude sees; it can be changed later
en.ModePo=Product Owner / analyst: work items, sprints, board, wiki, checking work, tests
en.ModeDev=Developer: work items, wiki, code, pull requests, pipelines
en.ModeFull=Everything
en.Checking=Checking the connection...
en.NeedOrg=Give the organization or a project link.
en.NeedPat=Paste the token.
en.NeedProject=The organization has several projects - type the one you work in into the Project field (back one page):%n%n%1
en.LoginFailed=The connection did not work:%n%n%1%n%nCheck the link and the token, then try again.
en.QuitDesktop=Claude Desktop is running and would overwrite its configuration.%n%nQuit it (tray icon next to the clock > Quit), then click OK. Cancel skips Claude Desktop for now (run "adolens register" later).
en.Registered=ADO Lens is ready. Open Claude and ask, for example: "show my current sprint".
en.OpenSkills=Open the skills for Claude Desktop (upload them in Settings > Capabilities > Skills)
en.OpenReadme=Read how to use ADO Lens
pl.ConnTitle=Azure DevOps
pl.ConnSub=Gdzie są twoje work itemy
pl.ConnText=Wklej link do projektu Azure DevOps z przeglądarki (np. https://dev.azure.com/contoso/Sprzedaz) albo nazwę organizacji.
pl.ConnOrg=Organizacja lub link do projektu:
pl.ConnProject=Projekt (niepotrzebny, gdy jest w linku):
pl.PatTitle=Token dostępu
pl.PatSub=ADO Lens loguje się osobistym tokenem dostępu (PAT)
pl.PatText=Utwórz go w Azure DevOps: twoje zdjęcie (prawy górny róg) > Personal access tokens > New Token. Zakresy: Work Items (Read and Write), Code (Read), Wiki (Read and Write), Build (Read), Release (Read), Test Management (Read and Write), Project and Team (Read), Identity (Read). Token trafia do Menedżera poświadczeń Windows, nigdy do pliku.
pl.PatLabel=Token:
pl.PatOpen=Otwórz stronę tokenów
pl.ModeTitle=Kto używa ADO Lens
pl.ModeSub=To tylko wybór narzędzi widocznych dla Claude'a; można zmienić później
pl.ModePo=Product Owner / analityk: work itemy, sprinty, tablica, wiki, sprawdzanie wykonania, testy
pl.ModeDev=Programista: work itemy, wiki, kod, pull requesty, pipeline'y
pl.ModeFull=Wszystko
pl.Checking=Sprawdzanie połączenia...
pl.NeedOrg=Podaj organizację albo link do projektu.
pl.NeedPat=Wklej token.
pl.NeedProject=Organizacja ma kilka projektów - wpisz swój w polu Projekt (strona wstecz):%n%n%1
pl.LoginFailed=Połączenie nie działa:%n%n%1%n%nSprawdź link i token, potem spróbuj ponownie.
pl.QuitDesktop=Claude Desktop jest uruchomiony i nadpisałby swoją konfigurację.%n%nZamknij go (ikona przy zegarze > Quit), potem kliknij OK. Anuluj pomija na razie Claude Desktop (później: "adolens register").
pl.Registered=ADO Lens jest gotowy. Otwórz Claude'a i zapytaj np.: "pokaż mój bieżący sprint".
pl.OpenSkills=Otwórz skille dla Claude Desktop (wgraj je w Ustawienia > Capabilities > Skills)
pl.OpenReadme=Przeczytaj, jak używać ADO Lens

[Code]
var
  ConnPage: TInputQueryWizardPage;
  PatPage: TInputQueryWizardPage;
  ModePage: TInputOptionWizardPage;
  HasConnection: Boolean;

function SetEnvironmentVariable(lpName: String; lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function HasClaudeCode: Boolean;
begin
  Result := DirExists(ExpandConstant('{%USERPROFILE}\.claude'));
end;

function SettingsFile: String;
begin
  Result := ExpandConstant('{localappdata}\ado-lens\adolens.json');
end;

{ An installation that already has a connection (an update) skips the questions. }
function ExistingConnection: Boolean;
var
  Text: AnsiString;
begin
  Result := LoadStringFromFile(SettingsFile, Text) and (Pos('"organization"', Lowercase(String(Text))) > 0);
end;

{ Runs adolens.exe with its output in a file; returns the exit code and the output. }
function RunAdoLens(const Exe, Params: String; var Output: String): Integer;
var
  OutFile: String;
  Text: AnsiString;
  Code: Integer;
begin
  OutFile := ExpandConstant('{tmp}\adolens-out.txt');
  DeleteFile(OutFile);
  if not Exec(ExpandConstant('{cmd}'), '/C ""' + Exe + '" ' + Params + ' > "' + OutFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Output := '';
  if LoadStringFromFile(OutFile, Text) then Output := Trim(String(Text));
  Result := Code;
end;

procedure OpenTokenPage(Sender: TObject);
var
  Org: String;
  Code: Integer;
begin
  Org := Trim(ConnPage.Values[0]);
  if Pos('dev.azure.com/', Lowercase(Org)) > 0 then
  begin
    Delete(Org, 1, Pos('dev.azure.com/', Lowercase(Org)) + Length('dev.azure.com/') - 1);
    if Pos('/', Org) > 0 then Org := Copy(Org, 1, Pos('/', Org) - 1);
  end;
  if Org = '' then
    ShellExec('open', 'https://dev.azure.com', '', '', SW_SHOWNORMAL, ewNoWait, Code)
  else
    ShellExec('open', 'https://dev.azure.com/' + Org + '/_usersSettings/tokens', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure InitializeWizard;
var
  Button: TNewButton;
begin
  HasConnection := ExistingConnection;

  ConnPage := CreateInputQueryPage(wpLicense, CustomMessage('ConnTitle'), CustomMessage('ConnSub'), CustomMessage('ConnText'));
  ConnPage.Add(CustomMessage('ConnOrg'), False);
  ConnPage.Add(CustomMessage('ConnProject'), False);

  PatPage := CreateInputQueryPage(ConnPage.ID, CustomMessage('PatTitle'), CustomMessage('PatSub'), CustomMessage('PatText'));
  PatPage.Add(CustomMessage('PatLabel'), True);
  Button := TNewButton.Create(PatPage);
  Button.Parent := PatPage.Surface;
  Button.Caption := CustomMessage('PatOpen');
  Button.Width := ScaleX(200);
  Button.Height := ScaleY(25);
  Button.Top := PatPage.Edits[0].Top + PatPage.Edits[0].Height + ScaleY(12);
  Button.OnClick := @OpenTokenPage;

  ModePage := CreateInputOptionPage(PatPage.ID, CustomMessage('ModeTitle'), CustomMessage('ModeSub'), '', True, False);
  ModePage.Add(CustomMessage('ModePo'));
  ModePage.Add(CustomMessage('ModeDev'));
  ModePage.Add(CustomMessage('ModeFull'));
  ModePage.SelectedValueIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := HasConnection and ((PageID = ConnPage.ID) or (PageID = PatPage.ID) or (PageID = ModePage.ID));
end;

{ The token is checked before anything is installed: adolens.exe runs from the temporary folder, signs in and keeps
  the token in Windows Credential Manager. It goes over an environment variable, never on a command line. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Exe, Params, Output: String;
  Code: Integer;
begin
  Result := True;
  if CurPageID = ConnPage.ID then
  begin
    if Trim(ConnPage.Values[0]) = '' then
    begin
      MsgBox(CustomMessage('NeedOrg'), mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = PatPage.ID then
  begin
    if Trim(PatPage.Values[0]) = '' then
    begin
      MsgBox(CustomMessage('NeedPat'), mbError, MB_OK);
      Result := False;
      Exit;
    end;
    ExtractTemporaryFile('adolens.exe');
    Exe := ExpandConstant('{tmp}\adolens.exe');
    SetEnvironmentVariable('ADOLENS_SETUP_PAT', Trim(PatPage.Values[0]));
    Params := 'login --org "' + Trim(ConnPage.Values[0]) + '" --pat-env ADOLENS_SETUP_PAT --default';
    if Trim(ConnPage.Values[1]) <> '' then Params := Params + ' --project "' + Trim(ConnPage.Values[1]) + '"';
    try
      Code := RunAdoLens(Exe, Params, Output);
      if Code = 4 then
      begin
        MsgBox(FmtMessage(CustomMessage('NeedProject'), [Output]), mbInformation, MB_OK);
        Result := False;
      end
      else if Code <> 0 then
      begin
        MsgBox(FmtMessage(CustomMessage('LoginFailed'), [Output]), mbError, MB_OK);
        Result := False;
      end;
    finally
      SetEnvironmentVariable('ADOLENS_SETUP_PAT', '');
    end;
  end;
end;

{ A running adolens.exe (an open Claude session) cannot be overwritten but can be renamed; the server removes *.old
  files when it starts. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\bin\adolens.exe');
  if FileExists(Exe) then
    RenameFile(Exe, Exe + '.' + GetDateTimeString('yyyymmddhhnnss', #0, #0) + '.old');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe, Output, Mode: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  Exe := ExpandConstant('{app}\bin\adolens.exe');
  if not HasConnection then
  begin
    case ModePage.SelectedValueIndex of
      1: Mode := 'dev';
      2: Mode := 'full';
    else
      Mode := 'po';
    end;
    RunAdoLens(Exe, 'config --mode ' + Mode, Output);
  end;
  { 7: Claude Desktop is running and would write its old configuration back. }
  Code := RunAdoLens(Exe, 'register', Output);
  while (Code = 7) and not WizardSilent do
  begin
    if MsgBox(CustomMessage('QuitDesktop'), mbInformation, MB_OKCANCEL) <> IDOK then Break;
    Code := RunAdoLens(Exe, 'register', Output);
  end;
  if (Code = 7) or ((Code <> 0) and not WizardSilent) then RunAdoLens(Exe, 'register --code', Output);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('Registered');
end;
