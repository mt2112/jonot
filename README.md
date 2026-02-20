# Jonot — Rate-Limited Azure Functions + Service Bus Pipeline

Azure Functions -pohjainen viestinvälitysjärjestelmä, joka ohjaa viestejä Service Bus -jonojen kautta ulkoiseen REST API:in. Arkkitehtuuri rajoittaa API-kutsut **5 kutsuun sekunnissa** `FixedWindowRateLimiter`-komponentilla, jotta ulkoisen API:n rate limit (429) ei ylity.

## Arkkitehtuuri

```
┌──────────────────┐     ┌──────────────────┐
│ NotifierFunctionA│     │ NotifierFunctionB│
│  (Timer 5 min)   │     │  (Timer 5 min)   │
└────────┬─────────┘     └────────┬─────────┘
         │                        │
         ▼                        ▼
   ┌──────────┐            ┌──────────┐
   │ jono-a-in│            │ jono-b-in│
   └────┬─────┘            └────┬─────┘
        │                       │
        ▼                       ▼
┌───────────────┐       ┌───────────────┐
│JonoATrigger   │       │JonoBTrigger   │
│SourceType = A │       │SourceType = B │
└───────┬───────┘       └───────┬───────┘
        │                       │
        └───────────┬───────────┘
                    ▼
          ┌─────────────────┐
          │  api-send-queue  │  ← Session-enabled (SessionId = "global-rate-limit")
          └────────┬────────┘
                   ▼
          ┌─────────────────┐
          │  ApiSendFunction │  ← FixedWindowRateLimiter (5/s)
          │  + RestApiSender │  ← Polly retry (429 + transient)
          └───────┬─────────┘
             ┌────┴────┐
             ▼         ▼
       ┌──────────┐ ┌──────────┐
       │jono-a-out│ │jono-b-out│
       └────┬─────┘ └────┬─────┘
            ▼             ▼
   ┌──────────────┐ ┌──────────────┐
   │JonoAOutTrigger│ │JonoBOutTrigger│
   │ (bookkeeping) │ │ (bookkeeping) │
   └───────────────┘ └───────────────┘
```

### Keskeiset suunnittelupäätökset

| Päätös | Ratkaisu | Miksi |
|--------|----------|-------|
| Rate limiting | `FixedWindowRateLimiter` (5 permits / 1s) | .NET 8 BCL, ei ulkoisia riippuvuuksia |
| Yksi prosessoija | Session-enabled jono, `maxConcurrentSessions: 1` | Estää rinnakkaiskäsittelyn eri instansseissa |
| Retry | Polly (`WaitAndRetryAsync`, 3 kertaa, exp. backoff) | Kunnioittaa `Retry-After`-headeria |
| Reititys | `SourceType`-kenttä (A/B) viestissä | Tulos ohjautuu oikeaan output-jonoon |

## Projektin rakenne

```
jonot/
├── Jonot.sln
├── README.md
├── AGENTS.md
│
├── emulator/                          # Service Bus Emulator (Docker)
│   ├── docker-compose.yml             #   Docker Compose -konfiguraatio
│   ├── Config.json                    #   5 jonon määritys (sbemulatorns)
│   └── .env                           #   Ympäristömuuttujat (EULA, SQL-salasana)
│
├── src/Jonot.Functions/               # Azure Functions -projekti (.NET 8 Isolated Worker)
│   ├── Program.cs                     #   DI: rate limiter, ServiceBusClient, HttpClient + Polly
│   ├── host.json                      #   Service Bus -asetukset (session concurrency)
│   ├── local.settings.json            #   Lokaali konfiguraatio (emulator, Azurite)
│   │
│   ├── Functions/
│   │   ├── NotifierFunctionA.cs       #   Timer → jono-a-in (TODO: tietokantalogiikka)
│   │   ├── NotifierFunctionB.cs       #   Timer → jono-b-in (TODO: tietokantalogiikka)
│   │   ├── JonoATriggerFunction.cs    #   jono-a-in → api-send-queue (SourceType.A)
│   │   ├── JonoBTriggerFunction.cs    #   jono-b-in → api-send-queue (SourceType.B)
│   │   ├── ApiSendFunction.cs         #   api-send-queue → REST API → jono-a/b-out
│   │   ├── JonoAOutTriggerFunction.cs #   jono-a-out → kirjanpito (TODO)
│   │   └── JonoBOutTriggerFunction.cs #   jono-b-out → kirjanpito (TODO)
│   │
│   ├── Models/
│   │   ├── SourceType.cs              #   Enum: A, B
│   │   ├── ApiSendMessage.cs          #   Viestin payload api-send-queueen
│   │   └── ApiResultMessage.cs        #   API-kutsun tulos output-jonoihin
│   │
│   └── Services/
│       ├── IRestApiSender.cs          #   Rajapinta API-kutsuille
│       └── RestApiSender.cs           #   Toteutus: HttpClient + virhekäsittely
│
└── tests/Jonot.Functions.Tests/       # xUnit-testit
    ├── RestApiSenderTests.cs          #   RestApiSender: onnistuminen, virheet, peruutus
    ├── ApiSendFunctionTests.cs        #   ApiSendFunction: reititys A/B → output-jonot
    ├── RateLimiterTests.cs            #   FixedWindowRateLimiter: permit/reject/queue
    └── RateLimitingDemonstrationTests.cs  # Visuaalinen POC: 20 viestiä, 5/s jakautuminen
```

## Service Bus -jonot

| Jono | Sessiot | Tarkoitus |
|------|---------|-----------|
| `jono-a-in` | Ei | Lähde A:n viestit (NotifierFunctionA → JonoATrigger) |
| `jono-b-in` | Ei | Lähde B:n viestit (NotifierFunctionB → JonoBTrigger) |
| `api-send-queue` | **Kyllä** | Keskitetty jono API-kutsuille, SessionId = `"global-rate-limit"` |
| `jono-a-out` | Ei | API-kutsun tulokset lähteelle A |
| `jono-b-out` | Ei | API-kutsun tulokset lähteelle B |

## Tekniset vaatimukset

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Service Bus Emulator)
- [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) (Azure Storage -emulaattori)

## Paikallinen kehitysympäristö

### 1. Käynnistä Service Bus Emulator

```bash
cd emulator
docker compose up -d
```

Odota kunnes emulaattori on valmis (lokeihin tulee `Emulator Service is Successfully Up!`):

```bash
docker logs servicebus-emulator -f
```

### 2. Käynnistä Azurite

```bash
# Erillisessä terminaalissa
azurite --silent --location ./azurite-data --debug ./azurite-data/debug.log
```

### 3. Käynnistä Azure Functions

```bash
cd src/Jonot.Functions
func start
```

Functions-runtime käynnistää kaikki 7 funktiota ja yhdistää Service Bus Emulatoriin automaattisesti.

## Testit

### Yksikkötestien ajaminen

```bash
# Kaikki testit
dotnet test

# Yksityiskohtaisella tulosteella
dotnet test --verbosity normal

# Yksittäinen testiluokka
dotnet test --filter "FullyQualifiedName~RestApiSenderTests"
dotnet test --filter "FullyQualifiedName~ApiSendFunctionTests"
dotnet test --filter "FullyQualifiedName~RateLimiterTests"
```

### Demonstraatiotesti (rate limiting POC)

Visuaalinen testi, joka osoittaa rate limitingin toiminnan konsolitulosteella:

```bash
dotnet test --filter "FullyQualifiedName~RateLimitingDemonstration" --logger "console;verbosity=detailed"
```

Esimerkkituloste:

```
--- CALLS PER SECOND WINDOW ---
 0s -  1s     |    5  | ##### OK
 1s -  2s     |    5  | ##### OK
 2s -  3s     |    5  | ##### OK
 3s -  4s     |    5  | ##### OK

--- OUTPUT QUEUE ROUTING ---
 jono-a-out   |   10  | ##########
 jono-b-out   |   10  | ##########

TOTAL: 20 sent, 20 results, 0 over-limit
Processing time: ~3.3s (theoretical min: 4.0s)
```

### Testien rakenne

| Testi | Kuvaus | Riippuvuudet |
|-------|--------|-------------|
| `RestApiSenderTests` | HTTP-kutsut: onnistuminen, 500, HttpException, peruutus, null | NSubstitute (HttpClient mock) |
| `ApiSendFunctionTests` | Viestireititys: SourceType.A → `jono-a-out`, B → `jono-b-out` | NSubstitute (ServiceBusClient mock) |
| `RateLimiterTests` | FixedWindowRateLimiter: 5 sallittu, 6. hylätty, jonossa odottava | Ei mockeja |
| `RateLimitingDemonstrationTests` | End-to-end POC: 20 viestiä, 5/s, visuaalinen tuloste | Ei mockeja, simuloi koko pipeline |

## Konfiguraatio

### Rate limiter (`Program.cs`)

```csharp
new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = 5,           // Max 5 API-kutsua per ikkuna
    Window = TimeSpan.FromSeconds(1),  // 1 sekunnin ikkuna
    QueueLimit = 50,           // Max 50 odottavaa pyyntöä
    AutoReplenishment = true   // Automaattinen uusiminen
});
```

### Service Bus -session (`host.json`)

```json
{
  "extensions": {
    "serviceBus": {
      "maxConcurrentSessions": 1,
      "maxConcurrentCallsPerSession": 5
    }
  }
}
```

### Polly retry (`Program.cs`)

- Käsittelee: transient HTTP -virheet + 429 (Too Many Requests)
- 3 uudelleenyritystä, eksponentiaalinen backoff (2s, 4s, 8s)
- Kunnioittaa `Retry-After`-headeria

## Avoinna olevat tehtävät (TODO)

- [ ] **NotifierFunction-logiikka** — Korvaa placeholder-data tietokantakyselyillä (`NotifierFunctionA.cs`, `NotifierFunctionB.cs`)
- [ ] **Bookkeeping-logiikka** — Toteuta tietokantapäivitykset output-funktioihin (`JonoAOutTriggerFunction.cs`, `JonoBOutTriggerFunction.cs`)
- [ ] **Fake External API** — WireMock- tai minimal API -pohjainen mock-palvelin `localhost:5001`:lle täydellistä end-to-end-testausta varten
- [ ] **Integraatiotestit** — Testit jotka ajavat oikeasti Service Bus Emulatorin kautta
