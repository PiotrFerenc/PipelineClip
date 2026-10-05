# PipelineClip

Konsolowy analizator nieudanych jobów `test` w pipeline GitLab: pobiera log, wybiera istotne fragmenty (BM25 + heurystyka), anonimizuje (`Anonymizer.Core`) i prosi LLM o podsumowanie błędu i sugestie. Specyfikacja: `opis.md`, plan: `PLAN.md`.

## Budowanie
Biblioteka anonimizująca jest w osobnym repo i referencjonowana ścieżką względną. Sklonuj oba repo obok siebie:

```
git clone https://github.com/PiotrFerenc/Anonimizator.git
git clone https://github.com/PiotrFerenc/PipelineClip.git
cd PipelineClip && dotnet build
dotnet test
```

## Szybki start
1. Skopiuj `PipelineClip.Cli/appsettings.Example.json` do `PipelineClip.Cli/appsettings.json` (plik jest w `.gitignore`).
2. Uzupełnij w nim `GitLab` (`BaseAddress`, `ProjectId`, `PipelineId`, `ApiKey`) oraz `TestStageAnalysis` (`BaseAddress`, `Model`, `ApiKey`), patrz niżej.
3. ID pipeline z błędem weź z URL w GitLabie (`.../-/pipelines/12345`, czyli `12345`) i wpisz w `GitLab:PipelineId`. Program pracuje wyłącznie na tym jednym pipeline. Uruchom:

```
cd PipelineClip.Cli
dotnet run
```

Bez `dotnet run`: `dotnet publish PipelineClip.Cli -c Release -o out`, potem `cd out && dotnet PipelineClip.Cli.dll`.

Uruchamiaj zawsze z katalogu, w którym leży `appsettings.json` (patrz Konfiguracja). Z `dotnet run -- 1234` ID `1234` nadpisuje `GitLab:PipelineId`; `--project grupa/projekt` nadpisuje `GitLab:ProjectId`.

## Jak to działa
1. Start: wczytanie i walidacja configu (pusty klucz lub adres = kod 1, zanim poleci żądanie HTTP).
2. Pobranie nieudanych jobów wskazanego pipeline z GitLaba. Joby z `allow_failure` i spoza stage `test` są pomijane.
3. Dla każdego nieudanego joba `test`, równolegle (domyślnie 4 naraz): pobranie końca logu (2 MB), czyszczenie (ANSI, `\r`, znaczniki sekcji CI), podział na okna po 40 linii z zakładką, wybór 40 chunków przez BM25, reranking heurystyczny do 20, anonimizacja, wysłanie do LLM.
4. Zebranie analiz w jeden raport (kolejność po ID joba). Błąd jednego joba nie przerywa reszty. Opcjonalnie drugie wywołanie LLM ze wspólnym podsumowaniem (`PipelineMerge`).
5. Raport na stdout.

Do LLM trafia tylko zanonimizowany wycinek logu, nie cały log.

## Wydania
Workflow `.github/workflows/package.yml` pakuje pliki z gita do `PipelineClip.zip` bez kompilacji (bez `bin/`, `obj/` i `appsettings.json`). Po pushu na `main` zip jest artefaktem workflow, a po wypchnięciu tagu `v*` trafia do GitHub Release (`git tag v0.1.2 && git push origin v0.1.2`). Zip nie zawiera repo `Anonimizator`, które trzeba mieć obok, żeby zbudować projekt.

## Konfiguracja
`appsettings.json` jest czytany z **katalogu roboczego**, z którego uruchamiasz program. `dotnet run` z katalogu `PipelineClip.Cli` i uruchomienie opublikowanej binarki z jej katalogu działają. Uruchomienie z innego katalogu (np. `dotnet run --project PipelineClip.Cli` z korzenia repo) nie znajdzie pliku.

Każdą wartość można nadpisać zmienną środowiskową, bez prefiksu, z `__` jako separatorem sekcji, np. `GitLab__ApiKey`, `TestStageAnalysis__ApiKey`. Przydatne w CI, żeby nie trzymać tokenów w pliku.

### GitLab
- `BaseAddress`: adres API, z `/api/v4/` na końcu i ukośnikiem na końcu, np. `https://gitlab.example.com/api/v4/`.
- `PipelineId`: ID pipeline z błędem (liczba > 0). Argument CLI nadpisuje wartość z configu. Brak = kod wyjścia 2.
- `ProjectId`: API GitLab wymaga projektu obok ID pipeline. Może to być ID liczbowe albo ścieżka `grupa/projekt` (w podgrupie `grupa/podgrupa/projekt`). Nadpisywane argumentem `--project`. Skąd wziąć ID: na stronie projektu menu ⋮ → **Copy project ID** (w starszych wersjach Settings → General), albo `curl -H "PRIVATE-TOKEN: <token>" "https://gitlab.example.com/api/v4/projects/grupa%2Fprojekt"` i pole `id`. Ścieżki z URL też działają, więc ID nie trzeba szukać.
- `ApiKey`: sam token (np. `glpat-...`), bez `nazwa_usera:` i bez base64. Trafia do nagłówka `PRIVATE-TOKEN` przez `Headers` (podmiana `{ApiKey}`). Base64 `user:token` to Basic auth (git po HTTPS, container registry), nie dotyczy REST API.

#### Uprawnienia tokena GitLab
- Scope **`read_api`** wystarcza. Program tylko czyta listę jobów pipeline i log joba (`/trace`). Nie dawaj `api` (pełny zapis).
- **Project access token** (Settings → Access Tokens) ogranicza dostęp do jednego projektu i jest bezpieczniejszy niż personal access token. Rola minimum **Reporter**; zależnie od ustawień projektu log joba może wymagać **Developer**.
- Błąd 401/403 (`Token odrzucony lub bez uprawnień read_api`) oznacza zwykle za mały scope albo za niską rolę.
- Nie commituj tokena: `appsettings.json` jest w `.gitignore`, a w CI użyj zmiennej `GitLab__ApiKey`.
- `IncludeAllowedFailures` (domyślnie `false`): joby nieudane, ale z `allow_failure: true`, są domyślnie pomijane.

### TestStageAnalysis (LLM)
`BaseAddress`, `Model`, `ApiKey`, `SystemPrompt`, `Temperature`, `MaxTokens`, `TimeoutSeconds`, `Headers`. Format API: zgodny z `POST {BaseAddress}chat/completions`. **To założenie nie zostało jeszcze sprawdzone na prawdziwym dostawcy.** Jeśli format jest inny, trzeba poprawić tylko klasę `TestStageSummarizer`. Autoryzacja wyłącznie przez `Headers` (np. `"Authorization": "Bearer {ApiKey}"` albo nagłówek dostawcy).

### Analysis
Parametry doboru fragmentów logu i równoległości (wartości domyślne w `appsettings.Example.json`):
- `MaxLogBytes`: ile bajtów końca logu pobrać (2 MB).
- `ChunkLines`, `ChunkOverlapLines`: rozmiar okna i zakładka w liniach (40 i 10).
- `RetrieverCandidates`: ile chunków wybiera BM25 (40).
- `MaxChunks`: ile chunków trafia do LLM po rerankingu (20). Razem z `ChunkLines` ogranicza rozmiar zapytania.
- `MaxParallelJobs`: ile jobów analizowanych równolegle (4).
- `TargetStage`: stage do analizy (`test`).

### PipelineMerge (opcjonalne)
Sekcja `PipelineMerge`, domyślnie `Enabled: false`. Przy co najmniej 2 udanych analizach drugie wywołanie LLM dostaje wyłącznie zanonimizowane podsumowania per job i zwraca jedno podsumowanie wspólne. Ma własny `BaseAddress`/`Model`/`ApiKey`, niezależny od `TestStageAnalysis`. Błąd scalania nie przerywa raportu.

### Walidacja przy starcie
Pusty `ApiKey`, `BaseAddress`, `ProjectId` (GitLab) albo `Model` kończy program kodem 1 z komunikatem wskazującym sekcję, zanim zostanie wysłane jakiekolwiek żądanie HTTP.

## Użycie
```
PipelineClip.Cli [pipelineId] [--project <id>]
```

Przykłady (z katalogu `PipelineClip.Cli`):

```
dotnet run                               # projekt i pipeline z configu
dotnet run -- 12345                      # inny pipeline, projekt z configu
dotnet run -- 12345 --project grupa/app  # pipeline i projekt z argumentów
```

Wynik trafia na stdout, błędy i użycie na stderr, więc `dotnet run > raport.txt` zapisuje sam raport. Przykładowy raport:

```
Pipeline #12345: 3 nieudane joby, 2 przeanalizowane, 1 pominięty (stage bez strategii)

── unit-tests-a (job 777) ───────────────
<podsumowanie LLM: błąd, prawdopodobna przyczyna, sugestie>
(fragmentów: 20, zanonimizowanych elementów: 5)

── unit-tests-b (job 778) ───────────────
BŁĄD ANALIZY: <komunikat>
```

Gdy pipeline nie ma nieudanych jobów `test`, program wypisuje `Brak nieudanych jobów test` i kończy się kodem 0. Błąd analizy jednego joba nie przerywa pozostałych.

Kody wyjścia: 0 ok (także brak jobów do analizy), 1 błąd (konfiguracja, GitLab, albo wszystkie analizy zakończone błędem), 2 złe argumenty albo brak ID pipeline, 130 Ctrl+C.

## Ograniczenia
- Anonimizacja obejmuje ścieżki plików, e-maile, IPv4 i sekrety przy kluczu (`password=`, `token=`, `Authorization:`, `glpat-`). Sekret luzem bez klucza obok nie jest wykrywany (PLAN.md, sekcja 9). Nie używaj na logach z wrażliwymi danymi bez przeglądu.
- Nazwa joba (`Job: {name}`) trafia do LLM bez anonimizacji.
- Tylko stage `test`, bez jobów bridge/child pipeline.
- Heurystyki rerankera są pisane pod `dotnet test`/xunit i sprawdzone tylko na syntetycznym logu. Inne frameworki mogą wymagać dopisania wzorców w `HeuristicReranker`.
- Nie wykonano jeszcze testu na prawdziwym GitLabie i LLM.
