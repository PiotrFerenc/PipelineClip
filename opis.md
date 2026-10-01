# Analizator pipeline z gitlab

# opis
w gitlab jest pipeline który posiada konkretne kroki. jak któryś krok wykona się nieprawidłowo to mechanizm pobiera ten konkretny krok i analizuje co się stało. w pierwszym etapie będzie to aplikacja konsolowa.

# rule
- każdy stage powinien mieć zaimplementowaną własną strategię (np stage test -> TestStageAnalizator)
- komunikacja po API gitlab

# zakres MVP
- obsługiwany tylko stage `test` (TestStageAnalizator)
- pozostałe stage'e poza zakresem; architektura (wybór strategii po nazwie stage) ma pozwolić dodać je później bez zmian w rdzeniu

# architektura
- API gitlab
- dotnet
- LLM
- DI
- wzorce projektowe
- testy jednostkowe
- reranker
- retriever

## LLM
- wywołania LLM przez dedykowany HttpClient z własną sekcją konfiguracji (skill `dotnet-llm-http-config`): osobny named HttpClient, nazwa modelu i system prompt
- dostawca własny, wywoływany przez HttpClient (bez SDK dostawcy)
- konfiguracja (adres, klucz, model, system prompt) w `appsettings.json`

## retriever i reranker
- dotyczą fragmentów logu joba
- log dzielony na chunki, retriever wybiera istotne, reranker porządkuje, do LLM trafiają tylko najlepsze
- technika (wybrana jako najszybsza, bez dodatkowych wywołań LLM i bez embeddingów):
    - retriever: BM25 po chunkach logu
    - reranker: heurystyka punktowa (wzorce błędów typu `error`, `failed`, `exception`, `Assert`, stack trace, bliskość końca logu)
- za interfejsami `IRetriever` i `IReranker`, więc można później podmienić na embeddingi lub LLM

## anonimizacja
- użycie istniejącej biblioteki `Anonymizer.Core` z `~/Projekty/Anonimizator` (referencja do projektu)
- anonimizacja następuje po wyborze fragmentów, przed wysyłką do LLM

# wejscie
- ID pipeline

# wyjscie
- podsumowanie błędu i sugestie

# proces
1. użytkownik podaje ID pipeline który zakończył się błędem
2. mechanizm po API Gitlab sprawdza które kroki skończyły się błędem
3. mechanizm wybiera odpowiednią strategię (MVP: tylko `test`)
4. dla każdego nieudanego joba `test` strategia (fan-out, równolegle):
    - pobiera log joba
    - dzieli na chunki, retriever + reranker wybierają istotne fragmenty
    - anonimizuje
    - wysyła do LLM w celu podsumowania
5. fan-in: wyniki z wszystkich jobów łączone w jedno podsumowanie i sugestie
6. wynik wraca do konsoli

## przypadki brzegowe
- job `test` zakończony sukcesem: nic nie robimy
- brak nieudanych jobów `test`: nic nie robimy (bez komunikatu błędu)

# konfiguracja
- `appsettings.json`: URL instancji GitLab, token GitLab, sekcja LLM

## limity
- log joba obcinany do ostatnich 2 MB (początek logu pomijany)
- do LLM trafia maksymalnie 20 chunków na job
- oba limity w `appsettings.json`
