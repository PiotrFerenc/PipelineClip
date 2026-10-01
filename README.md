# PipelineClip

Konsolowy analizator nieudanych jobów `test` w pipeline GitLab: pobiera log, wybiera istotne fragmenty (BM25 + heurystyka), anonimizuje (`Anonymizer.Core`) i prosi LLM o podsumowanie błędu i sugestie. Specyfikacja: `opis.md`, plan: `PLAN.md`.

## Konfiguracja
Skopiuj `PipelineClip.Cli/appsettings.Example.json` do `appsettings.json` (gitignored) i uzupełnij `GitLab` (BaseAddress, ProjectId, ApiKey) oraz `TestStageAnalysis` (BaseAddress, Model, ApiKey). Format API LLM: zgodny z `POST chat/completions`.

Opcjonalne scalanie podsumowań (sekcja `PipelineMerge`, domyślnie `Enabled: false`): przy co najmniej 2 udanych analizach drugie wywołanie LLM dostaje wyłącznie zanonimizowane podsumowania per job i zwraca jedno podsumowanie wspólne. Ma własny `BaseAddress`/`Model`/`ApiKey`, niezależny od `TestStageAnalysis`. Błąd scalania nie przerywa raportu.

## Użycie
`dotnet run --project PipelineClip.Cli -- <pipelineId> [--project <id>]`

Kody wyjścia: 0 ok (także brak jobów do analizy), 1 błąd, 2 złe argumenty, 130 Ctrl+C.

## Ograniczenia
- Anonimizacja obejmuje ścieżki plików, e-maile, IPv4 i sekrety przy kluczu (`password=`, `token=`, `Authorization:`, `glpat-`). Sekret luzem bez klucza obok nie jest wykrywany (PLAN.md, sekcja 9). Nie używaj na logach z wrażliwymi danymi bez przeglądu.
- Tylko stage `test`, bez jobów bridge/child pipeline.
