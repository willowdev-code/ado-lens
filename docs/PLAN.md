# ado-lens — plan

**Ogólny serwer MCP dla Azure DevOps** (następca Tiberriver w codziennej pracy — dla deweloperów i dla PO) z **trybami
pracy** dopasowanymi do roli. Tryb PO pomaga w work itemach, tablicy, sprintach i wiki, a przede wszystkim w
**weryfikacji, czy zrobiono to, o co proszono**, i w wyjaśnianiu, jak działa funkcjonalność. Tryb dev daje PR-y,
pipeline'y, kod, Git i TFVC. Zbudowany na wzór xpp-lens / tfvc-explorer-mcp: jeden plik `adolens.exe`
(.NET 9, self-contained), instalator, samoaktualizacja, rejestracja w Claude Desktop i Claude Code, CLI z tymi samymi
poleceniami co MCP.

**Nazewnictwo**
| Gdzie | Forma |
|---|---|
| nazwa produktu (README, instalator, „Aplikacje” w Windows, Claude) | **ADO Lens** — podtytuł „for Azure DevOps”, wydawca WillowDev |
| repozytorium GitHub | `willowdev-code/ado-lens` |
| plik wykonywalny / polecenie CLI | `adolens.exe` / `adolens` |
| nazwa serwera MCP w Claude | `ado-lens` |
| narzędzia MCP | `ado_*` (np. `ado_work_item`, `ado_trace`) |
| kod .NET | solucja `AdoLens.sln`, projekt i przestrzeń nazw `AdoLens` |
| folder instalacji / danych | `%LOCALAPPDATA%\Programs\ADO Lens` / `%LOCALAPPDATA%\ado-lens` |
| instalator / paczka | `ADOLens-Setup-X.Y.Z.exe` / `ado-lens-X.Y.Z.zip` |
| konfiguracja / zmienne | `adolens.json`, `.adolens.json` w repo, `ADOLENS_*` |

---

## 1. Dla kogo i co ma się zmienić względem Tiberriver

**Zestawy narzędzi i tryby.** Narzędzia są pogrupowane w zestawy (toolsets); tryb to gotowa kombinacja zestawów
plus skille i prompty dla danej roli. Tryb wybiera się w kreatorze, zmienia `adolens config --mode dev` albo per repo
w `.adolens.json`; dowolny zestaw można dołożyć ręcznie (`--add-toolset pipelines`). Tryb nie ogranicza uprawnień —
decyduje tylko, które narzędzia Claude widzi (mniej narzędzi = trafniejszy wybór, mniej tokenów w każdej rozmowie).

| Zestaw | Zawartość | PO | dev | full |
|---|---|:-:|:-:|:-:|
| `core` | status, kontekst, połączenia, metadane (`ado_fields`), wyszukiwanie łączone | ✓ | ✓ | ✓ |
| `work` | work itemy: odczyt, tworzenie z szablonu, edycja, stany, komentarze, linki, historia, zmiany wsadowe | ✓ | ✓ | ✓ |
| `boards` | sprinty, backlog, tablice, pojemność, „co nowego”, higiena backlogu | ✓ | – | ✓ |
| `wiki` | odczyt, wyszukiwanie, edycja stron | ✓ | ✓ | ✓ |
| `trace` | work item → kod → build → release/LCS, diff z redukcją szumu, historia pliku | ✓ | ✓ | ✓ |
| `code` | repozytoria Git i TFVC: pliki, commity, changesety, gałęzie, scalenia, Code Search | – | ✓ | ✓ |
| `pr` | PR-y: lista „do mojego przeglądu”, diff, wątki, komentarze w linii, głosowanie, tworzenie, uzupełnianie | – | ✓ | ✓ |
| `pipelines` | buildy i release'y: status, przyczyna porażki z logu (tylko istotny fragment), uruchomienie, artefakty | – | ✓ | ✓ |
| `test` | plany, zestawy, Test Case'y z kryteriów akceptacji, wyniki | ✓ | – | ✓ |
| `recipes` | przepisy/automatyzacje | ✓ | ✓ | ✓ |

Funkcje szczególnie dla dewelopera (poza tym, co ma Tiberriver):
- **„Dlaczego ten kod istnieje”**: plik / obiekt / linia → commit lub changeset → work item → kryteria i dyskusja.
- **Start pracy nad work itemem**: gałąź `feature/123-krótki-tytuł` od właściwej bazy (`dev`), stan → In Progress,
  przypisanie do mnie; przy PR — automatyczne podpięcie work itemu (wymóg polityki gałęzi) i opis z kryteriów.
- **Przegląd PR** z kontekstem work itemu: Claude widzi, czego chciano, i komentuje zmiany względem kryteriów.
- **Czerwony build**: z logu wycinany jest tylko błąd z kontekstem, a nie całe tysiące linii.
- **Gdzie jest moja zmiana**: czy PR/changeset dotarł do `staging` (UAT) i `master` (PROD) i czy release `lcs` przeszedł.
- Współpraca z **xpp-lens**: gdy jest zainstalowany, skille dev łączą work item z obiektami X++ z lokalnego indeksu.

| Tiberriver (dziś) | ado-lens |
|---|---|
| `npx` + Node.js + PAT ręcznie wpisany jawnym tekstem w JSON-ie | instalator `.exe` z kreatorem, PAT w Menedżerze poświadczeń Windows (lub zmiennej środowiskowej), nigdy w konfiguracji Claude'a |
| ~55 narzędzi naraz, niezależnie od roli | zestawy narzędzi i tryby (PO / dev / full) — Claude widzi to, czego potrzebuje dana rola |
| zwraca surowy JSON i HTML z pól | zwięzły markdown, nazwy pól tak, jak widzi je PO („Kryteria akceptacji”, nie `Microsoft.VSTS.Common.AcceptanceCriteria`) |
| zapis od razu | zmiany wsadowe i wrażliwe: **podgląd → potwierdzenie**; dziennik zmian i „cofnij moją ostatnią zmianę” |
| brak wiedzy o procesie | zna proces projektu: typy, stany i dozwolone przejścia, pola wymagane, kolumny tablicy, zespoły, sprinty, ludzi |
| Git i TFVC osobno (twój tfvc-explorer) | jedno narzędzie śledzenia: work item → changesety/commity/PR → pliki → build → wdrożenie |

Funkcje Tiberriver, które działają dobrze (lista/odczyt/edycja WI, wiki, PR, wyszukiwanie), przenosimy 1:1 jako bazę.

---

## 1a. Rozpoznanie na prawdziwej organizacji (2026-10-14, tylko odczyt)

- **Jeden projekt ma Git i TFVC naraz**: ok. 40 repozytoriów Git oraz work itemy z linkami „Fixed in Changeset”
  (`vstfs:///VersionControl/Changeset/…`). Obsługa obu w jednym projekcie jest więc konieczna, a nie opcjonalna.
- **Własny proces dziedziczony** (typy z prefiksem dostawcy procesu, np. `<Vendor>Scrum.*`): ok. 55 typów work
  itemów, własne pola (`Custom.*`), pola tablic Kanban (`WEF_<id>_Kanban.Column` — po jednym zestawie na tablicę),
  rzeczywiste stany np. Committed / In Progress / Done. Wszystko to są dostosowania danej organizacji, nie ADO —
  u każdej firmy inne, więc kod niczego z tego nie zakłada, tylko czyta strukturę z API. Epic i Feature bywają
  wyłączone i zastąpione własnymi typami portfolio. Nazwa procesu dziedziczonego jest pod `System.ProcessTemplateType`
  → `work/processes/{id}` (`System.Process Template` podaje tylko szablon bazowy); bugi mogą stać na backlogu wymagań
  (`bugsBehavior: asRequirements`, puste `bugWorkItems`) — o poziomie „bug” decyduje kategoria `Microsoft.BugCategory`. Tiberriver zwraca proces „Unknown” i **te same, zmyślone stany New/Active/Resolved/Closed dla
  każdego typu** — Claude dostaje błędne dane o przejściach. Baza metadanych z §F2 musi czytać stany z
  `wit/workitemtypes/{typ}/states` (działa bez uprawnień do procesu), a reguły procesu tylko wtedy, gdy PAT/konto pozwala.
- **Łańcuch do wdrożenia istnieje w ADO**: work item → link „Integrated in build” (dodawany automatycznie) → build
  (numer w stylu `RRRR.MM.DD.N`, gałąź `staging`) → klasyczny release pipeline (osobny na gałąź) z etapem `lcs`
  wysyłającym pakiet do LCS. Environments są puste. Wniosek: „czy jest na UAT” = czy istnieje udany etap `lcs`
  w release z buildem zawierającym zmianę. Mapowanie gałąź/release → środowisko LCS (np. `staging` → UAT, `master` → PROD)
  jest w konfiguracji połączenia/projektu, bo ADO tego nie wie. Potwierdzone dla firmy PO: `dev` → (DEV),
  `staging` → **UAT**, `master` → **PROD**. Klasyczne release'y są w osobnym API (`vsrm.dev.azure.com`)
  — Tiberriver ich w ogóle nie obsługuje.
- **Rozmiar odpowiedzi Tiberriver**: jeden work item ≈ 4 tys. tokenów (dziesiątki pustych pól, każda osoba jako blok
  z avatarami), lista repozytoriów ≈ 15 tys. tokenów. Cel adolens: ten sam work item ≈ 300–500 tokenów
  (puste pola pominięte, osoby jako „Imię Nazwisko”, pola WEF_ zamienione na „Kolumna tablicy: Done”).
- Wiki projektowe (typ 0, „Overview → Wiki”) — standardowe API wiki.
- Przepływ gałęzi Git w firmie PO: `dev → staging → master`, polityka wymaga work itemu w PR — więc PR jest
  wiarygodnym mostem work item ↔ kod.

## 2. Funkcjonalności (pogrupowane według pracy PO)

### A. Work itemy
- `ado_work_item` — odczyt jednego lub kilku (`123;456`): pola w języku procesu, opis i kryteria jako markdown,
  rodzic/dzieci, linki, załączniki, ostatnie komentarze. Parametr `include=history|comments|links|code`.
- `ado_create` — tworzenie z **szablonem typu** (User Story: „Jako… chcę… aby…” + kryteria Given/When/Then; Bug:
  kroki odtworzenia, oczekiwane/aktualne), walidacja pól wymaganych **przed** wysłaniem, rodzic, sprint, obszar, tagi.
- `ado_create_tree` — rozbicie wsadowe (Epic → Features → Stories → Tasks) z jednej listy; zawsze najpierw podgląd.
- `ado_update` — zmiana pól, przypisanie po imieniu („daj to Ani” → rozpoznanie tożsamości, przy niejednoznaczności
  pytanie), zmiana stanu tylko po dozwolonych przejściach (z podpowiedzią, czego brakuje, np. „Reason” przy zamknięciu).
- `ado_comment` — komentarz z prawdziwymi **@wzmiankami** (osoba dostaje powiadomienie) i odwołaniami `#123`.
- `ado_link` — rodzic/dziecko, powiązane, blokuje/blokowany, duplikat.
- `ado_history` — kto, co i kiedy zmienił (diff rewizji w ludzkim języku), „dlaczego to się zmieniło”.
- `ado_undo` — cofa ostatnią zmianę wykonaną przez adolens (z lokalnego dziennika), po podglądzie.
- Załączniki: dodanie pliku/obrazu (np. zrzut ekranu z czatu), odczyt.
- Brak usuwania work itemów (co najwyżej stan Removed). Usuwanie (np. gałęzi po scaleniu) tylko w zestawach `code`/`pr`.

### B. Tablica, backlog, sprinty
- `ado_sprint` — bieżący/następny sprint zespołu: cel, daty, elementy w podziale na stany, pojemność vs. szacunki,
  co zagrożone (bez ruchu od N dni, zablokowane, bez szacunku, bez kryteriów akceptacji).
- `ado_board` — kolumny tablicy (z podziałem Doing/Done i limitami WIP) i karty; przesunięcie karty do kolumny.
- `ado_backlog` — backlog zespołu w kolejności priorytetu; zmiana kolejności („przenieś #123 nad #456”), przypisanie do sprintu.
- `ado_query` — zapytanie w języku naturalnym → WIQL (z mapowaniem nazw pól procesu), uruchamianie zapisanych
  zapytań („Moje zapytania”, „Shared Queries”), wyniki jako tabela.
- `ado_my_day` — co nowego od wczoraj: moje elementy zmienione przez innych, gdzie mnie oznaczono (`@RecentMentions`),
  PR-y i elementy czekające na moją decyzję.
- `ado_health` — „higiena backlogu”: historie bez kryteriów, bez szacunku, osierocone, przeterminowane, duplikaty (podobne tytuły).

### C. Wiki i dokumentacja
- `ado_wiki` — drzewo stron, odczyt, wyszukiwanie (wiki projektowe i „code wiki”).
- `ado_wiki_write` — utworzenie/edycja strony z kontrolą wersji (eTag — nie nadpisze cudzej równoległej zmiany),
  obrazy jako załączniki wiki, Mermaid.
- Generowanie dokumentów z danych: notatki wydania (elementy zamknięte w sprincie/między datami/w buildzie),
  opis funkcjonalności z historii work itemów, raport statusu dla interesariuszy — zapis jako strona wiki lub plik.

### D. Weryfikacja „czy programista zrobił to, o co prosiłem” (wyróżnik)
- `ado_trace` — dla work itemu zbiera w jednej odpowiedzi:
  1. czego chciano: opis, kryteria akceptacji, komentarze z ustaleniami, zmiany kryteriów po rozpoczęciu prac (!),
  2. co zrobiono: powiązane changesety TFVC, commity i PR-y Git (także przez dzieci — Taski), lista zmienionych plików,
  3. gdzie to jest: czy zmiana jest scalona do gałęzi Main/Release, w którym buildzie (pakiet wdrożeniowy), czy
     wdrożona na UAT/Prod. Wdrożenia D365 idą przez LCS, więc ADO widzi je tylko wtedy, gdy release pipeline /
     environment wysyła pakiet do LCS (zadania „LCS Asset Upload / Deploy”). Gdy wdrożenie jest ręczne, adolens podaje
     ostatni build z tą zmianą, a środowisko pozostaje „nieznane” (opcjonalnie: tag / pole w work itemie ustawiane przy wdrożeniu),
  4. testy: Test Case'y powiązane z historią i ich ostatnie wyniki.
- `ado_changes` — treść zmian (diff) z **redukcją szumu**: dla X++/AOT XML wycina zmienione metody i właściwości
  zamiast surowego XML-a (wiedza z xpp-lens); dla innych plików zwykły diff z limitem.
- Skill `po-verify` (patrz §4) prowadzi Claude'a: kryterium po kryterium → dowód w kodzie / brak dowodu / do sprawdzenia
  ręcznie, a na końcu proponuje komentarz w work itemie (po akceptacji PO).
- Sygnały ostrzegawcze: zmiana bez powiązanego WI, WI zamknięty bez żadnej zmiany w kodzie, kryteria zmienione po
  ostatnim commicie, zmiana nie dotarła do środowiska, na którym PO testuje.

### E. „Jak działa ta funkcjonalność?”
- `ado_search` — jedno wyszukiwanie po work itemach, wiki i kodzie (Code Search dla Git i TFVC), wyniki pogrupowane.
- `ado_file` — odczyt pliku z repozytorium (Git/TFVC, dowolna wersja), dla X++ ze streszczeniem struktury.
- `ado_file_history` — „dlaczego tak działa”: kto i w ramach jakich work itemów zmieniał dany obiekt/plik.
- Skill `po-explain` — tłumaczy kod na język biznesowy (bez nazw metod, chyba że PO poprosi), zawsze podaje źródła
  (WI, strona wiki, plik) i odróżnia „tak jest w kodzie” od „tak wynika z dokumentacji”.
- Gdy na maszynie jest xpp-lens (deweloper), skill korzysta z niego; PO bez maszyny deweloperskiej dostaje odpowiedź
  z repozytorium przez API.

### F. Testy akceptacyjne (opcjonalnie, faza 3)
- Generowanie Test Case'ów z kryteriów akceptacji (kroki + oczekiwane rezultaty), podpinanie pod historię i plan testów.
- Stan testów historii/sprintu.

### F2. Struktura work itemów (lokalna baza) i automatyzacje

**Lokalna baza metadanych** (SQLite w `%LOCALAPPDATA%\ado-lens\meta\<połączenie>.db`), osobno dla każdego
połączenia i projektu:
- typy work itemów, pola (nazwa wyświetlana ⇄ referencyjna, typ, wymagane, tylko do odczytu, domyślne),
  listy dozwolonych wartości (picklisty), stany z kategoriami i przejścia, reguły procesu (dziedziczony proces:
  API `work/processes/…/rules`), szablony work itemów zespołów, kolumny tablic, drzewa obszarów i iteracji, zespoły,
  osoby spotkane w projekcie (do rozpoznawania „Ania” → tożsamość).
- **Odświeżanie**: przy starcie, gdy starsze niż kilka godzin (w tle, nie blokuje); natychmiast, gdy ADO odrzuci zapis
  z powodu nieznanego pola/wartości (samonaprawa i ponowienie); ręcznie przez `ado_refresh`.
- **Historia struktury**: każde odświeżenie zapisuje różnice — `ado_fields changes` odpowiada na „jakie pola doszły /
  zmieniły się od zeszłego miesiąca”, a Claude nie używa nazw pól, które już nie istnieją.
- `ado_fields` — „jakie pola ma Bug w tym projekcie”, „jakie wartości przyjmuje Klient”, „które pola są wymagane przy
  Resolved” — odpowiedź z bazy, bez wywołań API.
Dlaczego baza, a nie odpytywanie za każdym razem: pól bywa kilkaset, a każde ich wypisanie w kontekście Claude'a
kosztuje tysiące tokenów; baza pozwala podać Claude'owi tylko to, czego dotyczy pytanie, i walidować zapis przed wysłaniem.

**Automatyzacje — co jest możliwe:**
| Poziom | Jak | Kiedy |
|---|---|---|
| zmiana wsadowa | `ado_bulk_update`: zapytanie (WIQL / zapisane / lista id) + zmiany pól (także wyliczane, np. „dopisz tag”, „przenieś do następnego sprintu”, „skopiuj pole A do B”) → podgląd różnic → zapis przez API batch | jednorazowo, „teraz” |
| **przepisy** (recipes) | nazwane, sparametryzowane automatyzacje w YAML: zapytanie + kroki (ustaw pole, zmień stan, komentarz, link, utwórz dzieci z szablonu). Claude tworzy je z opisu słownego (`ado_recipe_save`), uruchamia `ado_recipe_run` (zawsze z możliwością próby na sucho). Przechowywane lokalnie lub w `.adolens/recipes` w repo, żeby zespół je współdzielił | powtarzalne: „zamknij sprint”, „nowy klient → epik + 5 feature'ów”, „po zamknięciu historii ustaw Wersję” |
| harmonogram | przepisy działają też z CLI (`adolens recipe run nazwa`) — bez Claude'a, z Harmonogramu zadań Windows; albo zadania zaplanowane w Claude Desktop / Claude Code, gdy potrzebny osąd (np. codzienna higiena backlogu z komentarzem) | cyklicznie |
| szablony ADO | odczyt i stosowanie istniejących szablonów work itemów zespołu (API `wit/templates`), tworzenie nowych | spójne domyślne wartości dla całego zespołu |
| reguły procesu | odczyt reguł (np. „przy Closed ustaw X”), a przy uprawnieniach admina procesu — propozycja i dodanie reguły po stronie ADO; działa wtedy dla wszystkich, bez adolens | trwałe zachowanie po stronie serwera |
| reakcja na zdarzenia | service hooks / webhooki wymagają usługi działającej stale (Azure Function itp.) — poza zakresem v1 | — |

Podgląd przed zapisem przy zmianach wsadowych to zabezpieczenie przed pomyłką Claude'a, nie ograniczenie uprawnień:
da się go wyłączyć w ustawieniach (`confirmBulkAbove`).

### G. Narzędzia pomocnicze
- `ado_status` — wersja, zalogowane konto, organizacja/projekt/zespół, czy jest nowsza wersja, tryb i aktywne zestawy narzędzi, tryb tylko do odczytu.
- `ado_context` — ustawia domyślny projekt/zespół/obszar dla rozmowy („pracujemy na zespole Logistyka”).
- Zasoby MCP (resources): proces projektu (typy, stany, pola), lista zespołów i sprintów — Claude czyta raz, nie pyta w kółko.
- Prompty MCP (w Claude Code widoczne jako `/` polecenia, w Desktop przez „+”): „nowa historia”, „weryfikuj #…”,
  „podsumuj sprint”, „notatki wydania”, „co nowego”.

---

## 3. Architektura

```
adolens.exe  (.NET 9, self-contained, win-x64, jeden plik)
├─ Mcp/            host stdio (ModelContextProtocol C# SDK): tools, prompts, resources, instructions serwera
├─ Cli/            te same operacje z linii poleceń (testy, diagnoza, skrypty) + setup/login/register/update/doctor
├─ Ado/            klient REST API 7.1: HttpClient, ponawianie, 429/Retry-After, stronicowanie, batch
│   ├─ WorkItems   WIQL, batch get, JSON Patch, komentarze (wzmianki), rewizje, załączniki
│   ├─ Work        zespoły, sprinty, pojemność, tablice, kolumny, backlog
│   ├─ Wiki        strony, eTag, załączniki
│   ├─ Code        Git (commity, PR, diff) + TFVC (changesety, scalenia, pliki), Code Search
│   └─ Build/Test  buildy, wdrożenia (environments/releases), test plans
├─ Domain/         logika PO: szablony, walidacja przejść stanów, rozpoznawanie osób, trace, health, raporty
├─ Metadata/       cache procesu (typy, pola, stany, zespoły, sprinty, tożsamości) w %LOCALAPPDATA%\ado-lens\cache
│                  — SQLite lub JSON z TTL; odświeżany w tle, `ado_status` pokazuje wiek
├─ Format/         HTML ⇄ Markdown (pola opisów), tabele, przycinanie dużych odpowiedzi, liczenie tokenów
├─ Diff/           diff tekstowy + diff „semantyczny” dla AOT XML (metody/właściwości)
├─ Safety/         tryby i zestawy narzędzi, opcjonalny tryb tylko do odczytu, podgląd→potwierdzenie, dziennik zmian, undo
├─ Auth/           PAT: zmienna środowiskowa → Menedżer poświadczeń; miejsce na Entra ID (MSAL) w przyszłości
└─ Infra/          konfiguracja, rejestracja w Claude (kod z tfvc-explorer `ClaudeConfigs.cs`), Updater (z xpp-lens), dziennik użycia + `stats`
```

### Wydajność i oszczędność tokenów (wymaganie pierwszej klasy)

Każdy token w odpowiedzi narzędzia i w opisach narzędzi jest płacony w każdej rozmowie, więc:

**Opisy narzędzi (koszt stały każdej sesji)**
- tylko narzędzia aktywnych zestawów; opisy krótkie, jednozdaniowe, parametry z sensownymi domyślnymi
  (połączenie/projekt/zespół z kontekstu — Claude ich nie podaje);
- mniej, ale szerszych narzędzi (`ado_work_item` z `include=` zamiast pięciu osobnych); budżet: tryb PO ≤ ~3 tys.
  tokenów na wszystkie definicje — test w CI liczy i pilnuje tego budżetu.

**Odpowiedzi**
- markdown/tekst, nigdy surowy JSON; puste pola pominięte; osoba = „Imię Nazwisko”; daty skrócone; pola `WEF_*`
  i systemowe zamienione na znaczenie albo ukryte; HTML opisów → zwięzły markdown (bez stylów, obrazy jako link);
- **domyślnie zwięźle, szczegóły na żądanie**: `detail=summary|normal|full`, listy z limitem i informacją
  „pokazano 20 z 143 — `skip=20`”; diffy i logi przycięte do istotnych fragmentów z numerami linii;
- odpowiedzi wsadowe: kilka id w jednym wywołaniu (`123;456;789`) zamiast wielu wywołań;
- stały budżet na odpowiedź (np. 4 tys. tokenów), po przekroczeniu — podsumowanie + jak doczytać resztę;
- `adolens stats` (jak w xpp-lens): rozmiar odpowiedzi w tokenach per narzędzie, p95, puste wyniki — strojenie
  na danych z prawdziwego użycia; test w CI z budżetami dla typowych odpowiedzi (snapshoty).

**Szybkość**
- jeden proces na sesję, trwałe połączenie HTTP/2 z keep-alive, kompresja gzip/brotli, równoległe wywołania
  tam, gdzie API na to pozwala (np. work item + komentarze + linki naraz);
- API wsadowe ADO: `workitemsbatch` (do 200 id), `$batch` dla zapisu, WIQL zwraca id → jedno pobranie pól;
- prosimy API tylko o potrzebne pola (`fields=`), nie o `$expand=all`;
- metadane (typy, pola, stany, zespoły, sprinty, osoby, repozytoria) z lokalnej bazy SQLite — zero wywołań
  przy walidacji i tłumaczeniu nazw; odświeżanie w tle, warunkowe (ETag / `If-None-Match`, data zmiany);
- krótkotrwały cache odpowiedzi w pamięci procesu (np. 60 s) dla powtórzonych odczytów w jednej rozmowie;
- start procesu: dziś single-file + ReadyToRun (ok. 0,37 s do odpowiedzi na `initialize`, zmierzone w 0.1.0);
  Native AOT do sprawdzenia, gdy SDK MCP przestanie wymagać refleksji przy rejestracji narzędzi;
- obsługa limitów ADO: `Retry-After`, nagłówki `X-RateLimit-*`, wykładnicze ponawianie, bez zalewania API.

**Zasady odpowiedzi narzędzi** (najważniejsze dla jakości pracy Claude'a z PO):
- zwięzły markdown, nigdy surowy JSON; duże listy z licznikiem i „pokaż więcej”;
- nazwy pól i stanów w wersji wyświetlanej procesu (także polskie, jeśli proces jest po polsku);
- każda odpowiedź podaje link do elementu w przeglądarce (`https://dev.azure.com/…/_workitems/edit/123`), żeby PO mógł kliknąć;
- błędy po ludzku i z podpowiedzią („Stan Closed wymaga pola Reason; dozwolone: Completed, Cut”).

**Podgląd → potwierdzenie.** Operacje wsadowe, zmiany stanu na Closed/Removed, edycja wiki i zmiany >N elementów
najpierw zwracają podgląd i jednorazowy `confirmToken`; dopiero drugie wywołanie z tokenem zapisuje. Claude pokazuje
podgląd PO i czeka na „tak”. Pojedyncze, odwracalne zmiany (komentarz, pole) idą od razu i trafiają do dziennika.

### Uwierzytelnianie (decyzja: PAT)
- **PAT poza konfiguracją Claude'a.** Zapis i odczyt **najpierw w Menedżerze poświadczeń Windows** (cel
  `ado-lens:<połączenie>`; kod z tfvc-explorer `CredentialStore.cs`); dopiero gdy tam nic nie ma — zmienna
  środowiskowa `ADOLENS_PAT_<POŁĄCZENIE>`, potem `AZURE_DEVOPS_PAT` (CI, ręczna konfiguracja, maszyny bez profilu).
  Kreator zawsze zapisuje do Menedżera. `adolens status` pokazuje, skąd wziął PAT, i ostrzega, gdy w Menedżerze
  i w zmiennej są różne wartości. Wpis MCP w Claude zawiera tylko `adolens.exe mcp` — bez `env` z sekretem.
- Kreator prowadzi krok po kroku: link do strony tworzenia PAT, sprawdza PAT jednym wywołaniem, pokazuje, czego PAT nie
  obejmuje (np. brak Wiki Write) — informacyjnie, **narzędzie niczego nie blokuje, uprawnienia wyznacza PAT i konto**.
- Przypomnienie o wygasaniu PAT na kilka dni przed terminem (w `ado_status` i w pierwszej odpowiedzi sesji);
  `adolens login` podmienia PAT bez ponownej instalacji.
- `adolens doctor` przenosi PAT-y zapisane jawnie w konfiguracji Claude'a (np. obecny wpis Tiberriver) do Menedżera.
- Entra ID (logowanie przeglądarką) — później, jeśli admini zarejestrują aplikację; architektura `Auth/` to przewiduje.

### Połączenia: wiele organizacji, Git i TFVC
- **Połączenie** = organizacja + PAT + domyślny projekt/zespół, z nazwą (`contoso`, `fabrikam`). Każde ma swój PAT
  w Menedżerze. Narzędzia przyjmują opcjonalny `connection`; bez niego — domyślne albo z kontekstu projektu.
- **Git i TFVC jednocześnie**: Azure DevOps ma oba w tym samym REST API, a projekt może mieć jedno, drugie albo oba.
  adolens wykrywa to sam (lista repozytoriów Git + czy istnieje `$/Projekt`) i zapisuje w cache metadanych.
  Wszystko wyżej (trace, diff, historia pliku, wyszukiwanie) działa na wspólnym pojęciu **„zmiana kodu”**:
  | | Git | TFVC |
  |---|---|---|
  | link w work itemie | `vstfs:///Git/Commit/…`, `…/Git/PullRequestId/…` | `vstfs:///VersionControl/Changeset/…` |
  | jednostka zmiany | PR (z commitami) albo commit | changeset |
  | „czy dotarło do gałęzi X” | PR scalony do X / commit osiągalny z X | changeset w X z `mergeSources` wskazującym oryginał |
  | historia pliku | commity ścieżki | changesety ścieżki |
  | build | build z commitu / PR | build z changesetu (`sourceVersion = C123`) |
  Narzędzia tfvc-explorer wchodzą do adolens; tfvc-explorer można potem wycofać (instalator przejmie jego PAT).

### Konfiguracja na poziomie projektu — jedno narzędzie wszędzie
- Instalator rejestruje serwer w zasięgu **użytkownika**: w Claude Desktop (dostępny w każdej rozmowie i w każdym
  Projekcie — włącza się go w menu narzędzi rozmowy) i w Claude Code (`~/.claude.json`, dostępny w każdym folderze).
- **Claude Code**: opcjonalny `.adolens.json` w repozytorium (połączenie, projekt, zespół, obszar, gałęzie,
  środowiska) — serwer szuka go od katalogu roboczego w górę.
- **Projekty w Claude Desktop**: w instrukcjach projektu jedna linijka „adolens: połączenie firma-b, zespół Logistyka”;
  serwer instruuje Claude'a, żeby na początku rozmowy wywołał `ado_context` z tymi wartościami.

---

## 4. Skille

Paczka `ado-lens-skills` instalowana razem z serwerem:

| Skill | Co robi |
|---|---|
| `po-story` | pisanie historii: dopytuje o wartość biznesową, kryteria Given/When/Then, sprawdza INVEST, zakłada WI |
| `po-refine` | refinement: przegląd backlogu, wykrywanie braków, rozbicie dużych historii, pytania do zespołu |
| `po-verify` | weryfikacja wykonania (§2D) z raportem kryterium po kryterium i propozycją komentarza |
| `po-explain` | „jak działa X” (§2E), odpowiedź biznesowa ze źródłami |
| `po-sprint` | podsumowanie sprintu / przegląd / notatki wydania / raport dla interesariuszy |
| `po-wiki` | utrzymanie dokumentacji: aktualizacja strony funkcjonalności po zamknięciu historii |
| `dev-start` | start pracy nad work itemem: gałąź, stan, przypisanie, plan na podstawie kryteriów |
| `dev-pr` | przygotowanie PR (opis z kryteriów, podpięcie WI) i przegląd PR z kontekstem work itemu |
| `dev-build` | diagnoza czerwonego buildu / release'u i propozycja poprawki |
| `dev-why` | „dlaczego ten kod tak wygląda” — historia zmian i work itemów dla pliku / obiektu |

Skille instaluje się według trybu (PO dostaje `po-*`, deweloper `dev-*`, `full` — wszystkie).

**Zasady pisania treści do ADO** (opisy work itemów, kryteria, komentarze, opisy PR) — zwięźle, ale kompletnie:
po przeczytaniu od razu wiadomo, *co trzeba zrobić* albo *co zostało zrobione*. Obowiązują wszystkie skille i prompty,
a narzędzia zapisu je wspierają.

| Treść | Struktura | Limit (miękki) |
|---|---|---|
| Wymaganie (User Story / PBI / Requirement) | **Cel** (1–2 zdania: kto, co, po co) · **Zakres** (punkty; także „poza zakresem”, jeśli istotne) · **Kryteria akceptacji** (osobne pole; każde sprawdzalne, Given/When/Then albo jedno zdanie) | opis ≤ ~120 słów, ≤ 7 kryteriów — więcej = sygnał do podziału historii |
| Bug | **Co się dzieje** · **Co powinno** · **Kroki** (numerowane) · **Gdzie** (środowisko, firma, dane przykładowe) | ≤ ~100 słów + kroki |
| Task | jedno zdanie, co zrobić + ewentualnie obiekt/plik | ≤ ~40 słów |
| Komentarz | decyzja / pytanie / wynik na początku, kontekst po nim; @osoba, gdy czegoś od niej oczekujemy | ≤ ~60 słów |
| Opis PR | **Co i dlaczego** (1–2 zdania + `#WI`) · **Zmiany** (punkty po obszarach, nie po plikach) · **Jak sprawdzić** (kroki testu) · **Ryzyka / do uwagi** (tylko jeśli są) | ≤ ~150 słów; bez listy plików (widać ją w PR), bez kopiowania kryteriów (są w WI) |

**Zasady są przypięte do poziomu backlogu, nie do nazwy typu.** Każdy proces ma te same poziomy, tylko różnie
nazwane typy — ado-lens czyta przypisanie z konfiguracji backlogu projektu (`work/backlogconfiguration`, kategorie
`Microsoft.RequirementCategory`, `Microsoft.TaskCategory`, `Microsoft.BugCategory`, poziomy portfolio) i zapisuje
je w bazie metadanych:

| Poziom (rola) | Przykłady typów | Treść |
|---|---|---|
| portfolio | Epic, Feature, Initiative | cel biznesowy i zakres, bez szczegółów realizacji |
| **wymaganie** | User Story (Agile), Product Backlog Item (Scrum), **Requirement** (CMMI), własne typy | *co* zrobić i *po co* + kryteria akceptacji — szablon z tabeli wyżej |
| zadanie | Task (z polem Activity: Development / Testing / …) | konkretna praca jednej osoby; Task deweloperski: co zmienić; Task testera: co i jak sprawdzić (scenariusz, dane, oczekiwany wynik) |
| błąd | Bug, Issue, własne typy | szablon błędu |

Szczegóły dopasowują się do pól, które dany typ naprawdę ma: jeśli typ wymagania nie ma pola „Acceptance Criteria”
(np. część procesów CMMI lub własnych), kryteria trafiają jako sekcja w opisie; jeśli ma własne pola (np. „Requirement
Type”, pola `Custom.*` wymagane przy zapisie), szablon je uzupełnia lub dopytuje. Nadpisanie per typ jest możliwe
(`.adolens/templates/requirement.md`, `…/Requirement.md` dla konkretnego typu), ale zwykle niepotrzebne. Przy
rozbijaniu wymagania (`ado_create_tree`) Claude proponuje Taski według Activity — osobno dla dewelopera i testera.

Zasady wspólne: bez wstępów i powtórzeń, bez opisywania oczywistości, konkret zamiast ogólników („walidacja
limitu kredytowego przy zatwierdzaniu zamówienia”, nie „poprawki w walidacji”), szczegóły techniczne w Tasku lub
PR, a nie w historii dla biznesu. Przykład opisu PR:

```
Blokada zatwierdzenia zamówienia po przekroczeniu limitu kredytowego (#4821).

Zmiany
- Zamówienia sprzedaży: sprawdzenie limitu przy zatwierdzaniu, komunikat z kwotą przekroczenia
- Parametry: nowy przełącznik „Blokuj po przekroczeniu limitu” (domyślnie wyłączony)

Jak sprawdzić
1. Włącz przełącznik w Parametrach modułu sprzedaży
2. Utwórz zamówienie dla klienta US-001 powyżej limitu → zatwierdzenie zablokowane z komunikatem

Do uwagi: istniejące zatwierdzone zamówienia nie są sprawdzane wstecz.
```

Wsparcie w narzędziach: `ado_create` / `ado_update` / tworzenie PR przyjmują treść w markdownie i w odpowiedzi
zwracają krótką uwagę, gdy treść przekracza limit albo brakuje sekcji („opis 340 słów — zwykle ≤ 120; brak kryteriów
akceptacji”) — informacyjnie, zapis i tak się wykonuje. Struktury i limity są domyślne i nadpisywalne per firma/projekt
(`.adolens/templates/*.md` w repo albo w konfiguracji połączenia), żeby pasowały do szablonów, których zespół już używa.

**Styl odpowiedzi zależny od odbiorcy** — wspólna zasada wszystkich skilli, z poziomem szczegółowości, który użytkownik
może zmienić jednym zdaniem („więcej szczegółów”, „prościej”) albo na stałe w konfiguracji (`adolens config --style`):

| | Skille `po-*` (domyślnie **obrazowo**) | Skille `dev-*` (domyślnie **technicznie**) |
|---|---|---|
| język | biznesowy, bez żargonu; nazwy techniczne tylko w nawiasie lub na życzenie | techniczny: obiekty, metody, ścieżki, id commitów/changesetów |
| forma | najpierw krótka odpowiedź („Tak, zrobione — z jednym wyjątkiem”), potem obraz: lista kroków procesu „co się dzieje, gdy…”, tabela kryterium → ✅/⚠️/❌, prosty diagram (Mermaid) przepływu lub statusu, przykład z danymi („zamówienie na 120 szt. → …”) | zwięzłe fakty, diff, fragment kodu, linki do PR/buildu |
| status/wdrożenie | „Jest na UAT od wtorku, na produkcji jeszcze nie” | `PR !482 → staging @ build 2026.01.15.1, release lcs ✓; master ✗` |
| szczegółowość | domyślnie mało; **„pokaż szczegóły”** → poziom techniczny (pliki, metody, commity) | domyślnie pełna; **„wersja prosta”** → streszczenie biznesowe, np. do przekazania PO lub do komentarza w work itemie |
| źródła | zawsze, ale jako klikalne linki „zobacz work item / stronę wiki” | id + link |

Skille dev mają polecenie „przetłumacz dla PO” — gotowy, prosty komentarz do work itemu lub wiadomość, a skille PO
„przekaż deweloperowi” — precyzyjne pytanie/zgłoszenie z kryteriami i krokami odtworzenia. Narzędzia MCP zwracają te
same dane w obu trybach (`detail=`); różnicę w prezentacji robią skille, więc nie płacimy tokenów za dwie wersje.

Dystrybucja: Claude Code — kopiowanie do `%USERPROFILE%\.claude\skills\` przez instalator (albo jako plugin Claude Code
zawierający i serwer, i skille). Claude Desktop — skille wgrywa się w Ustawieniach (Capabilities → Skills) jako ZIP;
instalator przygotowuje gotowe ZIP-y i otwiera folder z instrukcją; w organizacji z planem Team/Enterprise admin może je
udostępnić wszystkim centralnie. Uzupełniająco te same scenariusze jako **prompty MCP** — działają bez instalowania skilli.

---

## 5. Instalacja, aktualizacja, deinstalacja (dla osoby nietechnicznej)

**Instalator `ado-lens-setup-X.Y.Z.exe`** (Inno Setup, instalacja dla bieżącego użytkownika, **bez admina**,
do `%LOCALAPPDATA%\Programs\ado-lens`):
1. Ekran powitalny + sprawdzenie, czy jest Claude Desktop i/lub Claude Code.
2. Adres organizacji (wklej link z przeglądarki — kreator sam wyciągnie nazwę org i projektu).
3. PAT (link do strony tworzenia, podpowiedziane zakresy, pole z ukrytym wpisywaniem) → test połączenia → zapis
   w Menedżerze poświadczeń. Można dodać kolejne połączenie (inna organizacja).
4. Wybór domyślnego projektu i zespołu z listy (pobranej z ADO).
5. Tryb pracy: PO / dev / full (z krótkim opisem, co każdy daje); można zmienić później.
6. Rejestracja w Claude Desktop i Claude Code + skille. Jeśli Claude Desktop działa: „zamknąć i uruchomić ponownie?”
   (Desktop nadpisuje swoją konfigurację, gdy działa — znane z tfvc-explorer).
7. Koniec: „Otwórz Claude i napisz: *pokaż mój bieżący sprint*”.

Pod spodem kreator woła `adolens setup …`, więc to samo da się zrobić z PowerShella (`install.ps1` zostaje dla
deweloperów i CI, jak w xpp-lens).

**Aktualizacja**: serwer raz dziennie sprawdza GitHub Releases; gdy jest nowa wersja, `ado_status` i pierwsza odpowiedź
w sesji mówią „dostępna wersja X — powiedz *zaktualizuj adolens*”. Narzędzie `ado_update` pobiera instalator,
sprawdza SHA-256 (i podpis), uruchamia go w trybie cichym; działający `adolens.exe` jest przemianowywany na `*.old`
(trik z xpp-lens), więc otwarte sesje działają dalej. Ustawienia, logowanie i cache zostają.

**Deinstalacja**: standardowo z „Aplikacje i funkcje” Windows — usuwa wpisy MCP, skille, opcjonalnie dane logowania i cache.

**Podpis kodu (decyzja: za darmo)**: niepodpisany `.exe` pobrany przeglądarką dostaje ostrzeżenie SmartScreen,
które przestraszy PO. Azure Artifact Signing odpada (JDG). Plan:
1. **SignPath Foundation** (darmowy certyfikat dla open source): warunki — licencja OSI (MIT) bez komercyjnego
   dual-licensingu, brak własnościowych komponentów, aktywne utrzymanie, publiczne wydania już istnieją, opis
   funkcji na stronie pobierania, **polityka podpisu kodu** na stronie projektu, build w całości na runnerach
   hostowanych przez GitHub (workflow wydań już tak działa), podpis przez akcję `signpath/github-action-submit-signing-request`,
   każde wydanie zatwierdzane ręcznie w SignPath. Wydawcą w podpisie jest „SignPath Foundation”, nie WillowDev.
   Wniosek składamy po pierwszych publicznych (niepodpisanych) wydaniach.
2. Do tego czasu i niezależnie od podpisu — **instalacja bez przeglądarki**: jedno polecenie PowerShella (kopiuj →
   wklej) pobiera instalator przez `Invoke-WebRequest`, sprawdza SHA-256 i uruchamia go; plik pobrany w ten sposób nie
   dostaje znacznika „z internetu”, więc SmartScreen nie pokazuje okna. Aktualizacje (`ado_update`) działają tak samo.
3. Opcjonalnie później: pakiet w **winget** (`winget install WillowDev.ADOLens`) — darmowe, znane w firmach.
4. Gdyby SignPath odmówił: **Certum Open Source Code Signing** (polski wystawca, ok. 50 € rocznie, na imię
   i nazwisko) — tanio, ale nie za darmo.

**Alternatywa dla samego Claude Desktop**: paczka rozszerzenia Desktop (`.mcpb`) — instalacja dwuklikiem w Claude,
pola konfiguracji w UI, sekrety w magazynie systemu. Nie obejmuje Claude Code ani skilli, więc jako dodatek, nie główna droga.

---

## 6. Bezpieczeństwo i prywatność

- Sekrety tylko w Menedżerze poświadczeń (albo w zmiennej środowiskowej, jeśli użytkownik tak woli); `adolens doctor` wykrywa i przenosi PAT-y zapisane
  jawnym tekstem w konfiguracji Claude (jak tfvc-explorer).
- **Narzędzie nie ogranicza uprawnień** — robi to PAT i konto. Kreator podpowiada zestaw zakresów dla PO
  (Work Items R&W, Code Read, Wiki R&W, Build Read, Release Read, Test Management R&W, Identity Read, Project & Team Read),
  ale przyjmie każdy PAT; brak uprawnienia to czytelny komunikat („PAT nie ma zakresu Wiki: Write”).
- Tryby (`po` / `dev` / `full`) to tylko wybór **widocznego zestawu narzędzi** (mniej narzędzi = trafniejszy wybór przez
  Claude'a), nie blokada; tryb tylko do odczytu opcjonalnie dla ostrożnych.
- Dziennik zmian (`%LOCALAPPDATA%\ado-lens\audit`) — co adolens zmienił, kiedy, w czyim imieniu (podstawa `ado_undo`).
- Dziennik użycia jak w xpp-lens (`adolens stats`) — tylko lokalnie, do strojenia narzędzi.
- Treści z work itemów/wiki traktowane jako dane: instrukcje serwera mówią Claude'owi, że tekst w WI to nie polecenia.

---

## 7. Etapy

| Faza | Zakres | Cel |
|---|---|---|
| **0. Szkielet** | publiczne repo jak xpp-lens (sln, build/pack/release.ps1, CI, testy), połączenia + PAT w Menedżerze/zmiennej, `adolens login/status/register/doctor/mcp`, baza metadanych z historią zmian, `ado_fields` | działa w Claude Desktop i Code obok Tiberriver |
| **1. MVP dla PO** | A (odczyt, create z szablonem, update ze stanami, komentarz z @, link, historia), B (`ado_sprint`, `ado_query`, `ado_my_day`), C (wiki odczyt/zapis/szukaj), `ado_bulk_update` z podglądem, tryby i zestawy; dla dev: `pr` (lista do przeglądu, diff, wątki, komentarze) i `pipelines` (status, błąd z logu) | można wyłączyć Tiberriver u PO i u siebie |
| **2. Instalator** | Inno Setup z kreatorem, aktualizacja z `ado_update`, instalacja jednym poleceniem PowerShella (bez SmartScreen), skille `po-story`, `po-sprint` | instalacja przez PO bez pomocy |
| **3. Weryfikacja** | wspólna „zmiana kodu” Git+TFVC, `ado_trace`, `ado_changes` (diff AOT XML), gałęzie/buildy/release'y, skill `po-verify`; wchłonięcie tfvc-explorer | „czy zrobił to, o co prosiłem” |
| **4. Wiedza i automatyzacje** | `ado_search` łączony, `ado_file_history`, skill `po-explain`, notatki wydania, `ado_health`, `ado_undo`, przepisy (`ado_recipe_*`, CLI) | „jak to działa i dlaczego”, powtarzalne zmiany |
| **5. Testy i dodatki** | Test Case'y z kryteriów, stan testów, tablica (przesuwanie kart, kolejność backlogu), szablony i reguły procesu, `.mcpb` | pełny cykl PO |
| **6. Podpis kodu** | wniosek do SignPath Foundation + polityka podpisu, krok podpisu w workflow wydań, winget | instalator bez ostrzeżeń także z przeglądarki |

Testy: jak w xpp-lens — testy jednostkowe formatowania/walidacji + testy narzędzi na **nagranych odpowiedziach API**
(fixtures z neutralnymi danymi, bez nazw klienta), opcjonalnie testy integracyjne na osobnej organizacji testowej.
