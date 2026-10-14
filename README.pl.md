# ADO Lens for Azure DevOps

[English](README.md) | **Polski**

Serwer MCP, który daje Claude'owi (Desktop i Code) zwięzły, świadomy procesu dostęp do Azure DevOps: work itemy,
tablice, wiki, Git i TFVC, pipeline'y. Dla deweloperów i Product Ownerów: tryb PO pomaga pisać i sprawdzać pracę,
tryb dev dokłada kod, pull requesty i pipeline'y. Odpowiedzi to krótki tekst zamiast surowego JSON-a, więc kosztują mało tokenów.

> Stan: **0.1 — szkielet**. Działają połączenia, rejestracja w Claude i lokalna struktura work itemów (`ado_fields`);
> reszta według planu w [docs/PLAN.md](docs/PLAN.md).

Projekt niezależny, niezwiązany z Microsoftem. Azure DevOps jest znakiem towarowym Microsoftu.
© 2026 WillowDev, [licencja MIT](LICENSE).

## Instalacja

Windows x64; nic więcej nie jest potrzebne (paczka jest samodzielna). W zwykłym oknie PowerShella (nie jako administrator):

```powershell
powershell -ExecutionPolicy Bypass -File .\ado-lens\install.ps1
```

Instalator kopiuje program do `%LOCALAPPDATA%\Programs\ADO Lens`, pyta o adres Azure DevOps (wklej dowolny link
z przeglądarki), PAT (wpisywany niewidocznie) i domyślny projekt, a potem rejestruje serwer `ado-lens` w Claude Desktop
i Claude Code. Ponowne uruchomienie z nowszej paczki aktualizuje program i zostawia resztę bez zmian.

### PAT

Utwórz go na `https://dev.azure.com/<organizacja>/_usersSettings/tokens`. ADO Lens niczego nie ogranicza: może to, na co
pozwala PAT i twoje konto. Proponowane zakresy: Work Items (Read & write), Code (Read), Wiki (Read & write),
Build (Read), Release (Read), Test Management (Read & write), Project and Team (Read), Identity (Read).

PAT trafia do **Menedżera poświadczeń Windows** (`ado-lens:<połączenie>`), nigdy do pliku ani do konfiguracji Claude'a.
Bez zapisanego PAT używane są zmienne `ADOLENS_PAT_<POŁĄCZENIE>`, a potem `AZURE_DEVOPS_PAT`.

## Polecenia

```powershell
adolens login [--org <url>] [--project <nazwa>] [--connection <nazwa>] [--default]   # dodaj / zmień połączenie
adolens connections                          # lista połączeń (* = domyślne)
adolens config --connection contoso --project Demo --team "Demo Team"
adolens config --default-connection contoso --mode po|dev|full
adolens status                               # połączenie, zalogowany użytkownik, metadane, rejestracje w Claude
adolens fields [--type Bug] [--field Severity] [--changes 30d] [--all] [--refresh]
adolens refresh [--project Demo]             # pobierz teraz strukturę work itemów
adolens stats --days 7                       # rozmiary odpowiedzi (tokeny), czasy, puste odpowiedzi
adolens register | unregister
```

Ustawienia: `%LOCALAPPDATA%\ado-lens\adolens.json`; metadane: `%LOCALAPPDATA%\ado-lens\meta\<połączenie>.db`.

## Narzędzia

| Narzędzie | Odpowiada na |
|---|---|
| `ado_status` | wersja, połączenie, zalogowany użytkownik, źródło PAT, wiek metadanych |
| `ado_fields` | typy work itemów według poziomu backlogu (portfolio / wymaganie / zadanie / błąd); dla typu: stany, przejścia, pola, wymagalność, dozwolone wartości; dla pola: gdzie jest używane i jakie ma wartości; zmiany struktury w czasie |

## Rozwój

```powershell
dotnet test                  # testy (bez sieci: Azure DevOps jest symulowane)
.\build.ps1                  # kompilacja do .\build
.\build.ps1 -Test -Deploy    # podmiana zainstalowanych binariów, gdy testy przejdą
.\pack.ps1                   # dist\ado-lens-X.Y.Z.zip (+ .sha256)
```

Dokumentacja jest dwujęzyczna: każda zmiana w `README.md` idzie do `README.pl.md` w tym samym commicie.
