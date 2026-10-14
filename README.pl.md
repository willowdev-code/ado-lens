# ADO Lens for Azure DevOps — serwer MCP dla Claude'a

[![CI](https://github.com/willowdev-code/ado-lens/actions/workflows/ci.yml/badge.svg)](https://github.com/willowdev-code/ado-lens/actions/workflows/ci.yml)

[English](README.md) | **Polski**

Daje Claude'owi (Desktop i Code) zwięzły, świadomy procesu dostęp do Azure DevOps: work itemy, tablice, wiki, Git i TFVC,
pipeline'y. Dla deweloperów i Product Ownerów: **tryb PO** pomaga pisać i sprawdzać pracę, **tryb dev** dokłada kod,
pull requesty i pipeline'y. Odpowiedzi to krótki tekst zamiast surowego JSON-a, więc work item kosztuje kilkaset tokenów,
a nie tysiące. Zmiany między wersjami: [CHANGELOG.md](CHANGELOG.md). Plan rozwoju: [docs/PLAN.md](docs/PLAN.md).

© 2026 WillowDev. Udostępnione na [licencji MIT](LICENSE). Projekt niezależny, niezwiązany z Microsoftem;
Azure DevOps jest znakiem towarowym Microsoftu.

> Stan: **0.x — w budowie.** Działają połączenia, rejestracja w Claude, lokalna struktura work itemów i narzędzia work
> itemów; tablice, wiki, pull requesty i pipeline'y — według planu.

## Czym się różni

- **Zna proces każdego projektu.** Typy według poziomu backlogu (portfolio / wymaganie / zadanie / błąd), prawdziwe
  stany i przejścia, pola wymagane, dozwolone wartości i pola własne trafiają z Azure DevOps do lokalnej kopii i są
  sprawdzane *przed* zapisem — niezależnie od tego, jak proces nazywa typy (User Story, Product Backlog Item,
  Requirement albo własne nazwy firmy).
- **Małe odpowiedzi.** Bez pustych pól, osoby jako imię i nazwisko, opisy jako markdown, linki w skrócie
  (`code (2): changeset 523 "Fix …"; build 213864`), techniczne pola tablic ukryte.
- **Bezpieczne zmiany wielu elementów.** Zmiana więcej niż kilku work itemów zwraca najpierw podgląd; zapis następuje
  dopiero po powtórzeniu wywołania z tokenem z podglądu.
- **Git i TFVC** w tym samym projekcie, kilka organizacji obok siebie.
- **Niczego nie ogranicza:** ADO Lens może to, na co pozwala twój PAT i konto.

## Instalacja

Paczki instalacyjnej nie ma w repozytorium (`dist\` jest pomijany przez `.gitignore`). Są dwie drogi:

**A. Z gotowej paczki** — na maszynie docelowej nie trzeba niczego instalować, nawet .NET.

1. Pobierz `ado-lens-X.Y.Z.zip` z zakładki **Releases** tego repozytorium.
2. Rozpakuj i w zwykłym PowerShellu (nie jako administrator) uruchom:

```powershell
powershell -ExecutionPolicy Bypass -File .\ado-lens\install.ps1
```

**B. Z kodu źródłowego** — wymaga .NET 9 SDK.

```powershell
git clone https://github.com/willowdev-code/ado-lens.git C:\Dev\ado-lens
cd C:\Dev\ado-lens
.\pack.ps1                     # tworzy dist\ado-lens\ i dist\ado-lens-X.Y.Z.zip (+ .sha256)
powershell -ExecutionPolicy Bypass -File .\dist\ado-lens\install.ps1
```

Instalator kopiuje program do `%LOCALAPPDATA%\Programs\ADO Lens` (bez uprawnień administratora), pyta o adres Azure
DevOps (wklej dowolny link z przeglądarki — organizacja i projekt zostaną z niego wyciągnięte), PAT (wpisywany
niewidocznie) i domyślny projekt (wybierany z listy), a potem rejestruje serwer `ado-lens` w Claude Desktop i Claude Code.
Jeśli rejestracja musi się zmienić, a Claude Desktop działa, instalator prosi o zamknięcie Claude'a, czeka i uruchamia go
ponownie. Uruchomienie `install.ps1` z nowszej paczki aktualizuje program i zostawia ustawienia, PAT-y i metadane;
`adolens.exe` używany przez otwartą sesję dostaje nazwę `*.old`, więc ta sesja działa dalej.

Przydatne parametry:

```powershell
.\install.ps1 -Organization https://dev.azure.com/contoso/Demo   # bez pytania o adres
.\install.ps1 -Mode dev                                           # po (domyślny) | dev | full
.\install.ps1 -DesktopOnly | -CodeOnly | -NoRegister | -NoRestart
```

Deinstalacja: `uninstall.ps1` w folderze instalacji (usuwa rejestracje tej instalacji w Claude, pyta o zapisane PAT-y
i dane lokalne).

### PAT

Utwórz go na `https://dev.azure.com/<organizacja>/_usersSettings/tokens` dla jednej organizacji, z zakresami, których
ma używać Claude, i krótką ważnością. Proponowane dla PO: Work Items (Read & write), Code (Read), Wiki (Read & write),
Build (Read), Release (Read), Test Management (Read & write), Project and Team (Read), Identity (Read). PAT tylko do
odczytu sprawia, że cały serwer tylko czyta.

PAT trafia do **Menedżera poświadczeń Windows** (`ado-lens:<połączenie>`), nigdy do pliku ani do konfiguracji Claude'a.
Bez zapisanego PAT używane są zmienne `ADOLENS_PAT_<POŁĄCZENIE>`, a potem `AZURE_DEVOPS_PAT`. Szczegóły:
[SECURITY.md](SECURITY.md).

## Konfiguracja po instalacji

```powershell
adolens login --org https://dev.azure.com/fabrikam/Shop --connection fabrikam   # kolejna organizacja
adolens connections                          # lista (* = domyślne)
adolens config                               # pokaż ustawienia
adolens config --connection contoso --project Demo --team "Demo Team"
adolens config --default-connection fabrikam
adolens config --mode dev                    # po | dev | full
adolens config --add-toolset pipelines       # dodatkowy zestaw narzędzi ponad tryb
adolens config --confirm-above 3             # zmiany większej liczby elementów najpierw jako podgląd (0: zawsze)
adolens config --usage-log false
```

Ustawienia siedzą w `%LOCALAPPDATA%\ado-lens\adolens.json` (bez sekretów) — można je też edytować ręcznie.

### Tryby i zestawy narzędzi

Tryb wybiera tylko, które narzędzia widzi Claude: mniej narzędzi to trafniejszy wybór i mniej tokenów w każdej sesji.
Nigdy nie jest uprawnieniem.

| Zestaw | Narzędzia | po | dev | full |
|---|---|:-:|:-:|:-:|
| `core` | `ado_status`, `ado_fields` | ✓ | ✓ | ✓ |
| `work` | `ado_work_item`, `ado_query`, `ado_create`, `ado_update`, `ado_comment`, `ado_link` | ✓ | ✓ | ✓ |
| `boards`, `wiki`, `trace`, `test`, `recipes` | w planie | ✓ | (wiki, trace, recipes) | ✓ |
| `code`, `pr`, `pipelines` | w planie | – | ✓ | ✓ |

## Mapa narzędzi

Na co odpowiada każde narzędzie MCP, kiedy Claude powinien po nie sięgnąć i jaki jest odpowiednik w CLI.

| Narzędzie | Odpowiada na | Kiedy | CLI |
|---|---|---|---|
| `ado_status` | wersja, połączenie, zalogowany użytkownik, źródło PAT, wiek metadanych | sprawdzenie konfiguracji | `adolens status` |
| `ado_fields` | typy według poziomu backlogu; dla typu: stany, przejścia, pola, wymagalność, dozwolone wartości; dla pola: gdzie jest używane i jakie ma wartości; zmiany struktury w czasie | przed zapisem pól albo WIQL; „jakie pola ma Bug”, „co się zmieniło w procesie w tym miesiącu” | `adolens fields --type Bug`, `adolens fields --field Severity`, `adolens fields --changes 30d` |
| `ado_work_item` | jeden lub kilka elementów: stan, osoby, ścieżki, wypełnione pola, opis i kryteria akceptacji jako markdown, rodzic / dzieci / powiązane, changesety, commity, PR-y i buildy; z `include`: komentarze, historia | „o co chodzi w #123”, „kto i kiedy zmienił kryteria” | `adolens item 123 456 --include comments,history --detail summary` |
| `ado_query` | elementy z WIQL (z `@Me`, `@CurrentIteration`…) albo z zapisanego zapytania, po jednej linii | listy, filtry, „otwarte bugi sprintu” | `adolens query --wiql "SELECT …"`, `adolens query --query "Shared Queries/Open bugs"` |
| `ado_create` | tworzy element z markdownu; najpierw sprawdza pola wymagane, dozwolone wartości i osoby; miękkie uwagi o długości | pisanie historii, bugów, zadań | `adolens create --type Bug --title "…" --description "…" --field "Severity=2 - High"` |
| `ado_update` | zmienia jeden lub wiele elementów (id albo WIQL): pola, stan zgodnie z przejściami, osoby, tagi `+a; -b`, iteracja `current`, komentarz; wiele elementów → podgląd + token `confirm` | zamykanie, przenoszenie do sprintu, zmiany masowe | `adolens update 123 --state Done --comment "…"`, `adolens update --wiql "…" --tags "+reviewed"` |
| `ado_comment` | komentarz w markdownie; `@[imię]` oznacza osobę i wysyła jej powiadomienie | pytania, decyzje, raporty | `adolens comment 123 "@[Anna] potwierdź proszę"` |
| `ado_link` | rodzic, dziecko, powiązany, poprzednik, następnik, duplikat, duplikat-czego, testowany-przez, testuje; `remove` usuwa | porządkowanie backlogu | `adolens link 123 456 --type child` |

### Wbudowane zasady pisania

Tworzona treść jest sprawdzana pod kątem miękkich limitów (informacyjnie, nigdy nie blokuje): opis wymagania do ok. 120
słów ze sprawdzalnymi kryteriami akceptacji w osobnym polu (więcej niż 7 kryteriów sugeruje podział), bug do ok. 100
słów plus numerowane kroki, zadanie jednym zdaniem, komentarz z decyzją lub pytaniem na początku. Gdy typ nie ma pola
Acceptance Criteria, kryteria trafiają jako sekcja opisu.

## Statystyki użycia

Każde wywołanie MCP jest dopisywane do `%LOCALAPPDATA%\ado-lens\usage\usage-RRRRMM.jsonl` (narzędzie, argumenty, rozmiar
odpowiedzi, czas, czy wynik był pusty) — nigdy PAT, nigdy nie opuszcza maszyny. `adolens stats --days 7 --top 10`
pokazuje dla każdego narzędzia liczbę wywołań, średni / p95 / maksymalny rozmiar odpowiedzi w tokenach (znaki / 4),
czasy i puste odpowiedzi, a do tego największe i najwolniejsze wywołania. Wyłączenie: `adolens config --usage-log false`.

## CLI

```
adolens item|query|create|update|comment|link|fields …
adolens login|logout|connections|config|status|register|unregister|refresh|stats|api|mcp|version
```

`adolens help` wypisuje wszystkie opcje. `adolens api <ścieżka>` drukuje surowy JSON z GET (do rozwoju).
Zmienne: `ADOLENS_CONFIG` (inny plik ustawień), `ADOLENS_DATA_DIR` (inny folder danych), `ADOLENS_CONNECTION`
(połączenie dla tego procesu), `ADOLENS_USAGE=0` (bez dziennika użycia).

## Rozwój narzędzia

Kod źródłowy mieszka **osobno od instalacji**, np. w `C:\Dev\ado-lens`:

```
AdoLens.sln                 solucja (Visual Studio 2022 17.12+ albo dotnet CLI)
src\AdoLens\*.cs            kod
tests\AdoLens.Tests\        testy xUnit; Azure DevOps jest symulowane (FakeAdo), bez sieci
build.ps1                   kompilacja; -Deploy podmienia binaria w instalacji, -Test najpierw uruchamia testy
pack.ps1                    paczka wydania (ZIP + SHA-256)
install.ps1 uninstall.ps1   instalacja
docs\PLAN.md                plan rozwoju
build\  dist\               wyniki (nie trzymaj tu niczego własnego)
```

Pętla pracy:

```powershell
.\build.ps1                          # kompilacja do .\build
.\build\adolens.exe item 123         # test polecenia bez restartu Claude'a
dotnet test                          # testy (kilka sekund)
.\build.ps1 -Test -Deploy            # podmiana w instalacji, gdy testy przejdą
```

Po `-Deploy` zrestartuj Claude Desktop / zacznij nową sesję Claude Code — MCP ładuje binarium przy starcie.

### Testy

| Plik | Co sprawdza |
|---|---|
| `SetupTests.cs` | ustawienia, połączenia i kolejność PAT (najpierw Menedżer poświadczeń), linia poleceń, edycja konfiguracji Claude'a, Menedżer poświadczeń, ponawianie i błędy HTTP, budżet definicji narzędzi na tryb, faktyczna rejestracja narzędzi |
| `MetadataTests.cs` | pobieranie struktury (poziomy, nazwa procesu dziedziczonego, bugi na backlogu wymagań), historia zmian, zwięzłe odpowiedzi `ado_fields` |
| `WorkItemTests.cs` | odpowiedzi o work itemach, pola wymagane i dozwolone wartości przed zapisem, przejścia stanów, podgląd i potwierdzanie zmian wsadowych, wzmianki, linki, zapytania, HTML ⇄ markdown, PAT nigdy do innych hostów |

Dane testowe mają wyłącznie neutralne nazwy (`contoso`, `Demo`, `Anna Nowak`). Nigdy nie wkładaj danych z prawdziwych
organizacji do testów, zgłoszeń ani commitów.

### Gdzie co dopisać

| Zmiana | Plik |
|---|---|
| nowe narzędzie MCP | klasa `*Tools` z `[Toolset]` w `McpTools.cs` (albo własnym pliku) + zapytanie w `*Queries.cs` + polecenie CLI w `Cli.cs`/`Program.cs`/`CommandLine.cs` + test + linijka w instrukcjach |
| odczyt, zapis i kontrole work itemów | `WorkItems.cs`, `WorkQueries.cs`; wyjście w `WorkItemFormat.cs` |
| struktura work itemów (typy, pola, stany) | `MetadataService.cs` (pobieranie), `MetaStore.cs` (SQLite; podnieś `SchemaVersion`, żeby pobrać od nowa), `Metadata.cs` (wykrywanie zmian) |
| tekst sformatowany | `Html.cs` |
| osoby | `Identities.cs` |
| połączenia, PAT, rejestracja w Claude | `Settings.cs`, `CredentialStore.cs`, `ClaudeConfigs.cs`, `Cli.cs` |
| tryby i zestawy narzędzi | `Toolsets.cs` |
| HTTP, ponawianie, błędy | `AdoClient.cs` |

Dokumentacja jest w dwóch językach: każdą zmianę w `README.md` trzeba odzwierciedlić w `README.pl.md` w tym samym commicie.

## Zgłaszanie problemów

Błędy i pomysły: załóż issue (formularz pyta o wersję i wykonane polecenie). Problemy z bezpieczeństwem:
**Security → Report a vulnerability**, zob. [SECURITY.md](SECURITY.md). Nigdy nie wklejaj work itemów, stron wiki, kodu,
nazw ani innych danych z własnych organizacji ani organizacji klientów — odtwórz problem na neutralnych nazwach.

## Ograniczenia

- Tylko Azure DevOps Services (dev.azure.com); Azure DevOps Server (on-premises) nie jest testowany.
- Windows x64 (Menedżer poświadczeń, samodzielna paczka).
- Przejścia stanów pochodzą z definicji typu; dodatkowe reguły procesu (np. pole wymagane tylko w jednym stanie)
  zgłasza Azure DevOps przy zapisie — prostymi słowami.
- Tekst sformatowany jest zamieniany na podzbiór markdownu (nagłówki, listy, pogrubienie, linki, kod, proste tabele);
  obrazy stają się `[image]`.
