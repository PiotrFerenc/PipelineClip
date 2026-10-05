# PLAN — PipelineClip (analizator nieudanych jobów test z GitLab)

> **Status (2026-10-01):** fazy 0–8 zaimplementowane, faza 9 częściowo (scalanie przez LLM zrobione, de-anonimizacja i strategie dla innych stage'y celowo pominięte). `dotnet build` bez błędów, `dotnet test`: 78 testów w PipelineClip, 73 w Anonimizator. **Nie wykonano** smoke testu na prawdziwym GitLabie ani LLM, więc format API LLM (`chat/completions`) to nadal założenie. Lista odstępstw od planu: sekcja 9.

Specyfikacja źródłowa: `opis.md`. Ten plik opisuje jak to zbudować, w jakiej kolejności i po czym poznać, że etap jest skończony.

## 0. Założenia i luki wykryte przy planowaniu

Rzeczy, których `opis.md` nie rozstrzyga. Plan przyjmuje domyślne wartości, każdą można zmienić bez przebudowy architektury.

| # | Luka | Przyjęte w planie | Skutek zmiany |
|---|------|-------------------|---------------|
| 1 | API GitLab wymaga **ID projektu**, a wejściem jest tylko ID pipeline (`/projects/:id/pipelines/:pipeline_id/jobs`) | `GitLab:ProjectId` w `appsettings.json`, opcjonalnie nadpisywane argumentem `--project` | Brak ID projektu = błąd konfiguracji z jasnym komunikatem |
| 2 | Format żądania/odpowiedzi „własnego” dostawcy LLM nie jest podany | Zakładamy JSON zgodny z `POST {BaseAddress}/chat/completions` (messages, `choices[0].message.content`). Całość w jednej klasie `TestStageSummarizer`, skonfigurowanej wg skilla `dotnet-llm-http-config` (sekcja `TestStageAnalysis`, named HttpClient `TestStageAnalysis`) | Inny format = podmiana jednej klasy, interfejs `ITestStageSummarizer` bez zmian |
| 3 | `Anonymizer.Core` anonimizuje **tylko ścieżki plików** (Linux/Windows). Nie zna sekretów, tokenów, e-maili, adresów IP | **Zrobione (Faza 8):** `SecretAnonymizer` w bibliotece Anonimizator, wbudowany w `AddAnonymizer()` | Pokrycie niepełne, patrz sekcja 9 (sekret luzem bez klucza i `Bearer` poza nagłówkiem nie są wykrywane) |
| 4 | Fan-in: „łączone w jedno podsumowanie” nie mówi czy łączy człowiek-czytelny raport czy kolejne wywołanie LLM | Domyślnie fan-in deterministyczny (raport z sekcjami per job + nagłówek z liczbami). Drugie wywołanie LLM jako opcja (Faza 9) | **Zrobione (Faza 9):** merge przez LLM jest w `PipelineAnalyzer` (nie w agregatorze), domyślnie wyłączony (`PipelineMerge:Enabled`) |
| 5 | Joby bridge/child pipeline | Poza zakresem MVP. Analizowane są tylko joby z `stage == "test"` i `status == "failed"` bezpośrednio w pipeline | Dodanie wymaga endpointu `/bridges` |
| 6 | `allow_failure: true` | Job nieudany z `allow_failure` jest **pomijany** (nie blokuje pipeline) | Flaga `GitLab:IncludeAllowedFailures` (domyślnie `false`) |
| 7 | Bezpieczeństwo tokena | Token i klucze w `appsettings.json` (wymóg), zgodnie ze skillem: `appsettings.json` jest w `.gitignore`, w repo leży `appsettings.Example.json` z pustymi `ApiKey` | Brak, tylko higiena |

## 1. Zakres

W zakresie MVP:
- aplikacja konsolowa, wejście: ID pipeline
- stage `test`, strategia `TestStageAnalizator`
- pobranie nieudanych jobów i logów po API GitLab
- czyszczenie logu, chunkowanie, retriever (BM25) + reranker (heurystyka)
- anonimizacja przez `Anonymizer.Core`
- podsumowanie przez LLM (HttpClient), konfiguracja w `appsettings.json`
- fan-out po jobach (równolegle), fan-in do jednego raportu
- testy jednostkowe

Poza zakresem MVP: inne stage'e, bridge/child pipeline, embeddingi, UI, trwały cache, retry wielopoziomowy, strumieniowanie odpowiedzi LLM.

## 2. Architektura

### 2.1 Struktura solucji

Trzy projekty. Celowo bez rozbijania na Domain/Application/Infrastructure, bo to narzędzie jednego przypadku użycia. Podział na warstwy wymusza się katalogami i interfejsami na granicach I/O.

```
PipelineClip/
├── PipelineClip.slnx
├── opis.md
├── PLAN.md
├── .gitignore                       (appsettings.json, bin/, obj/)
├── PipelineClip.Cli/                (net10.0, Exe)
│   ├── Program.cs                   host, DI, parsowanie argumentów, kody wyjścia
│   ├── CliOptions.cs                pipelineId, projectId?
│   ├── ConsoleReportWriter.cs       render raportu do konsoli
│   ├── appsettings.json             (gitignored, prawdziwe sekrety)
│   └── appsettings.Example.json     (w repo, puste ApiKey)
├── PipelineClip.Core/               (net10.0, biblioteka)
│   ├── Abstractions/                interfejsy i modele współdzielone
│   ├── GitLab/                      klient API
│   ├── Strategies/                  IStageAnalizator + TestStageAnalizator + resolver
│   ├── Logs/                        czyszczenie, chunkowanie
│   ├── Retrieval/                   BM25 + reranker
│   ├── Options.cs                   HttpClientOptions, GitLabOptions, TestStageAnalysisOptions, AnalysisOptions
│   ├── HttpClientHeaders.cs         Apply(HttpClient, HttpClientOptions)
│   ├── Llm/                         TestStageSummarizer (ITestStageSummarizer)
│   ├── Anonymization/               adapter na Anonymizer.Core
│   ├── Orchestration/               PipelineAnalyzer (fan-out/fan-in), agregator
│   └── DependencyInjection.cs       AddPipelineClip(IConfiguration)
└── PipelineClip.Tests/              (xunit)
```

Referencje: `Cli -> Core`, `Tests -> Core`, `Core -> ../Anonimizator/Anonymizer.Core/Anonymizer.Core.csproj` (ProjectReference, ścieżka względna `..\..\Anonimizator\...`; sprawdzić wielkość liter, katalog nazywa się `Anonimizator`).

Zależności NuGet (minimum): `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Options.ConfigurationExtensions`. Testy: `xunit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`. Bez bibliotek mockujących: ręczne fake'i (mało interfejsów). Bez bibliotek BM25: ok. 40 linii własnego kodu.

### 2.2 Przepływ danych

```
CLI args ──► PipelineAnalyzer.AnalyzeAsync(projectId, pipelineId)
               │
               ├─ IGitLabClient.GetFailedJobsAsync            (lista jobów failed)
               │
               ├─ grupowanie po stage ─► IStageAnalizatorResolver (strategia per stage)
               │      stage != test ─► pominięty (liczony w raporcie jako "pominięte")
               │
               ├─ FAN-OUT: dla każdego joba test, równolegle (SemaphoreSlim)
               │      TestStageAnalizator.AnalyzeAsync(job)
               │         1. IGitLabClient.GetJobTraceAsync     (ostatnie MaxLogBytes)
               │         2. LogCleaner                         (ANSI, \r, sekcje CI)
               │         3. LogChunker                         (okna linii)
               │         4. IRetriever  (BM25, top Candidates)
               │         5. IReranker   (heurystyka, top MaxChunks)
               │         6. przywrócenie kolejności z logu
               │         7. IAnonymizationService.Anonymize
               │         8. ITestStageSummarizer.SummarizeAsync (chunki, prompt z opcji)
               │         9. JobAnalysis (sukces albo błąd per job)
               │
               └─ FAN-IN: IAnalysisAggregator.Aggregate(JobAnalysis[]) ─► PipelineReport
                                                                            │
CLI: ConsoleReportWriter.Write(report) ◄─────────────────────────────────────┘
```

### 2.3 Wzorce projektowe (gdzie i po co)

| Wzorzec | Miejsce | Powód |
|---------|---------|-------|
| Strategy | `IStageAnalizator` + `TestStageAnalizator` | Wymóg z `opis.md`: osobna strategia na stage |
| Registry/Resolver | `StageAnalizatorResolver` (słownik `Stage -> strategia` budowany z `IEnumerable<IStageAnalizator>`) | Nowy stage = nowa klasa + rejestracja DI, bez zmian w rdzeniu |
| Pipeline (kroki) | Kroki 1–9 w `TestStageAnalizator` jako wywołania interfejsów | Każdy krok testowalny osobno |
| Fan-out / Fan-in | `PipelineAnalyzer` + `IAnalysisAggregator` | Wymóg z rozmowy |
| Adapter | `AnonymizationAdapter` nad `IAnonymizationService` | Odcięcie od zewnętrznej biblioteki, łatwa podmiana |
| Options | `HttpClientOptions` (baza), `GitLabOptions`, `TestStageAnalysisOptions`, `AnalysisOptions` | Konfiguracja z `appsettings.json`, jedna sekcja = jedna klasa = jeden named HttpClient |
| Named HttpClient | `GitLab`, `TestStageAnalysis` | Skill `dotnet-llm-http-config`: osobny named HttpClient i osobne Options per call site, bez wspólnego interfejsu LLM |

### 2.4 Kontrakty (modele i interfejsy)

Wszystkie modele jako `sealed record`, w `Core/Abstractions`.

```csharp
public sealed record FailedJob(long Id, string Name, string Stage, string WebUrl, bool AllowFailure);

public sealed record LogChunk(int Index, int StartLine, int EndLine, string Text);

public sealed record ScoredChunk(LogChunk Chunk, double Score);

public sealed record JobAnalysis(
    FailedJob Job,
    bool Succeeded,
    string? Summary,        // odpowiedź LLM (null gdy Succeeded == false)
    string? Error,          // komunikat błędu przetwarzania tego joba
    int ChunksSent,
    int AnonymizedItems);

public sealed record PipelineReport(
    long PipelineId,
    int FailedJobsTotal,
    int AnalyzedJobs,
    int SkippedJobs,          // stage bez strategii
    IReadOnlyList<JobAnalysis> Analyses,
    string? MergedSummary = null,   // Faza 9: wspólne podsumowanie z LLM
    string? MergeError = null);     // Faza 9: błąd scalania (raport i tak powstaje)

public interface IGitLabClient
{
    Task<IReadOnlyList<FailedJob>> GetFailedJobsAsync(string projectId, long pipelineId, CancellationToken ct);
    Task<string> GetJobTraceAsync(string projectId, long jobId, CancellationToken ct);   // już obcięty do MaxLogBytes (ogon)
}

public interface IStageAnalizator
{
    string Stage { get; }                                   // "test"
    Task<JobAnalysis> AnalyzeAsync(string projectId, FailedJob job, CancellationToken ct);
}

public interface IStageAnalizatorResolver { IStageAnalizator? Resolve(string stage); }

public interface IRetriever  { IReadOnlyList<ScoredChunk> Retrieve(IReadOnlyList<LogChunk> chunks, int top); }
public interface IReranker   { IReadOnlyList<ScoredChunk> Rerank(IReadOnlyList<ScoredChunk> candidates, int top); }

public interface ITestStageSummarizer { Task<string> SummarizeAsync(string userContent, CancellationToken ct); }   // własny interfejs call site'u, bez wspólnego ILlmClient (skill)

public interface ITextAnonymizer { (string Text, int Items) Anonymize(string text); }   // nazwa ITextAnonymizer, bo Anonymizer.Core ma własne IAnonymizer

public interface IPipelineMergeSummarizer { Task<string> MergeAsync(IReadOnlyList<JobAnalysis> successful, CancellationToken ct); }   // Faza 9

public interface IAnalysisAggregator { PipelineReport Aggregate(long pipelineId, int failedTotal, int skipped, IReadOnlyList<JobAnalysis> analyses); }
```

Zasada: wyjątek w jednym jobie nie przerywa pozostałych. `TestStageAnalizator` łapie wyjątki I/O i zwraca `JobAnalysis(Succeeded: false, Error: ...)`. `OperationCanceledException` przechodzi dalej.

## 3. Konfiguracja

Wg skilla `dotnet-llm-http-config`: jedna sekcja top-level = jedna klasa Options = jeden named `HttpClient`. Nazwa sekcji = nazwa klasy bez `Options`. Klasy w `Core/Options.cs`:

```csharp
public class HttpClientOptions
{
    public string BaseAddress { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public string? ApiKey { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

public class GitLabOptions : HttpClientOptions
{
    public string ProjectId { get; set; } = "";
    public bool IncludeAllowedFailures { get; set; } = false;
}

public class TestStageAnalysisOptions : HttpClientOptions
{
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = "Jesteś asystentem CI. ..."; // domyślny literał, nadpisywalny z configu
    public double Temperature { get; set; } = 0.1;
    public int MaxTokens { get; set; } = 800;
}

public class AnalysisOptions   // nie dotyczy HTTP, poza wzorcem skilla
{
    public int MaxLogBytes { get; set; } = 2_097_152;
    public int ChunkLines { get; set; } = 40;
    public int ChunkOverlapLines { get; set; } = 10;
    public int RetrieverCandidates { get; set; } = 40;
    public int MaxChunks { get; set; } = 20;
    public int MaxParallelJobs { get; set; } = 4;
    public string TargetStage { get; set; } = "test";
}
```

`HttpClientHeaders.Apply(HttpClient, HttpClientOptions)` ustawia `BaseAddress`, `Timeout` i nagłówki, podmieniając `{ApiKey}` w wartościach (dokładnie wg skilla).

`appsettings.json` (gitignored) i `appsettings.Example.json` (w repo, puste `ApiKey`), te same klucze w tej samej kolejności:

```json
{
  "GitLab": {
    "BaseAddress": "https://gitlab.example.com/api/v4/",
    "ProjectId": "",
    "IncludeAllowedFailures": false,
    "TimeoutSeconds": 30,
    "ApiKey": "",
    "Headers": { "PRIVATE-TOKEN": "{ApiKey}" }
  },
  "TestStageAnalysis": {
    "BaseAddress": "https://llm.example.com/v1/",
    "Model": "model-name",
    "SystemPrompt": "Jesteś asystentem CI. Dostajesz fragmenty logu nieudanego joba testowego. Podaj: 1) krótkie podsumowanie błędu, 2) prawdopodobną przyczynę, 3) sugestie naprawy. Odpowiadaj po polsku, zwięźle. Nie zmyślaj, jeśli log nie wystarcza, napisz to.",
    "Temperature": 0.1,
    "MaxTokens": 800,
    "TimeoutSeconds": 120,
    "ApiKey": "",
    "Headers": { "Authorization": "Bearer {ApiKey}" }
  },
  "Analysis": {
    "MaxLogBytes": 2097152,
    "ChunkLines": 40,
    "ChunkOverlapLines": 10,
    "RetrieverCandidates": 40,
    "MaxChunks": 20,
    "MaxParallelJobs": 4,
    "TargetStage": "test"
  }
}
```

Rejestracja w `AddPipelineClip(IConfiguration)` (jedna para `Configure` + `AddHttpClient` na call site):

```csharp
services.Configure<GitLabOptions>(config.GetSection("GitLab"));
services.AddHttpClient("GitLab", (sp, client) =>
    HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<GitLabOptions>>().Value));

services.Configure<TestStageAnalysisOptions>(config.GetSection("TestStageAnalysis"));
services.AddHttpClient("TestStageAnalysis", (sp, client) =>
    HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<TestStageAnalysisOptions>>().Value));

services.Configure<AnalysisOptions>(config.GetSection("Analysis"));
```

Reguły:
- Skill ostrzega, że błędy bindowania są ciche (zostają domyślne). Dlatego dodatkowo: walidacja przy starcie (`BaseAddress` niepusty i poprawny URI, `ApiKey` niepusty, `ProjectId` niepusty dla GitLab, `Model` niepusty, zakresy w `AnalysisOptions`), `ValidateOnStart`. Brak klucza = natychmiastowy błąd z nazwą sekcji i klucza, bez żadnego żądania HTTP.
- Nazwa sekcji w `GetSection("...")` musi dokładnie (wielkość liter) zgadzać się z nazwą klasy bez `Options`. Test DI w Fazie 7 to pilnuje.
- Zmienne środowiskowe też nadpisują, bez prefiksu, z `__` jako separatorem (np. `GitLab__ApiKey`; domyślne zachowanie hosta, zero kodu).
- Kolejny call site (np. merge w Fazie 9) dostaje własną klasę Options, sekcję, named HttpClient i własny interfejs. Nie współdzieli niczego z `TestStageAnalysis`, nawet gdy wskazuje na ten sam serwer.
- Wartości limitów (2 MB, 20 chunków) z uzgodnionych wymagań.

## 4. Szczegóły komponentów

### 4.1 GitLabClient (`Core/GitLab`)

Named `HttpClient` `"GitLab"` z `GitLabOptions` (`BaseAddress` kończy się `/api/v4/`, nagłówek `PRIVATE-TOKEN: {ApiKey}` w `Headers`, timeout z opcji), klient przez `IHttpClientFactory.CreateClient("GitLab")`.

**Lista jobów:** `GET projects/{projectId}/pipelines/{pipelineId}/jobs?scope[]=failed&per_page=100&page=N`
- Paginacja po nagłówku `X-Next-Page` (pusty = koniec). Zabezpieczenie: maks. 20 stron.
- `projectId` przechodzi przez `Uri.EscapeDataString` (ID liczbowe albo ścieżka `grupa/projekt`).
- Mapowanie z JSON: `id`, `name`, `stage`, `web_url`, `allow_failure`. DTO prywatne w pliku klienta, `System.Text.Json` z `JsonPropertyName`.
- Filtr `allow_failure` według `IncludeAllowedFailures`.

**Log joba:** `GET projects/{projectId}/jobs/{jobId}/trace`
- Odpowiedź to `text/plain`. Obcięcie do ostatnich `MaxLogBytes`: preferowane żądanie z nagłówkiem `Range: bytes=-{MaxLogBytes}` (GitLab nie zawsze go respektuje), więc obowiązkowo także obcięcie po stronie klienta: czytać strumień (`ResponseHeadersRead`), trzymać bufor kołowy ostatnich N bajtów, dekodować UTF-8 dopiero na końcu. Pierwszą (potencjalnie uciętą w pół znaku/linii) linię po obcięciu odrzucić.
- Nigdy nie wczytywać całego logu do pamięci przed obcięciem.

**Błędy HTTP** mapowane na `GitLabException(statusCode, message)`:
- 401/403: „Token odrzucony lub bez uprawnień `read_api`”
- 404: „Nie znaleziono pipeline {id} w projekcie {projectId}”
- 429/5xx: jeden retry po `Retry-After` lub 2 s (prosta pętla w kliencie, bez Polly); drugi raz = błąd
- Token **nigdy** nie trafia do komunikatów błędów ani logów.

### 4.2 Czyszczenie i chunkowanie logu (`Core/Logs`)

`LogCleaner.Clean(string raw)`:
1. usunięcie sekwencji ANSI: regex `\x1B\[[0-9;?]*[ -/]*[@-~]` (generated regex, `[GeneratedRegex]`)
2. normalizacja końców linii: `\r\n` i samotny `\r` (GitLab używa `\r` do odświeżania linii postępu) na `\n`
3. usunięcie znaczników sekcji: linie z `section_start:` / `section_end:` (po usunięciu ANSI zostają jako `section_start:1234:name`)
4. zwinięcie 3+ kolejnych identycznych linii do `linia  [x N]`
5. `TrimEnd` każdej linii

`LogChunker.Chunk(string clean, int chunkLines, int overlapLines)`:
- okna przesuwne: krok = `chunkLines - overlapLines` (walidacja: overlap < chunkLines)
- `LogChunk(Index, StartLine, EndLine, Text)`, numery linii 1-based względem oczyszczonego logu
- ostatnie okno może być krótsze; log krótszy niż okno = jeden chunk; pusty log = pusta lista

### 4.3 Retriever BM25 (`Core/Retrieval`)

`Bm25Retriever : IRetriever`. Brak indeksu trwałego: korpus to chunki jednego logu, liczone w pamięci.

- Tokenizacja: lowercase, podział na `[^\p{L}\p{Nd}_]`, odrzucenie tokenów < 2 znaków. Identyfikatory typu `NullReferenceException` zostają jako jeden token (podkreślenie w klasie znaków, bez rozbijania camelCase w MVP).
- Parametry: `k1 = 1.2`, `b = 0.75`, IDF w wariancie `ln(1 + (N - n + 0.5)/(n + 0.5))` (zawsze nieujemne).
- **Zapytanie** (BM25 potrzebuje zapytania, a tu nie ma pytania użytkownika): stała lista terminów błędu, w kodzie jako `static readonly string[]`: `error, failed, failure, exception, assert, assertion, expected, actual, fail, fatal, panic, traceback, timeout, stack, at, exit`. Opcja rozszerzenia o nazwę joba (tokeny z `Job.Name`) odłożona.
- Zwraca `RetrieverCandidates` chunków o najwyższym wyniku, malejąco. Chunki z wynikiem 0 odrzucane (jeśli nic nie przejdzie, patrz niżej).
- **Fallback:** jeśli żaden chunk nie dopasował terminów, zwróć ostatnie `RetrieverCandidates` chunków (błąd zwykle jest na końcu logu). Zapisać w teście.

### 4.4 Reranker heurystyczny (`Core/Retrieval`)

`HeuristicReranker : IReranker`. Wynik końcowy = `bm25Normalized * 1.0 + bonusy`, gdzie `bm25Normalized = score / maxScore` (0..1).

| Sygnał | Bonus | Wykrywanie |
|--------|-------|-----------|
| Podsumowanie testów dotnet/xunit | +1.5 | regex `Failed!\s+-\s+Failed:\s+\d+` lub `Failed\s+\S+\s+\[\d+\s*m?s\]` |
| Asercja | +1.0 | `Assert\.`, `Expected:`, `Actual:`, `Assert.Equal() Failure` |
| Wyjątek | +1.0 | `\b\w+Exception\b` |
| Stack trace | +0.7 | linie `^\s+at\s+\S+.*in\s+.+:line\s+\d+` lub `^\s+at\s+` |
| Kod wyjścia / fatal | +0.5 | `exit code`, `ERROR: Job failed`, `fatal:` |
| Bliskość końca logu | 0..+0.8 | `0.8 * (EndLine / maxEndLine)` |
| Szum (restore, pobieranie obrazów) | -1.0 | `Restore(d)? complete`, `Pulling docker image`, `Downloading`, `Using docker image` |

Wagi jako stałe w klasie z komentarzem `ponytail:` o sufitach (heurystyki pod dotnet/xunit; inne frameworki wymagają dopisania wzorców). Zwraca `top = MaxChunks`, malejąco po wyniku.

Po rerankingu `TestStageAnalizator` **sortuje wybrane chunki rosnąco po `StartLine`**, żeby LLM dostał logiczny przebieg zdarzeń, a między niepołączonymi chunkami wstawia separator `[...]`. Nakładające się chunki (overlap) scalane przy składaniu tekstu, żeby nie wysyłać tych samych linii dwa razy.

### 4.5 Anonimizacja (`Core/Anonymization`)

- `AnonymizationAdapter` wywołuje `IAnonymizationService.Anonymize(text)` na **złożonym tekście wszystkich wybranych chunków jednym wywołaniem**. Dzięki temu ta sama ścieżka dostaje ten sam token w obrębie joba (mapa `ReplacementMap` jest per wywołanie).
- Zwraca `AnonymizedDocument` i liczbę `Anonymized.Count` (do raportu).
- Rejestracja: `services.AddAnonymizer()` w `AddPipelineClip`.
- Wątkowość sprawdzona: `AnonymizationService` jest bezpieczny wątkowo (mapa per wywołanie, pola tylko do odczytu, `Random.Shared`), potwierdza to test `Parallel.For` na 2000 wywołań. Adapter bez locka, singleton wystarcza.
- Mapowanie wstecz (de-anonimizacja odpowiedzi LLM) **nie** w MVP: podsumowanie zawiera tokeny zamiast ścieżek. Jeśli to przeszkadza, `AnonymizationResult.Anonymized` ma pary `Original -> Anonymized`, więc odwrócenie to jedna pętla `Replace` po stronie raportu (Faza 9).
- Sekrety: obsługuje je `SecretAnonymizer` (Faza 8, nazwa `secrets`) z tej samej biblioteki. Ograniczenia w sekcji 9.

### 4.6 Klient LLM: `TestStageSummarizer` (`Core/Llm`)

Wg skilla `dotnet-llm-http-config`, krok po kroku:
- `TestStageAnalysisOptions : HttpClientOptions` (`Model`, `SystemPrompt`, `Temperature`, `MaxTokens`), sekcja `"TestStageAnalysis"`, named HttpClient `"TestStageAnalysis"`. Prompt i model tylko z opcji, żaden literał w klasie serwisu poza domyślną wartością w Options.
- `TestStageSummarizer : ITestStageSummarizer` przyjmuje `IHttpClientFactory` i `IOptions<TestStageAnalysisOptions>`, bierze klienta przez `CreateClient("TestStageAnalysis")`.
- Autoryzacja wyłącznie przez `Headers` w configu (`"Authorization": "Bearer {ApiKey}"` albo inny nagłówek dostawcy), podmiana `{ApiKey}` robi `HttpClientHeaders.Apply`. Brak własnej logiki auth w klasie.
- Brak wspólnego interfejsu LLM: `ITestStageSummarizer` jest własny dla tego call site'u (skill, sekcja Boundaries).
- Żądanie (założenie z sekcji 0, punkt 2):
  ```json
  { "model": "...", "temperature": 0.1, "max_tokens": 800,
    "messages": [ {"role":"system","content":"<SystemPrompt>"},
                  {"role":"user","content":"<treść>"} ] }
  ```
- Odpowiedź: `choices[0].message.content`. Brak pola/pusty string = `LlmException("Pusta odpowiedź modelu")`. Ścieżka względna `chat/completions` (BaseAddress kończy się `/`).
- Treść user: nagłówek `Job: {name} (stage: {stage})`, potem chunki z separatorami `[...]`, wszystko już zanonimizowane. Nie wysyłać URL joba ani nazwy projektu.
- Błędy: 401/403 klucz, 429/5xx jeden retry (jak GitLab), reszta = `LlmException` z kodem statusu i skróconym (do 300 znaków) ciałem odpowiedzi. Klucz API nigdy w komunikatach.
- Wejście przekraczające limit kontekstu modelu: kontroluje to `MaxChunks * ChunkLines`. Domyślnie ok. 20 × 40 = 800 linii, rząd wielkości kilkudziesięciu tysięcy tokenów w najgorszym razie. Do zweryfikowania na realnym logu, w razie potrzeby zmniejszyć `ChunkLines`/`MaxChunks` w konfiguracji.

### 4.7 TestStageAnalizator (`Core/Strategies`)

`Stage => "test"` (porównanie `OrdinalIgnoreCase`; wartość czytana z `Analysis:TargetStage`, domyślnie `test`).

Kolejność kroków i obsługa błędów:
1. `trace = await gitlab.GetJobTraceAsync(...)`
2. `clean = LogCleaner.Clean(trace)`; pusty po czyszczeniu = `JobAnalysis(false, Error: "Pusty log joba")`
3. `chunks = LogChunker.Chunk(...)`
4. `candidates = retriever.Retrieve(chunks, RetrieverCandidates)`
5. `best = reranker.Rerank(candidates, MaxChunks)`
6. `text = ChunkComposer.Compose(best)` (sortowanie po linii, scalanie overlapu, separatory)
7. `anon = anonymizer.Anonymize(text)`
8. `summary = await summarizer.SummarizeAsync(userContent, ct)`
9. `return new JobAnalysis(job, true, summary, null, best.Count, anon.Count)`

Całość w `try/catch (Exception ex) when (ex is not OperationCanceledException)` zwracającym `JobAnalysis(false, Error: ex.Message)`.

`ChunkComposer` osobna statyczna klasa (czysta funkcja, łatwo testowalna).

### 4.8 PipelineAnalyzer, fan-out i fan-in (`Core/Orchestration`)

`PipelineAnalyzer.AnalyzeAsync(projectId, pipelineId, ct)`:
1. `jobs = await gitlab.GetFailedJobsAsync(...)`
2. `forTest = jobs z resolver.Resolve(job.Stage) != null`, reszta liczona jako `SkippedJobs`
3. **Fan-out:** `Task.WhenAll` nad zadaniami zawiniętymi w `SemaphoreSlim(MaxParallelJobs)`. Każde zadanie: `await sem.WaitAsync(ct)`, wywołanie strategii, `finally Release`.
4. **Fan-in:** `aggregator.Aggregate(...)`; kolejność analiz stabilna (po `Job.Id` rosnąco), niezależna od tego, który job skończył pierwszy.
5. Brak jobów do analizy: zwraca `PipelineReport` z `AnalyzedJobs = 0`.

`DefaultAnalysisAggregator`: buduje `PipelineReport` bez wywołań sieciowych (sortowanie, zliczanie).

### 4.9 CLI (`PipelineClip.Cli`)

Użycie: `pipelineclip <pipelineId> [--project <id>]`

- `Program.cs`: `Host.CreateApplicationBuilder(args)` `AddPipelineClip(configuration)`; parsowanie argumentów ręcznie (dwa argumenty, bez biblioteki). `pipelineId` musi być `long > 0`, w przeciwnym razie komunikat użycia i kod 2.
- Ctrl+C: `CancellationTokenSource` podpięty pod `Console.CancelKeyPress`, przekazywany w dół.
- `ConsoleReportWriter` (w CLI, nie w Core): format raportu:

```
Pipeline #12345: 3 nieudane joby, 2 przeanalizowane, 1 pominięty (stage bez strategii)

── unit-tests-a (job 777) ───────────────
<podsumowanie LLM>
(fragmentów: 20, zanonimizowanych elementów: 5)

── unit-tests-b (job 778) ───────────────
BŁĄD ANALIZY: <komunikat>
```
- Kody wyjścia: `0` = analiza zakończona (także gdy nie było czego analizować, wtedy komunikat „Brak nieudanych jobów test”), `1` = błąd (konfiguracja, GitLab niedostępny/odrzucony, wszystkie joby zakończyły się błędem analizy), `2` = złe argumenty, `130` = przerwane Ctrl+C.
- Wynik na stdout, diagnostyka i błędy na stderr. Brak logowania treści logów i żądań LLM (zawierają dane z pipeline).

## 5. Fazy implementacji

Każda faza kończy się zielonym `dotnet build` i `dotnet test`. Fazy po kolei, bez przeskakiwania.

### Faza 0: szkielet
- [x] `dotnet new sln -n PipelineClip --format slnx` (jeśli szablon nie wspiera, ręcznie `PipelineClip.slnx`)
- [x] projekty `Cli` (console), `Core` (classlib), `Tests` (xunit), `net10.0`, `Nullable` i `ImplicitUsings` włączone, `TreatWarningsAsErrors` dla `Core`
- [x] `ProjectReference` Core -> `Anonymizer.Core`; `Cli -> Core`; `Tests -> Core`
- [x] `.gitignore` (z `appsettings.json`), `appsettings.Example.json` (sekcja 3, puste `ApiKey`), kopia na `appsettings.json`, kopiowanie do output
- [x] `df -h /` przed pierwszym restore (dysk bywa pełny, patrz AGENTS.md)
- **Gotowe gdy:** `dotnet build` zielony, `dotnet run --project PipelineClip.Cli` wypisuje użycie i zwraca 2

### Faza 1: modele, interfejsy, opcje
- [x] rekordy i interfejsy z 2.4
- [x] klasy opcji z walidacją, `AddPipelineClip` rejestruje opcje z `ValidateOnStart`
- [x] test: pusty `GitLab:ApiKey` (i analogicznie `TestStageAnalysis:ApiKey`, `BaseAddress`) => `OptionsValidationException` z nazwą sekcji i klucza
- **Gotowe gdy:** aplikacja bez tokena kończy się kodem 1 z czytelnym komunikatem

### Faza 2: GitLabClient
- [x] `GitLabClient` (4.1), paginacja, mapowanie błędów, obcięcie logu do ogona
- [x] testy na `HttpMessageHandler` stub (bez sieci):
  - [x] paginacja: dwie strony, `X-Next-Page`
  - [x] filtr `allow_failure`
  - [x] 404/401 mapowane na właściwy komunikat, bez tokena w treści
  - [x] retry po 429, drugi 429 = wyjątek
  - [x] trace większy niż `MaxLogBytes` zwraca dokładnie ogon i nie zaczyna się od połowy linii
  - [x] `projectId` ze slashem jest zakodowany
- **Gotowe gdy:** testy zielone; ręczny smoke test na prawdziwym pipeline (opcjonalnie, na koszt użytkownika) wypisuje listę jobów

### Faza 3: log (czyszczenie, chunkowanie)
- [x] `LogCleaner`, `LogChunker`, `ChunkComposer`
- [x] testy: ANSI, `\r`, sekcje CI, zwijanie powtórzeń, okna z overlapem (liczba i zakresy linii), log krótszy od okna, pusty log, scalanie nakładających się chunków, separator `[...]` między rozłącznymi
- [x] fixture: `Tests/Fixtures/dotnet-test-failed.log` (realistyczny, zanonimizowany log `dotnet test` z jednym failem, restore na początku, podsumowanie na końcu)
- **Gotowe gdy:** testy zielone

### Faza 4: retriever i reranker
- [x] `Bm25Retriever`, `HeuristicReranker`
- [x] testy na fixture z fazy 3:
  - [x] chunk z asercją i stack trace jest w top `MaxChunks`, chunk z `Restore complete` nie
  - [x] chunk z podsumowaniem testów (koniec logu) jest w wyniku
  - [x] fallback: log bez żadnego terminu błędu zwraca ostatnie chunki
  - [x] deterministyczność: ten sam input = ta sama kolejność (stabilne sortowanie, tie-break po `Index`)
  - [x] `top` większe niż liczba chunków nie rzuca wyjątku
- **Gotowe gdy:** na fixture z 1 failem wybrane 20 chunków zawiera linię z nazwą testu, który padł

### Faza 5: klient LLM
- [x] checklista skilla `dotnet-llm-http-config`: `TestStageAnalysisOptions` w `Options.cs`, `Configure` + `AddHttpClient("TestStageAnalysis")`, serwis na `IOptions` i `CreateClient`, sekcja w obu plikach appsettings, `dotnet build`
- [x] `TestStageSummarizer` (bez własnej logiki auth, tylko `Headers` z configu)
- [x] testy na stub handlerze:
  - [x] poprawne ciało żądania (model, temperatura, system prompt z konfiguracji, treść user)
  - [x] nagłówek z `Headers` z podmienionym `{ApiKey}` (dla `Authorization: Bearer {ApiKey}` i dla niestandardowego nagłówka dostawcy)
  - [x] sekcja `TestStageAnalysis` realnie się zbindowała (test na `ConfigurationBuilder` z in-memory: `Model`, `SystemPrompt`, `Headers` różne od domyślnych)
  - [x] parsowanie `choices[0].message.content`
  - [x] pusta odpowiedź i zły JSON = `LlmException`
  - [x] klucz API nie występuje w żadnym komunikacie wyjątku
- **Gotowe gdy:** testy zielone; ręczny smoke test z prawdziwym endpointem potwierdza założenie z sekcji 0, punkt 2 (jeśli format inny: poprawić tylko `TestStageSummarizer`)

### Faza 6: strategia i orkiestracja
- [x] `AnonymizationAdapter`, sprawdzenie wątkowości `AnonymizationService` (4.5)
- [x] `TestStageAnalizator`, `StageAnalizatorResolver`, `PipelineAnalyzer`, `DefaultAnalysisAggregator`
- [x] testy z fake'ami `IGitLabClient` i `ITestStageSummarizer`:
  - [x] 3 nieudane joby test, wszystkie OK: 3 analizy, kolejność po `Job.Id`
  - [x] jeden job rzuca w LLM: pozostałe analizy nietknięte, ten ma `Succeeded=false`
  - [x] job innego stage'a (`build`): liczony w `SkippedJobs`, strategia nie wywołana
  - [x] brak nieudanych jobów: `AnalyzedJobs = 0`, brak wywołań LLM
  - [x] `MaxParallelJobs = 2` przy 6 jobach: fake mierzy maksymalną jednoczesność, nie przekracza 2
  - [x] anulowanie: `CancellationToken` anulowany w trakcie przerywa całość wyjątkiem anulowania
  - [x] do `ITestStageSummarizer` trafia tekst **po** anonimizacji (fake sprawdza, że ścieżka z logu została zastąpiona)
  - [x] resolver: duplikat `Stage` w rejestracji = wyjątek przy starcie
- **Gotowe gdy:** testy zielone

### Faza 7: CLI i spięcie DI
- [x] `Program.cs`, parsowanie argumentów, kody wyjścia, `ConsoleReportWriter`
- [x] test `ConsoleReportWriter` (do `StringWriter`): format dla sukcesu, błędu joba, braku jobów
- [x] test DI: `AddPipelineClip` z przykładową konfiguracją buduje `ServiceProvider` i `GetRequiredService<PipelineAnalyzer>()` działa (z `ValidateOnBuild`)
- [x] `README.md` (krótki: użycie, konfiguracja, uruchomienie, ostrzeżenie o sekretach)
- **Gotowe gdy:** scenariusz E2E z sekcji 6 przechodzi na fake'ach, a na prawdziwym GitLabie i LLM zwraca raport

### Faza 8 (opcjonalna, zalecana przed użyciem na realnych logach): anonimizacja sekretów
- [x] `SecretAnonymizer : IAnonymizer` (z biblioteki Anonimizator; w repo Anonimizator, bo tam jest kontrakt i `ReplacementMap`; alternatywnie w `PipelineClip.Core`, rejestrowany `services.AddAnonymizer<SecretAnonymizer>()`)
- [x] wzorce: nagłówki `Authorization: ...`, `Bearer <token>`, `glpat-...`, `password=`/`token=`/`secret=`/`apikey=` w URL i zmiennych, adresy e-mail, IPv4, connection stringi (`Password=...;`)
- [x] testy w bibliotece Anonimizator na tym samym wzorcu co istniejące anonimizatory
- **Gotowe gdy:** fixture z sekretami nie zawiera ich w tekście wysyłanym do LLM (test w `PipelineClip.Tests` z fake'iem `ITestStageSummarizer`)

### Faza 9 (opcjonalna): rozszerzenia fan-in
- [x] merge w `PipelineAnalyzer` zamiast `LlmMergeAggregator` (`PipelineMergeSummarizer`): drugie wywołanie LLM z listą podsumowań per job, własny interfejs `IPipelineMergeSummarizer`, własna klasa `PipelineMergeOptions`, sekcja `PipelineMerge` i named HttpClient `PipelineMerge` (bez współdzielenia z `TestStageAnalysis`, wg skilla)
- [ ] de-anonimizacja podsumowań w raporcie przy użyciu `AnonymizationResult.Anonymized` (pominięte: scalanie przez LLM dostałoby wtedy prawdziwe ścieżki, trzeba najpierw scalać, potem odwracać)
- [ ] strategie dla kolejnych stage'y (`BuildStageAnalizator`, itd.) jako nowe klasy `IStageAnalizator` (poza zakresem MVP z `opis.md`)

## 6. Scenariusz akceptacyjny E2E (na fake'ach)

Dane: pipeline 100 z jobami: `unit-a` (test, failed), `unit-b` (test, failed), `integration` (test, success), `compile` (build, failed). Log `unit-a` zawiera ścieżkę `/home/ci/builds/proj/src/Foo.cs`, log `unit-b` zawiera `Assert.Equal() Failure`.

Oczekiwane:
1. Lista z GitLab zwraca (po `scope[]=failed`) `unit-a`, `unit-b`, `compile`
2. `compile` pominięty (brak strategii dla `build`), `SkippedJobs = 1`
3. `unit-a` i `unit-b` analizowane równolegle; do LLM nie trafia `/home/ci/builds/proj`
4. Raport: 3 nieudane, 2 przeanalizowane, 1 pominięty; sekcje w kolejności `Job.Id`
5. Kod wyjścia 0
6. Gdy `unit-b` dostaje błąd LLM: raport ma sekcję „BŁĄD ANALIZY” dla `unit-b`, `unit-a` ma podsumowanie, kod wyjścia 0 (kod 1 tylko gdy **wszystkie** joby padły)

## 7. Ryzyka

| Ryzyko | Prawdopodobieństwo | Mitygacja |
|--------|-------------------|-----------|
| Do LLM trafiają sekrety z logu (anonimizator zna tylko ścieżki) | Wysokie | Faza 8 przed użyciem na realnych logach; do tego czasu README ostrzega |
| Format API „własnego” dostawcy inny niż zakładany | Średnie | Izolacja w `TestStageSummarizer`, smoke test w Fazie 5 |
| Heurystyki rerankera nietrafione dla frameworków innych niż dotnet/xunit | Średnie | Wagi i wzorce w jednym miejscu, test na fixture, dopisywanie wzorców |
| Log większy niż okno kontekstu modelu | Niskie-średnie | Limity `MaxChunks`/`ChunkLines` w konfiguracji |
| `AnonymizationService` nie jest wątkowo bezpieczny | Niskie | Sprawdzenie w Fazie 6, lock w adapterze |
| Brak `ProjectId` w wejściu | Pewne (wymóg API) | Konfiguracja + `--project`, jasny komunikat |
| Dysk pełny przy restore | Znane z workspace | `df -h /` przed Fazą 0 |
| Token w `appsettings.json` przypadkowo skomitowany | Średnie | `appsettings.json` w `.gitignore`, w repo tylko `appsettings.Example.json`, README |

## 8. Definicja ukończenia MVP

- [x] `dotnet build` bez ostrzeżeń w `Core`, `dotnet test` zielony
- [x] wszystkie testy z faz 1–7 istnieją i przechodzą
- [ ] `dotnet run --project PipelineClip.Cli -- <pipelineId>` na prawdziwym pipeline z nieudanym jobem `test` wypisuje podsumowanie i sugestie
- [ ] pipeline bez nieudanego joba `test` kończy się kodem 0 i komunikatem „Brak nieudanych jobów test”
- [x] brak sekretów (token GitLab, klucz LLM) w stdout, stderr i komunikatach wyjątków
- [x] README opisuje konfigurację i znane ograniczenia (sekcja 0)

## 9. Odstępstwa od planu i stan faktyczny (po implementacji)

Zbudowane równolegle: GitLab, log + retrieval, LLM + anonimizacja (jedna tura agentów), potem Faza 8 i Faza 9 (druga tura). Integrację (strategia, orkiestracja, DI, CLI, testy zbiorcze) zrobiono osobno.

**Struktura:** `PipelineClip.Tests` referencjonuje także `PipelineClip.Cli` (testy `CliArgs` i `ConsoleReportWriter`). Walidacja opcji idzie przez `.Validate(lambda)` + `ValidateOnStart()`, bez DataAnnotations (pakiet `Microsoft.Extensions.Options.DataAnnotations` usunięty z `Core.csproj`).

**Kontrakty:**
- `ITextAnonymizer` zamiast `IAnonymizer` (kolizja nazw z `Anonymizer.Core`).
- `PipelineReport` ma `MergedSummary` i `MergeError`.
- `TestStageAnalizator.Stage` zwraca `Analysis:TargetStage`.

**GitLabClient (Faza 2):**
- Konstruktor ma dodatkowy opcjonalny parametr `TimeSpan? defaultRetryDelay` (domyślnie 2 s), testy podają `TimeSpan.Zero`.
- Żądanie trace wysyła `Range: bytes=-N`; odpowiedź 206 traktowana jak obcięta, 416 daje pusty string.
- Po obcięciu pierwsza linia jest odrzucana zawsze (przy cięciu dokładnie na granicy linii ginie jedna pełna linia, bez znaczenia, bo to początek logu).
- 404 przy trace ma komunikat „Nie znaleziono zasobu w projekcie X” (bez numeru pipeline). Inne błędy HTTP, w tym 5xx po retry: `GitLabException(kod, "GitLab zwrócił błąd HTTP {kod}")`.

**Log i retrieval (Fazy 3–4):**
- `ChunkComposer.Compose(IEnumerable<ScoredChunk>)`, stała `Separator = "[...]"`, scala też chunki styczne, nie tylko nakładające się.
- `LogCleaner` usuwa jedną końcową pustą linię i zwija także identyczne puste linie.
- `LogChunker` rzuca `ArgumentOutOfRangeException` dla `overlap >= chunkLines`, `overlap < 0`, `chunkLines <= 0`; log mieszczący się dokładnie w oknie to jeden chunk.
- Fallback `Bm25Retriever`: ostatnie `top` chunków rosnąco po `Index`, wynik 0. Gdy `maxScore == 0`, o kolejności w rerankerze decydują bonusy i bliskość końca.
- Fixture `dotnet-test-failed.log` (303 linie) jest syntetyczny, nie pochodzi z prawdziwego pipeline.

**LLM (Faza 5):**
- `TestStageSummarizer` łapie też `ArgumentOutOfRangeException` (`choices: []`) i traktuje jak zły JSON.
- Dla `HttpRequestException` komunikat zawiera tylko nazwę typu wyjątku (ochrona przed wyciekiem klucza). Timeout i anulowanie nie są opakowywane w `LlmException`.

**CLI (Faza 7):** `builder.Logging.ClearProviders()`, bo host przy błędzie walidacji drukował stack trace, a logi hosta mogłyby zawierać dane z pipeline. Błędy raportuje sam CLI. Kody wyjścia: 0 także przy braku jobów, 1 gdy wszystkie analizy padły albo błąd konfiguracji/GitLab, 2 złe argumenty, 130 Ctrl+C.

**Faza 8 (`SecretAnonymizer`, `Name = "secrets"`):**
- Nazwa `secrets`, bo test w Anonimizator ma własny anonimizator `secret` i serwis rzuca na duplikacie.
- Wykrywa: wartość po `Authorization:` (Bearer/Basic/Token), `glpat-...` (min. 10 znaków), pary `password`/`passwd`/`token`/`secret`/`apikey`/`api_key`/`access_key` (dopasowanie po przyrostku klucza, łapie `GITLAB_TOKEN=` i `Password=...;`), e-maile, IPv4 (bez wersji typu `1.2.3`).
- Nie wykrywa: sekretu luzem bez klucza obok, `Bearer xyz` poza `Authorization:` (wymagałoby triggera na spacji i spowolniło skan).
- `user:pass@host.tld` w URL jest traktowane jak e-mail, więc `pass@host.tld` też znika.

**Faza 9 (merge):**
- Merge w `PipelineAnalyzer` po fan-in, tylko gdy `PipelineMerge:Enabled` (domyślnie `false`) i są co najmniej 2 udane analizy. `DefaultAnalysisAggregator` bez zmian.
- Własne `PipelineMergeOptions`, sekcja `PipelineMerge`, named HttpClient `PipelineMerge`, `PipelineMergeSummarizer` (kopia logiki HTTP, bez współdzielenia z `TestStageSummarizer`).
- Do LLM idą podsumowania opisane numerem („Podsumowanie 1:”), bez nazw jobów (nazwy nie przechodzą anonimizacji).
- Błąd merge nie przerywa raportu: `MergeError` dostaje komunikat `LlmException`, przy innym wyjątku tylko `Błąd scalania: <TypWyjątku>`. Anulowanie się propaguje.
- Named client `PipelineMerge` wywołuje `HttpClientHeaders.Apply` tylko przy `Enabled` (pusty `BaseAddress` rzuciłby `UriFormatException`). Walidacja opcji tylko przy `Enabled`.
- `ConsoleReportWriter` drukuje `── Podsumowanie wspólne ──` przed jobami oraz `Scalanie nie powiodło się: ...`.

**Otwarte:**
- smoke test na prawdziwym GitLabie i LLM (potwierdzenie formatu `chat/completions`)
- fixture z prawdziwego logu zamiast syntetycznego
- de-anonimizacja podsumowań, strategie innych stage'y
