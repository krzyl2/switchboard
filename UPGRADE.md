# Przegląd architektury Switchboard

Przegląd z 2026-10-30, wersja 0.12.1 (commit `fe9e1f1`). Zakres: `src/` (~56 tys. linii C#), `web/` (~35 tys. linii TS), `switchboard/`, `deploy/`.

Architektura jest dobra: warstwy pilnowane testami, niezmienne snapshoty między pętlami, zamknięta lista kodów przyczyn wymuszona w konstruktorze `Decision`, a zakaz bezpośredniego czytania zegara egzekwowany przy kompilacji. Słabe miejsca to trzy klasy po 1200–2000 linii, model urządzenia oparty na `if`-ach zamiast typów, kopie tej samej logiki w kilku miejscach i jedna realna dziura w izolacji ochrony od reszty.

✔ oznacza ustalenie sprawdzone ręcznie w kodzie. Pozostałe pochodzą od recenzentów; mają podane miejsca w kodzie (`plik:linia`), ale nie każda linia była sprawdzana.

## P0 – bezpieczeństwo i ochrona

1. ✔ **Błąd w dispatchu wyłącza ochronę.**
   - `GuardRunner.cs:181-183` liczy guard, dispatch i gate w jednym bloku bez izolacji.
   - Wyjątek w ekonomii, planie lub EV zatrzymuje pętlę guarda i cały add-on (`GuardService.cs:98-106`).
   - `ShutdownAsync` (`GuardRunner.cs:226-231`) wywołuje cały `Step()` jeszcze raz, razem z `_dispatch.Evaluate`. Ten sam wyjątek wraca, więc polecenia wyłączenia urządzeń przy zamknięciu nie wychodzą.
   - Poprawka: dispatch w `try`, a przy błędzie `dispatch: null` (`Apply` już to obsługuje, `EffectorGate.cs:741`). Zamknięcie tylko ścieżką guarda, przez istniejące przeciążenie `Apply(evaluation, mode, now)`.
2. **Kill switch i przełącznik sterowania mogą zapisać się do `state.json` w złej kolejności.**
   - `Notify` jest wołane poza blokadą (`KillSwitch.cs:114`, `ControlSwitch.cs:167`), a koordynator bierze stan prosto z eventu (`RuntimeStateCoordinator.cs:166-179`, `:302-310`).
   - Override'y i cele mają `Version`, te dwa przełączniki nie.
   - Skutek: dwa przełączenia naraz (z MQTT i z UI) mogą zostawić w pliku „sterowanie dozwolone”, choć użytkownik je wyłączył.
3. ✔ **MQTT może włączyć sterowanie i zwolnić kill switch** (`GuardEntities.cs:155`, `:178-185`). To świadoma decyzja z 2026-10-25 (`DECISIONS.md:328`), ale oznacza, że ufamy każdemu klientowi brokera (Zigbee2MQTT, Tasmota…).
   - Rekomendacja: przez MQTT domyślnie tylko kierunek bezpieczny (kill ON, sterowanie OFF, `force_off`), kierunek włączający za flagą konfiguracyjną. Do tego unikalny client id zamiast stałego `switchboard` (`appsettings.json:29`).
4. ✔ **W UI znika przycisk kill switcha**, gdy zawiedzie odczyt stanu guarda (`Limits.tsx:756`). Zwolnienie kill switcha nie ma potwierdzenia (`Limits.tsx:700`), a włączenie sterowania ma.
   - Rekomendacja: przycisk zawsze widoczny (ponowne „włącz” jest bezpieczne), potwierdzenie przy zwalnianiu, ponowny odczyt stanu po nieudanej zmianie (`useControl.ts:103-104`).
   - Do tego error boundary na całą aplikację (`App.tsx:272`), bo dziś jeden wyjątek przy renderowaniu czyści całą stronę razem z kill switchem.
5. **Drobniejsze:**
   - `_ = SendOneAsync` (`HomeAssistantDeviceCommandPort.cs:278`) nie łapie nieoczekiwanych wyjątków, więc jeden mógłby zablokować wszystkie dalsze polecenia dla tego urządzenia. Dziś niewywoływalne, ale tani bezpiecznik.
   - `Dockerfile:88` uruchamia `dotnet-install.sh` bez sumy kontrolnej, obrazy bazowe przypięte tagiem, nie digestem, kontener działa jako root.
   - Flaga `SWITCHBOARD_DEV_TRUST_ALL_REQUESTS` nie jest odrzucana w trybie add-onu (`IngressAccessPolicy.cs:27`).
   - Limit rozmiaru body działa tylko w części endpointów; reszta przyjmuje do 30 MB.
   - Brak nagłówka CSP.

## Model obiektowy i polimorfizm

Tu polimorfizm ma sens:

| Gdzie | Dziś | Propozycja |
|---|---|---|
| **Urządzenie** (największy zysk) | 80 rozgałęzień na `IsSetpoint`/`CanSwitchOff` w 21 plikach, `Range!`, `DeviceLevel(Relays, Setpoint?, …)` jako ręczna unia | `abstract record DeviceModel` z podtypami `RelayModel` i `SetpointModel`; `DeviceLevel` z podtypami `RelayLevel` i `SetpointLevel`. Wystarczą dwa podtypy, nie cztery. `DeviceType` zostaje jako enum konfiguracji |
| **Taryfa** | `Tariff.cs:34-64` sprawdza typ (`is ZonedPrice/DynamicPrice`) i używa `_fallback!` | `FlatTariff`, `ZonedTariff`, `DynamicTariff(fallback, add)`, tworzone fabryką `Tariff.For(spec)` |
| **Cel** | `GoalRequest` ma 18 parametrów pozycyjnych, a pola zależne od rodzaju celu są nullable | `GoalTarget` z podtypami `EnergyTarget` i `SocTarget`, każdy z własnym `Remaining()`; `GoalNumber` jako `Literal` albo `FromSignal` |
| **Źródło energii** | 3 różne reprezentacje (`EnergyClass`+`string? Zone`, `bool Pv`, string w `PlanEntry.Class`) | `EnergySource` z wariantami `Surplus` i `Grid(ZoneId?)`, `ZoneId` jako struct |
| **`SourceSet`** | `Pv` zawsze `true`; publiczny konstruktor omija `TryParse` | `PvOnly`, `Any` albo `PvAnd(zones)` |
| **Parser konfiguracji** | switch po typie urządzenia w 4 miejscach; warstwa setup porównuje typy jako stringi (`"modulating"`) | `DeviceTypeSchema` jako strategia per typ; drafty parsowane na enumy od razu przy wejściu do API |

Tu polimorfizm nie ma sensu: `ISectionParser` (sekcje zależą od siebie, a jawna kolejność wywołań jest uczciwa), `Verification.Judge`, `ReactionDelay`, `DayClass` (po jednym switchu, mogą zostać enumami).

**Typy jednostek kończą się na granicy guarda i dispatchu:**
- Planner ma 16 ręcznych `/1000`, ceny są gołym `double` w 8 typach.
- ✔ Dowód, że to realne ryzyko: `EvComposer.cs:198` porównuje procent z `MetEpsilonKwh`.
- Dodać `Watts × TimeSpan → KilowattHours`, `PricePerKwh × KilowattHours → Money`, `Min` dla wszystkich jednostek.

**`Decision`** ma 16 argumentów pozycyjnych (`Decision.cs:96-112`); `overridden` i `refusedBy` to oba `ReasonCode?`, więc zamiana kompiluje się bez ostrzeżenia. Do zastąpienia fabrykami `Decision.Device(...)` i `Decision.System(...)`.

**Niezmienniki pilnowane przez wołających, nie przez typ:**
- `ModulatingRange` bez walidacji, a `DeviceLevels.cs:38` dzieli przez `Step`.
- `DeviceStateSample` nie wymusza, że `Applied` jest null, gdy próbka nie jest świeża.

## Za duże klasy – propozycje podziału

| Klasa | Wydzielić | Zostaje razem |
|---|---|---|
| `EffectorGate` (1960) | `CommandTracker`, `SettlingVerifier`, `RestoreBaselines`, `TransitionObserver`, `DecisionFactory`; `DeviceTargets.State` (25 zmiennych pól) na pod-rekordy | `Resolve` (kolejność arbitrażu) i `Accept` jako jedna atomowa operacja |
| `GuardEngine` (1230) | `HoldLedger`, `PhaseGuards`, `LoadEstimator`, `GuardStatusProjector` | aktualizacje blokad w ramach jednej ewaluacji |
| `DispatchEngine` (4 pliki partial) | `ForeignLoadObserver`, `BalanceTracker`, `EconomicsTracker`, `PlanReader`, każdy z własnym `Reset()` | — |
| `SignalStore` (1360) | rejestr, przyjmowanie próbek, świeżość, `BalanceChecker`, budowanie snapshotu; niezmienny `SignalMap` zamiast 12 osobnych resetów pól | — |
| `Planner` (923) | `CapacityLedger`, `GoalWork`, `SpreadPass`, `OrderRefiner`, `PlanAssembler` | — |
| `PlannerService` (650) | `ReplanTriggers`, `SolverRunner`, `PlanAdoption`; równość rekordów zamiast sygnatur tekstowych | — |

W `Planner` dodatkowo 9 flag w `Work` zamienić na jeden enum stanu. Endpointy `SetupEndpoint`, `PlanEndpoint` i `EvEndpoint` liczą logikę aplikacji; przenieść ją do serwisów w Application, endpointy tylko mapują DTO. Schemat „zmień, potem poczekaj na ewaluację guarda” jest napisany dwa razy (`ControlEndpoint.cs:136-171`, `DispatchEndpoint.cs:134-174`).

## Duplikaty

- **Istotne dla bezpieczeństwa:** „które ograniczenia obciąża urządzenie” jest liczone 3 razy (`GuardEngine.cs:695`, `EffectorGate.cs:889`, `DispatchEngine.cs:626`). Jeśli kopie się rozjadą, guard i dispatch nie zgodzą się co do fazy. Przenieść do Domain (`ProtectionSetup.ConstraintsOf`, `DeviceLevels.StageOn`).
- **Konfiguracja:** reguła `max_age` 6–7 razy, tabele pisowni enumów w kilku kopiach. Jedno `ConfigSpelling` na `FrozenDictionary`.
- **Domain:**
  - Hash FNV jest skopiowany 4 razy.
  - Zamiana czasu lokalnego na moment z obsługą zmiany czasu jest skopiowana 4 razy.
  - `Planner.VEff` i `WorthIt` powtarzają `EffectiveValue` i `ValueTest`.
- **Zapis plików:** wzorzec `LatestStateWriter<T>` jest zbudowany 3 razy, zapis atomowy też 3 razy (żadna kopia nie robi fsync katalogu po rename).
- **Web:**
  - Pętla odpytywania serwera istnieje w 8 kopiach z 4 różnymi sposobami wyboru, która odpowiedź wygrywa. `useDispatch` potrafi nadpisać nowszą odpowiedź starszą.
  - Dwa `parseNumber` w krokach kreatora zachowują się różnie dla tego samego wejścia (`SetupMeter.tsx:121`, `SetupLoads.tsx:126`).

## Nowoczesny .NET i narzędzia

- `System.Threading.Lock` zamiast 28 blokad na `object`. Zmiana mechaniczna.
- **Composition root:** 45 ręcznych fabryk `sp => new X(...)`. Wystarczy zarejestrować rekord `DataDirectory` i wartości opcji, a większość zamieni się na zwykłe `AddSingleton<T>()`.
- **Minimal API:** `MapGroup("/api")` z filtrem wymagającym JSON przy metodach zmieniających stan, jeden czytnik body z limitem, `AddProblemDetails()`, `TypedResults`. Dziś 6 ręcznych czytników i 2 formaty błędów.
- **Zamknięcie guarda** w `IHostedLifecycleService.StoppingAsync`, żeby nie zależało od kolejności rejestracji.
- **Analiza statyczna:**
  - Dodać `.editorconfig`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`.
  - Dopisać `TimeProvider.System` do `BannedSymbols.txt` (jest awaryjne użycie w `DispatchEngine.Economics.cs:31`).
- **Testy architektury:** pogrupować według reguł, nie według etapów (`Stage2..5ArchitectureTests`).
- **JSON w persystencji:** zostawić ręczny. Jest ścisły i obsługuje wiele wersji schematu, a AOT nic tu nie da. Scalić tylko zdublowane helpery (`DecisionJson.cs:679-711`, `RuntimeStateFile.cs:788-808`).
- **`[LoggerMessage]`:** niski priorytet.
- **Web:**
  - W `tsconfig.json` włączyć `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes` i `verbatimModuleSyntax`.
  - Dodać `assertNever` w `Limits.tsx:570`.
  - Kontekst `useT()` zamiast 219 propsów `t={t}`.
  - Układ folderów według funkcji (`features/…`).
  - Jeden wspólny `api/http.ts`.

## Wydajność

Pętla sterowania nie ma problemu: najwyżej 64 urządzenia przy okresie 1–30 s to poniżej 1 ms na krok. Realne problemy:

- **Log decyzji** otwiera, zapisuje, robi fsync i zamyka plik przy każdym kroku z decyzjami (`DecisionFileSystem.cs:34`). To zużywa kartę SD w Raspberry Pi; zapisywać paczkami w konfigurowalnym oknie.
- **WebSocket** kopiuje każdą ramkę 3 razy: `get_states` o rozmiarze 16 MB staje się stringiem 32 MB (`ClientWebSocketTransport.cs:43`). Parsować bezpośrednio z bajtów.
- **Historia decyzji** wczytuje cały plik dnia na każdą stronę (`JsonlDecisionHistory.cs:69`).
- **`/api/plan`** przebudowuje `PlanInputs` przy każdym żądaniu, gdy planu nie ma (`PlanEndpoint.cs:238-240`).
- **Web:**
  - Paczka ma 615 KB (167 KB gzip), z czego oba języki to około 30% kodu.
  - Odpytywanie nie zatrzymuje się w ukrytej karcie.
  - Karta urządzenia uruchamia 5 równoległych pętli odpytywania.

## Co zostawić bez zmian

- Kanały z jednym czytelnikiem i niezmienne snapshoty: guard nigdy nie widzi połowicznie zaktualizowanego cyklu.
- Allow-lista poleceń, która czyta zserializowane bajty tak jak HA; strażnik tematów MQTT bez wildcardów.
- Czytnik YAML: odrzuca aliasy, tagi i duplikaty kluczy, ma limit głębokości i rozmiaru.
- `state.json` traktowany jako niezaufany: uszkodzony plik blokuje sterowanie.
- Typy nazw z walidacją (`DeviceName`, `SignalName`), brak operatorów między jednostkami w ochronie.
- Frontend: walidacja odpowiedzi API przy wejściu (`strict.ts`), tłumaczenie `pl` sprawdzane w czasie kompilacji względem `en`, jedna zależność runtime.

## Kolejność prac

1. **P0, w tej kolejności:**
   - izolacja dispatchu od guarda i zamknięcie tylko ścieżką guarda,
   - wersje stanu dla kill switcha i przełącznika sterowania,
   - przycisk kill switcha i error boundary w UI,
   - kierunek MQTT.
2. Jedno źródło „ograniczeń urządzenia” w Domain.
3. Hierarchia `DeviceModel`, potem podział `EffectorGate` i `GuardEngine`; najpierw testy charakteryzujące obecne zachowanie.
4. Duplikaty, composition root, Minimal API, web `usePoll` i `http.ts`.
5. Typy jednostek w planerze, EV i ekonomii.

## Otwarte decyzje maintainera

- Błąd dispatchu potrzebuje nowego kodu przyczyny, a to oznacza podbicie wersji listy w `04-ALGORITHMS.md`.
- Czy MQTT ma nadal móc *włączać* sterowanie.
