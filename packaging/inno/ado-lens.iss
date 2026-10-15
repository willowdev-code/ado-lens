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
DefaultGroupName=ADO Lens
DisableReadyPage=yes
SetupIconFile={#SourcePath}..\icon\adolens.ico
; The program folder goes on the user's PATH, so "adolens" works in any new terminal.
ChangesEnvironment=yes
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
; Users choose: Polish or English (the choice is kept for the settings console too).
ShowLanguageDialog=yes
CloseApplications=no
VersionInfoVersion={#Version}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#DesktopSkills}\*.zip"; DestDir: "{app}\skills-for-claude-desktop"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Start menu: settings and status open in a console that stays open (adolens commands work there too).
Name: "{group}\{cm:IconSettings}"; Filename: "{cmd}"; Parameters: "/k """"{app}\bin\adolens.exe"" setup"""; WorkingDir: "{app}"; IconFilename: "{app}\bin\adolens.exe"
Name: "{group}\{cm:IconStatus}"; Filename: "{cmd}"; Parameters: "/k """"{app}\bin\adolens.exe"" status"""; WorkingDir: "{app}"; IconFilename: "{app}\bin\adolens.exe"
Name: "{group}\{cm:IconSkills}"; Filename: "{app}\skills-for-claude-desktop"
Name: "{group}\{cm:UninstallProgram,ADO Lens}"; Filename: "{uninstallexe}"; IconFilename: "{app}\bin\adolens.exe"

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\bin"; Check: NeedsAddPath(ExpandConstant('{app}\bin'))

[UninstallDelete]
Type: files; Name: "{app}\bin\*.old"

[UninstallRun]
Filename: "{app}\bin\adolens.exe"; Parameters: "unregister"; Flags: runhidden waituntilterminated; RunOnceId: "unregister"

[Run]
Filename: "{app}\skills-for-claude-desktop"; Description: "{cm:OpenSkills}"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{cm:ReadmeUrl}"; Description: "{cm:OpenReadme}"; Flags: postinstall shellexec skipifsilent unchecked

[CustomMessages]
en.EnvTitle=Environments
en.EnvSub=Where code goes before it reaches production
en.EnvText=ADO Lens found these release stages and branches. Name each environment the way your team says it (for example UAT (sbx03)); leave a field empty to skip it.
en.EnvDetecting=Looking for release stages and branches...
en.EnvNone=No release stages or long-lived branches were found. Claude will ask about environments when it needs them.
en.EnvExisting=Environments are already set: %1 (%2).
en.EnvLater=Later: Start menu > ADO Lens - settings > Environments (change, add, remove).
en.EnvMine=Save for me only (this computer)
en.EnvTeam=Save for the whole team (wiki page "ADO Lens" of the project)
en.EnvSkip=Skip - Claude asks when needed
en.EnvFailed=Saving the environments did not work:%n%n%1
en.ReadmeUrl=https://github.com/willowdev-code/ado-lens#readme
en.IconSettings=ADO Lens - settings
en.IconStatus=ADO Lens - status
en.IconSkills=ADO Lens - skills for Claude Desktop
en.RemoveData=Also remove the stored personal access tokens (Windows Credential Manager) and the settings and cache of ADO Lens?%n%nChoose No to keep them for a later installation.
pl.EnvTitle=Środowiska
pl.EnvSub=Gdzie trafia kod, zanim dojdzie na produkcję
pl.EnvText=ADO Lens znalazł te etapy release'ów i gałęzie. Nazwij każde środowisko tak, jak mówi o nim zespół (np. UAT (sbx03)); puste pole = pomiń.
pl.EnvDetecting=Szukam etapów release'ów i gałęzi...
pl.EnvNone=Nie znaleziono etapów release'ów ani gałęzi. Claude zapyta o środowiska, gdy będą potrzebne.
pl.EnvExisting=Środowiska są już ustawione: %1 (%2).
pl.EnvLater=Później: menu Start > ADO Lens - ustawienia > Środowiska (zmiana, dodanie, usunięcie).
pl.EnvMine=Zapisz tylko dla mnie (ten komputer)
pl.EnvTeam=Zapisz dla całego zespołu (strona wiki "ADO Lens" w projekcie)
pl.EnvSkip=Pomiń - Claude zapyta, gdy będzie trzeba
pl.EnvFailed=Nie udało się zapisać środowisk:%n%n%1
pl.ReadmeUrl=https://github.com/willowdev-code/ado-lens/blob/main/README.pl.md
pl.IconSettings=ADO Lens - ustawienia
pl.IconStatus=ADO Lens - status
pl.IconSkills=ADO Lens - skille dla Claude Desktop
pl.RemoveData=Usunąć też zapisane tokeny dostępu (Menedżer poświadczeń Windows) oraz ustawienia i pamięć podręczną ADO Lens?%n%nWybierz Nie, aby zachować je na potrzeby ponownej instalacji.
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
  EnvPage: TWizardPage;
  EnvInfo, EnvLater: TNewStaticText;
  EnvKeys: TArrayOfString;
  EnvLabels: array of TNewStaticText;
  EnvEdits: array of TNewEdit;
  EnvMine, EnvTeam, EnvSkip: TNewRadioButton;
  EnvDetected: Boolean;

function SetEnvironmentVariable(lpName: String; lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

{ Whether a folder is missing from the user's PATH (compared without case and trailing backslash). }
function NeedsAddPath(Dir: String): Boolean;
var
  Path: String;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
  begin
    Result := True;
    Exit;
  end;
  Result := Pos(';' + Lowercase(RemoveBackslashUnlessRoot(Dir)) + ';', ';' + Lowercase(Path) + ';') = 0;
end;

procedure RemoveFromPath(Dir: String);
var
  Path, Item, Kept: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then Exit;
  Kept := '';
  Path := Path + ';';
  while Path <> '' do
  begin
    P := Pos(';', Path);
    Item := Copy(Path, 1, P - 1);
    Delete(Path, 1, P);
    if (Item <> '') and (Lowercase(RemoveBackslashUnlessRoot(Item)) <> Lowercase(RemoveBackslashUnlessRoot(Dir))) then
    begin
      if Kept <> '' then Kept := Kept + ';';
      Kept := Kept + Item;
    end;
  end;
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Kept);
end;

var
  RemoveUserData: Boolean;

{ Before the files go: ask whether to remove the stored PATs and the local data (default: keep them), and sign out
  with the program while it is still there. After: take the program folder off the PATH and delete the data. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveUserData := not UninstallSilent and
      (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);
    if RemoveUserData then
      Exec(ExpandConstant('{app}\bin\adolens.exe'), 'logout --all', '', SW_HIDE, ewWaitUntilTerminated, Code);
    RemoveCodeSkills;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}\bin'));
    if RemoveUserData then DelTree(ExpandConstant('{localappdata}\ado-lens'), True, True, True);
  end;
end;

function HasClaudeCode: Boolean;
begin
  Result := DirExists(ExpandConstant('{%USERPROFILE}\.claude'));
end;

{ The mode saved in the settings (po when unknown). }
function SavedMode: String;
var
  Text: AnsiString;
  S: String;
begin
  Result := 'po';
  if not LoadStringFromFile(ExpandConstant('{localappdata}\ado-lens\adolens.json'), Text) then Exit;
  S := Lowercase(String(Text));
  if Pos('"mode": "dev"', S) > 0 then Result := 'dev'
  else if Pos('"mode": "full"', S) > 0 then Result := 'full';
end;

{ Claude Code loads the description of every installed skill in each session, so only the skills of the mode go
  there: po-* for a Product Owner, dev-* for a developer, all for everything. Claude Desktop gets ZIPs of all. }
procedure InstallCodeSkills(Mode: String);
var
  F: TFindRec;
  Src, Dst, Name: String;
begin
  if not HasClaudeCode then Exit;
  Src := ExpandConstant('{app}\skills');
  if not FindFirst(Src + '\*', F) then Exit;
  try
    repeat
      Name := F.Name;
      if (F.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (Name <> '.') and (Name <> '..') then
      begin
        Dst := ExpandConstant('{%USERPROFILE}\.claude\skills\') + Name;
        if (Mode = 'full') or ((Mode = 'dev') and (Copy(Name, 1, 4) = 'dev-')) or ((Mode = 'po') and (Copy(Name, 1, 3) = 'po-')) then
        begin
          ForceDirectories(Dst);
          FileCopy(Src + '\' + Name + '\SKILL.md', Dst + '\SKILL.md', False);
        end
        else if FileExists(Dst + '\SKILL.md') then DelTree(Dst, True, True, True);
      end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

{ Uninstall: the skills of this program in Claude Code. }
procedure RemoveCodeSkills;
var
  F: TFindRec;
  Src, Dst: String;
begin
  Src := ExpandConstant('{app}\skills');
  if not FindFirst(Src + '\*', F) then Exit;
  try
    repeat
      if (F.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (F.Name <> '.') and (F.Name <> '..') then
      begin
        Dst := ExpandConstant('{%USERPROFILE}\.claude\skills\') + F.Name;
        if FileExists(Dst + '\SKILL.md') then DelTree(Dst, True, True, True);
      end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
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

  EnvPage := CreateCustomPage(ModePage.ID, CustomMessage('EnvTitle'), CustomMessage('EnvSub'));
  EnvInfo := TNewStaticText.Create(EnvPage);
  EnvInfo.Parent := EnvPage.Surface;
  EnvInfo.AutoSize := False;
  EnvInfo.WordWrap := True;
  EnvInfo.Width := EnvPage.SurfaceWidth;
  EnvInfo.Height := ScaleY(40);
  EnvInfo.Caption := CustomMessage('EnvDetecting');
  EnvMine := TNewRadioButton.Create(EnvPage);
  EnvTeam := TNewRadioButton.Create(EnvPage);
  EnvSkip := TNewRadioButton.Create(EnvPage);
  EnvMine.Parent := EnvPage.Surface;
  EnvTeam.Parent := EnvPage.Surface;
  EnvSkip.Parent := EnvPage.Surface;
  EnvMine.Caption := CustomMessage('EnvMine');
  EnvTeam.Caption := CustomMessage('EnvTeam');
  EnvSkip.Caption := CustomMessage('EnvSkip');
  EnvMine.Width := EnvPage.SurfaceWidth;
  EnvTeam.Width := EnvPage.SurfaceWidth;
  EnvSkip.Width := EnvPage.SurfaceWidth;
  EnvMine.Checked := True;
  EnvMine.Visible := False;
  EnvTeam.Visible := False;
  EnvSkip.Visible := False;
  EnvLater := TNewStaticText.Create(EnvPage);
  EnvLater.Parent := EnvPage.Surface;
  EnvLater.AutoSize := False;
  EnvLater.WordWrap := True;
  EnvLater.Width := EnvPage.SurfaceWidth;
  EnvLater.Height := ScaleY(30);
  EnvLater.Caption := CustomMessage('EnvLater');
  EnvLater.Top := EnvPage.SurfaceHeight - EnvLater.Height;
end;

{ Fills the environment page from "adolens setup --detect": the map already in use, or one row per stage or branch
  found with a suggested name. }
procedure DetectEnvironments;
var
  Exe, OutFile, Line, Key, Guess: String;
  Lines: TArrayOfString;
  I, P, N, Top: Integer;
  Code: Integer;
begin
  EnvDetected := True;
  Exe := ExpandConstant('{tmp}\adolens.exe');
  OutFile := ExpandConstant('{tmp}\adolens-envs.txt');
  DeleteFile(OutFile);
  Exec(Exe, 'setup --detect --out "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not LoadStringsFromFile(OutFile, Lines) then SetArrayLength(Lines, 0);
  N := 0;
  SetArrayLength(EnvKeys, 0);
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    if Copy(Line, 1, 9) = 'existing' + #9 then
    begin
      Delete(Line, 1, 9);
      P := Pos(#9, Line);
      EnvInfo.Caption := FmtMessage(CustomMessage('EnvExisting'), [Copy(Line, 1, P - 1), Copy(Line, P + 1, Length(Line))]);
      Exit;
    end;
    if (Copy(Line, 1, 4) = 'row' + #9) and (N < 8) then
    begin
      Delete(Line, 1, 4);
      P := Pos(#9, Line);
      if P > 0 then
      begin
        Key := Copy(Line, 1, P - 1);
        Guess := Copy(Line, P + 1, Length(Line));
      end
      else
      begin
        Key := Line;
        Guess := '';
      end;
      N := N + 1;
      SetArrayLength(EnvKeys, N);
      SetArrayLength(EnvLabels, N);
      SetArrayLength(EnvEdits, N);
      EnvKeys[N - 1] := Key;
      Top := EnvInfo.Top + EnvInfo.Height + ScaleY(4) + (N - 1) * ScaleY(26);
      EnvLabels[N - 1] := TNewStaticText.Create(EnvPage);
      EnvLabels[N - 1].Parent := EnvPage.Surface;
      EnvLabels[N - 1].Caption := Key;
      EnvLabels[N - 1].Top := Top + ScaleY(3);
      EnvLabels[N - 1].Width := EnvPage.SurfaceWidth div 2 - ScaleX(8);
      EnvEdits[N - 1] := TNewEdit.Create(EnvPage);
      EnvEdits[N - 1].Parent := EnvPage.Surface;
      EnvEdits[N - 1].Left := EnvPage.SurfaceWidth div 2;
      EnvEdits[N - 1].Top := Top;
      EnvEdits[N - 1].Width := EnvPage.SurfaceWidth div 2;
      EnvEdits[N - 1].Text := Guess;
    end;
  end;
  if N = 0 then
  begin
    EnvInfo.Caption := CustomMessage('EnvNone');
    Exit;
  end;
  EnvInfo.Caption := CustomMessage('EnvText');
  Top := EnvInfo.Top + EnvInfo.Height + ScaleY(10) + N * ScaleY(26);
  EnvMine.Top := Top;
  EnvTeam.Top := Top + ScaleY(20);
  EnvSkip.Top := Top + ScaleY(40);
  EnvMine.Visible := True;
  EnvTeam.Visible := True;
  EnvSkip.Visible := True;
end;

{ Saves the names typed on the environment page; returns False to stay on the page when it failed. }
function SaveEnvironments: Boolean;
var
  Env, Name, Scope, Output: String;
  I, Code: Integer;
begin
  Result := True;
  if EnvSkip.Checked or (GetArrayLength(EnvKeys) = 0) then Exit;
  Env := '';
  for I := 0 to GetArrayLength(EnvKeys) - 1 do
  begin
    Name := Trim(EnvEdits[I].Text);
    StringChangeEx(Name, '"', '', True);
    StringChangeEx(Name, ';', ',', True);
    if Name <> '' then
    begin
      if Env <> '' then Env := Env + '; ';
      Env := Env + EnvKeys[I] + '=' + Name;
    end;
  end;
  if Env = '' then Exit;
  if EnvTeam.Checked then Scope := 'team' else Scope := 'local';
  Code := RunAdoLens(ExpandConstant('{tmp}\adolens.exe'), 'setup --env "' + Env + '" --scope ' + Scope, Output);
  if Code <> 0 then
  begin
    MsgBox(FmtMessage(CustomMessage('EnvFailed'), [Output]), mbError, MB_OK);
    Result := False;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := HasConnection and ((PageID = ConnPage.ID) or (PageID = PatPage.ID) or (PageID = ModePage.ID) or (PageID = EnvPage.ID));
end;

{ The token is checked before anything is installed: adolens.exe runs from the temporary folder, signs in and keeps
  the token in Windows Credential Manager. It goes over an environment variable, never on a command line. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Exe, Params, Output: String;
  Code: Integer;
begin
  Result := True;
  if CurPageID = EnvPage.ID then
    Result := SaveEnvironments
  else if CurPageID = ConnPage.ID then
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
  { The language chosen in the installer is kept for the settings console. }
  if not WizardSilent then RunAdoLens(Exe, 'config --language ' + ActiveLanguage, Output);
  if not HasConnection then
  begin
    case ModePage.SelectedValueIndex of
      1: Mode := 'dev';
      2: Mode := 'full';
    else
      Mode := 'po';
    end;
    RunAdoLens(Exe, 'config --mode ' + Mode, Output);
  end
  else Mode := SavedMode;
  InstallCodeSkills(Mode);
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
  if (CurPageID = EnvPage.ID) and not EnvDetected then
  begin
    WizardForm.NextButton.Enabled := False;
    WizardForm.Repaint;
    try
      DetectEnvironments;
    finally
      WizardForm.NextButton.Enabled := True;
    end;
  end;
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('Registered');
end;
