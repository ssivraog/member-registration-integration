# Member Registration → Legacy Payload

An HTTP-triggered Azure Function (.NET 10, isolated worker). It takes a member registration in the CRM's JSON shape, validates it, and returns the request body the legacy membership system's API expects.

```
POST /api/registrations/legacy-payload
{ "firstName": "Alex", "lastName": "Nguyen", "dateOfBirth": "1990-04-12",
  "email": "alex.nguyen@example.com", "membershipType": "Single", "registeredAt": "2026-06-01T09:00:00Z" }

200 OK
{ "member": { "given_name": "Alex", "family_name": "Nguyen", "dob": "12/04/1990",
  "contact": { "email": "alex.nguyen@example.com" }, "plan_code": "S", "source": "MILKYWAY" } }
```

The full design, covering every validation rule, error code and the reasoning behind it, is in [`docs/DESIGN.md`](docs/DESIGN.md).

## Run it

```bash
dotnet test                       # 154 tests, no external services needed
```

To run the function locally you need [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local) and Azurite, or point `AzureWebJobsStorage` at a storage account:

```bash
cd src/MemberRegistration.Functions
func start
curl -X POST http://localhost:7071/api/registrations/legacy-payload \
  -H "Content-Type: application/json" \
  -d '{"firstName":"Alex","lastName":"Nguyen","dateOfBirth":"1990-04-12","email":"alex.nguyen@example.com","membershipType":"Single"}'
```

## Layout

| Project | Role |
|---|---|
| `src/MemberRegistration.Core` | All the parsing, validation and mapping. It has no Azure dependency, so it can be reused behind any trigger |
| `src/MemberRegistration.Functions` | A thin HTTP adapter: content type, body size limit, status codes, logging |
| `tests/MemberRegistration.Tests` | xUnit tests for the mapping, every validation failure, plan codes, date conversion and the HTTP adapter |

## Behaviour at a glance

| Situation | Result |
|---|---|
| Valid registration | **200** with the legacy JSON |
| Required field missing, `null`, empty or whitespace | **422** `required` |
| Field is not a string (e.g. `"firstName": 123`) | **422** `invalid_type` |
| Implausible email | **422** `invalid_email` |
| `membershipType` not Single/Couple/Family (case-insensitive) | **422** `invalid_membership_type` |
| `dateOfBirth` not `yyyy-MM-dd` (an ISO date-time is also accepted) | **422** `invalid_date_format` |
| `dateOfBirth` impossible (`1990-02-30`), in the future, or before 1900 | **422** `invalid_date` |
| Unknown extra fields | Ignored |
| Body empty, not JSON, not an object, or with a duplicate key | **400** |
| Content-Type not JSON / body over 64 KB | **415** / **413** |

All validation errors are returned together as `application/problem+json`, each with `field`, `code` and `message`. Messages never repeat the submitted value, and logs record only field names and codes, because the values are personal information.

**Decisions worth flagging:**
- **Slash dates are rejected rather than guessed.** `12/04/1990` could be 12 April or 4 December, and a wrong date of birth saved silently to a member record is worse than a clear error.
- **`source` is `MILKYWAY`.** The brief's prose writes it as `" MILKYWAY "` with spaces, but the contract example has none, so the example wins. It is one constant (`RegistrationTranslator.Source`) if that turns out to be wrong.
- **The future-date check uses the furthest-ahead time zone (UTC+14),** so a baby registered on its birth day in Australia isn't rejected because UTC is still on the previous day.

## Wiring this into a real outbound call

This function only builds the payload. In production the legacy API call would work like this:

**Delivery.** Put a queue in front of the call instead of calling the legacy API synchronously from the CRM. The CRM, through a Dynamics plugin or Power Automate flow, sends registrations to an **Azure Service Bus** queue. A Service Bus-triggered function runs `RegistrationTranslator` (unchanged) and posts the result. This gives at-least-once delivery, buffering while the legacy system is down, and a **dead-letter queue**: validation failures go there immediately, transient failures go there only after retries run out.

**HTTP client.** A typed `HttpClient` from `IHttpClientFactory`, with `Microsoft.Extensions.Http.Resilience`:

```csharp
services.AddHttpClient<LegacyMembershipClient>(c => c.BaseAddress = new Uri(config["Legacy:BaseUrl"]!))
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        o.Retry.MaxRetryAttempts = 3;            // exponential backoff + jitter
        o.Retry.UseJitter = true;
    });
```

- **Timeouts:** about 10 s per attempt and about 30 s in total, so one slow call can't hold a worker indefinitely. Legacy systems are often slow, so tune these against observed p99 latency.
- **Retries:** only on transient failures: network errors, 408, 429 (respecting `Retry-After`), 500, 502, 503 and 504. **Never** retry on 400 or 422, because the same payload will fail again. That message goes straight to the dead-letter queue.
- **Circuit breaker** (part of the standard handler): stops calling a legacy system that is clearly down, and lets the queue absorb the backlog.

**Idempotency.** Retries and at-least-once delivery mean the same registration *will* sometimes be sent twice. Send an `Idempotency-Key` header derived from the CRM record id. If the legacy API doesn't support that, check for an existing member by that key before creating one. Without this, retries create duplicate members.

**Authentication.**
- Preferred: the Function's **managed identity** requests an Entra ID token for the legacy API, or for **APIM** in front of it, using `DefaultAzureCredential`. There are no secrets to store or rotate.
- If the legacy API only accepts an API key or basic auth, keep the credential in **Key Vault** and surface it to the Function through a Key Vault reference in app settings. Never put it in code or `local.settings.json`. APIM can also inject it, so the Function never sees it.
- Inbound: replace the function key on this endpoint with Entra ID (Easy Auth), or put it behind APIM.

**Observability.** Pass a correlation id (the CRM record id) to the legacy call as a header, and log it on both sides. Alert on dead-letter queue depth, circuit-breaker opens and the rate of 4xx responses from the legacy API. A rising 4xx rate usually means the contract has drifted.
