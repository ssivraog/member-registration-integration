# Design Spec: CRM Registration → Legacy Membership Payload

## 1. Purpose

A small integration layer that receives a member registration in the CRM's JSON shape, validates it, and returns the JSON request body the legacy membership system's REST API expects. It runs as an HTTP-triggered Azure Function. It only translates: it does not call the legacy API (section 10 covers how that call would be wired).

## 2. Scope

| In scope | Out of scope |
|---|---|
| Parsing the CRM registration JSON | UI or front end |
| Validating required fields, email, membership type and date of birth | Deploying to Azure |
| Mapping to the legacy contract (names, nesting, date format, plan code, fixed source) | The real outbound call to the legacy API (design notes only, section 10) |
| HTTP-triggered Azure Function endpoint | Persistence, queues, Dynamics 365 plugins |
| Automated tests | |
| README notes on auth, retries and timeouts | |

## 3. Contracts

### 3.1 Input (CRM registration)

```json
{
  "firstName": "Alex",
  "lastName": "Nguyen",
  "dateOfBirth": "1990-04-12",
  "email": "alex.nguyen@example.com",
  "membershipType": "Single",
  "registeredAt": "2026-06-01T09:00:00Z"
}
```

### 3.2 Output (legacy request body)

```json
{
  "member": {
    "given_name": "Alex",
    "family_name": "Nguyen",
    "dob": "12/04/1990",
    "contact": { "email": "alex.nguyen@example.com" },
    "plan_code": "S",
    "source": "MILKYWAY"
  }
}
```

### 3.3 Field mapping

| CRM field | Legacy field | Rule |
|---|---|---|
| `firstName` | `member.given_name` | Required. Leading and trailing whitespace trimmed; otherwise unchanged |
| `lastName` | `member.family_name` | Required. Trimmed |
| `dateOfBirth` | `member.dob` | Required. Parsed as described in 4.3, written as `dd/MM/yyyy` using the invariant culture |
| `email` | `member.contact.email` | Required. Trimmed and checked for plausibility (4.2). Case is kept as sent |
| `membershipType` | `member.plan_code` | Required. `Single`→`S`, `Couple`→`C`, `Family`→`F`. Matching ignores case and surrounding whitespace |
| *(none)* | `member.source` | Always the literal `MILKYWAY` |
| `registeredAt` | *(dropped)* | Not part of the legacy contract. Not validated |
| any other field | *(dropped)* | Ignored. It never causes an error |

> **Note on `source`:** the brief's prose shows `" MILKYWAY "` with spaces, but the contract example shows `"MILKYWAY"`. Since the example is the contract, the output uses `MILKYWAY` with no spaces. It lives in one constant, so it is easy to change if the spaces turn out to be intended.

## 4. Validation rules

All rules run on every request and **every error found is returned together**, so the caller can fix everything in one pass. No mapping happens unless validation passes.

### 4.1 Body and field shape

| Condition | Error code | HTTP |
|---|---|---|
| Empty body | `body_empty` | 400 |
| Body is not valid JSON | `body_malformed` | 400 |
| JSON root is not an object (for example an array or a string) | `body_not_object` | 400 |
| A known field is present but is not a JSON string (for example `"firstName": 123`) | `invalid_type` | 422 |
| A required field is missing, `null`, empty, or only whitespace | `required` | 422 |

Field names are matched **exactly** (camelCase, as the CRM sends them). So `"FirstName"` counts as an unknown extra field and is ignored, and the request then fails with `required` on `firstName`. The error message tells the caller what is wrong without the service guessing at what they meant.

### 4.2 Email

The email must be **plausible**. A deliverability check is out of scope. The rules after trimming:

- at most 254 characters
- exactly one `@`, with a non-empty local part before it
- no whitespace anywhere
- the domain contains at least one `.`, and does not start or end with `.` or contain `..`

Implemented as one compiled regex plus a length check. Failure → `invalid_email`.

### 4.3 Date of birth

The canonical format is **`yyyy-MM-dd`**. Dates are parsed with `DateOnly.TryParseExact` and the invariant culture.

| Input | Result | Reason |
|---|---|---|
| `1990-04-12` | accepted | Canonical |
| ` 1990-04-12 ` | accepted | Trimmed first |
| `1990-04-12T00:00:00Z`, `1990-04-12T00:00:00+10:00` | accepted, date part `1990-04-12` | An ISO 8601 date-time is unambiguous. The date is taken **exactly as written** and never shifted between time zones, so a birth date never moves by a day |
| `12/04/1990`, `04/12/1990`, `12-04-1990`, `12.04.1990` | rejected, `invalid_date_format` | Day/month order cannot be known for sure (12 April or 4 December?). Guessing could save a wrong DOB to a member record, so the request is rejected with a message stating the expected format |
| `1990-02-30`, `1990-13-01` | rejected, `invalid_date` | Right format, but not a real date |
| a future date | rejected, `invalid_date` | A birth date cannot be in the future. "Today" comes from an injected `TimeProvider`, so tests are deterministic |
| before `1900-01-01` | rejected, `invalid_date` | Sanity floor to catch typos such as `0990-04-12` |

### 4.4 Membership type

The value is trimmed and compared ignoring case against `Single | Couple | Family`. Anything else → `invalid_membership_type`, with a message listing the allowed values.

### 4.5 Error shape

Errors come back as an RFC 9457 problem details response with an `errors` extension. `type` is left out, which per the RFC means `about:blank`, so the status code carries the meaning:

```json
{
  "title": "Registration failed validation.",
  "status": 422,
  "errors": [
    { "field": "email", "code": "invalid_email", "message": "email is not a valid email address." },
    { "field": "membershipType", "code": "invalid_membership_type", "message": "membershipType must be one of: Single, Couple, Family." }
  ]
}
```

Messages **never echo the submitted value**, because the values are personal information (PII).

## 5. Architecture

### 5.1 Solution layout

```
new-project/
  MemberRegistration.slnx
  README.md
  docs/DESIGN.md
  src/
    MemberRegistration.Core/           # Pure logic, no Azure dependency
      Contracts/
        LegacyMemberRequest.cs         # Output records with [JsonPropertyName]
        LegacyJson.cs                  # Serialiser that writes non-ASCII and '+' literally
      Validation/
        ValidationError.cs             # record(Field, Code, Message)
        ErrorCodes.cs                  # The string constants from section 4
        FieldNames.cs                  # CRM field names
      Parsing/
        RegistrationReader.cs          # JsonDocument → ReadResult (Read | Unreadable)
        RawRegistration.cs             # Nullable strings, exactly as received, + wrong-type fields
        ReadResult.cs
      Mapping/
        DateOfBirthParser.cs           # Section 4.3
        PlanCodeMapper.cs              # Section 4.4
        EmailRule.cs                   # Section 4.2
      RegistrationTranslator.cs        # Entry point: string JSON → TranslationResult
      TranslationResult.cs             # Success(LegacyMemberRequest) | Failure(kind, errors)
    MemberRegistration.Functions/      # Thin HTTP adapter
      Program.cs                       # Isolated worker host, DI, TimeProvider.System
      TranslateRegistrationFunction.cs
      host.json, local.settings.json   # Local settings hold no secrets
  tests/
    MemberRegistration.Tests/
      TestSupport.cs                   # Fixed clock, sample input and expected output
      DateOfBirthParserTests.cs
      PlanCodeMapperTests.cs
      EmailRuleTests.cs
      RegistrationTranslatorTests.cs   # Whole mapping and validation, end to end
      TranslateRegistrationFunctionTests.cs  # Status codes and response bodies
```

**Why Core is separate from Functions:** the mapping is the deliverable, and the Function is only one way to run it. Keeping Core free of Azure means it can be unit tested without a host, and it could later sit behind a Service Bus trigger or an APIM policy unchanged.

### 5.2 Processing flow

```
POST /api/registrations/legacy-payload
  │
  ▼
TranslateRegistrationFunction
  │  read body (max 64 KB) ──────────────────────────► 413 if larger
  ▼
RegistrationTranslator.Translate(json)
  ├─ RegistrationReader ── JSON broken / not an object ──► Failure(BadRequest)  → 400
  │     └─ RawRegistration + type errors
  ├─ validate: required, email, DOB, membershipType (collect all)
  │     └─ any errors ────────────────────────────────► Failure(Invalid)     → 422
  └─ map → LegacyMemberRequest ──────────────────────► Success              → 200
```

### 5.3 Key types

```csharp
public sealed record ValidationError(string Field, string Code, string Message);

public abstract record TranslationResult
{
    public sealed record Success(LegacyMemberRequest Request) : TranslationResult;
    public sealed record Failure(FailureKind Kind, IReadOnlyList<ValidationError> Errors) : TranslationResult;
}
public enum FailureKind { MalformedBody, Invalid }

public sealed record LegacyMemberRequest([property: JsonPropertyName("member")] LegacyMember Member);
public sealed record LegacyMember(
    [property: JsonPropertyName("given_name")]  string GivenName,
    [property: JsonPropertyName("family_name")] string FamilyName,
    [property: JsonPropertyName("dob")]         string Dob,
    [property: JsonPropertyName("contact")]     LegacyContact Contact,
    [property: JsonPropertyName("plan_code")]   string PlanCode,
    [property: JsonPropertyName("source")]      string Source);
public sealed record LegacyContact([property: JsonPropertyName("email")] string Email);

public sealed class RegistrationTranslator(TimeProvider clock)
{
    public TranslationResult Translate(string json);
}
```

**Design choices:**
- **Results instead of exceptions for validation.** Invalid input is an expected outcome, not an exceptional one. Exceptions are reserved for real faults, and the Function returns 500 with no details for those.
- **Parsing with `JsonDocument` instead of deserialising to a POCO.** `JsonSerializer.Deserialize` throws on a type mismatch such as `"firstName": 123`. Reading the document by hand lets the service report *which* field has the wrong type, and makes "ignore extra fields" explicit rather than a side effect of serialiser defaults.
- **`DateOnly` and invariant culture.** Output formatting cannot be changed by the server's locale.
- **`TimeProvider` is injected.** The future-date rule can then be tested.
- Property order in the output records matches the contract, so the serialised JSON looks the same as the example.

## 6. HTTP API

| | |
|---|---|
| Route | `POST /api/registrations/legacy-payload` |
| Auth level | `Function` (function key). Section 10 covers production |
| Request | `Content-Type: application/json`, the CRM registration |
| 200 | `application/json`, the legacy request body |
| 400 | `application/problem+json`: the body is empty, not JSON, or not an object |
| 413 | The body is over 64 KB |
| 415 | `Content-Type` is not JSON |
| 422 | `application/problem+json`: field validation errors (section 4.5) |
| 500 | `application/problem+json` with only a generic title; the details are logged |

Stack: .NET 10 (LTS) with the **Azure Functions isolated worker model** (`Microsoft.Azure.Functions.Worker`, `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`), so the trigger can return `IActionResult`.

## 7. Logging and PII

- Log the outcome, the error **codes** and **field names**, and the invocation id. **Never** log names, email or DOB.
- No request body is written to logs, including at debug level.
- Application Insights through the worker's default OpenTelemetry/App Insights integration (configuration only).

## 8. Test plan (xUnit)

| Area | Cases |
|---|---|
| **Valid registration** | The sample input produces JSON exactly equal to the contract (compared structurally with `JsonNode.DeepEquals`) |
| **Plan-code mapping** | `Single→S`, `Couple→C`, `Family→F` (Theory). Case and whitespace variants (`single`, ` FAMILY `). `Gold`, `""` and `Singles` rejected |
| **Date conversion** | `1990-04-12→12/04/1990`. Single-digit day and month are zero-padded (`2001-01-05→05/01/2001`). Leap day `2000-02-29` accepted, `1999-02-29` rejected. ISO date-time accepted with no time-zone shift. Slash, dot and day-first formats → `invalid_date_format`. Future date and pre-1900 date → `invalid_date`. Output is unchanged when `CultureInfo.CurrentCulture` is set to `en-US` |
| **Required fields** | Theory over the 5 required fields × {missing, `null`, `""`, `"   "`} → `required` on that field |
| **Email** | Valid: `a@b.co`, plus addressing (`alex+test@example.com.au`). Invalid: `alex`, `alex@`, `@example.com`, `alex@example`, `a@@b.com`, `a b@c.com`, `a@b..com`, over 254 characters |
| **Membership type** | Unknown value → `invalid_membership_type` |
| **Wrong types** | `"firstName": 123`, `"email": {}` → `invalid_type` |
| **Extra fields** | Unknown fields, including nested objects and arrays → mapping succeeds and the output is unchanged. `registeredAt` absent → still succeeds |
| **Multiple errors** | Bad email and bad type in one request → both errors returned |
| **Body shape** | Empty body, `{`, `[]`, `"text"` → `MalformedBody` |
| **Function adapter** | 200 with the legacy JSON. 422 and 400 return `application/problem+json`. Exercised by calling the function class directly with a `DefaultHttpContext`; no Functions host is needed |

Run with `dotnet test`. No external services are needed.

## 9. Assumptions and open questions

1. `source` is `MILKYWAY` with no surrounding spaces (section 3.3 note).
2. The legacy system's field length limits and allowed character set for names are unknown. Names are passed through trimmed, and no length limit is enforced beyond the 64 KB body limit. **To confirm with the legacy system's owner.**
3. Day-first or month-first slash dates are rejected rather than guessed. If the CRM is known to send `dd/MM/yyyy`, that could be accepted explicitly as a second format.
4. Email case is preserved. Some legacy systems want lowercase, which would be a one-line change.
5. Field names are matched case-sensitively (section 4.1).

## 10. Production wiring (summary; the README expands on it)

- **Delivery pattern:** the CRM puts registrations on **Azure Service Bus**. A queue-triggered function translates each one and calls the legacy API. The HTTP endpoint here is for synchronous use and testing. A queue gives at-least-once delivery, buffering when the legacy system is down, and a dead-letter queue for messages that fail validation or keep failing.
- **Outbound client:** a typed `HttpClient` from `IHttpClientFactory`, with `Microsoft.Extensions.Http.Resilience`:
  - **Timeouts:** about 10 s per attempt and about 30 s in total.
  - **Retries:** 3 attempts with exponential backoff and jitter, **only** on transient failures (network errors, 408, 429 honouring `Retry-After`, 502/503/504). Never on 400 or 422.
  - **Circuit breaker** so a failing legacy system is not overloaded.
- **Idempotency:** retrying a create is only safe if duplicates are prevented. Send an `Idempotency-Key` header, or a business key derived from the CRM record id. If the legacy API supports neither, look up the member before creating on retry.
- **Authentication:** a managed identity gets an Entra ID token for the legacy API, or for APIM in front of it. If the legacy API only supports an API key or basic auth, the secret lives in **Key Vault** and is read through a Key Vault reference, never stored in config.
- **Inbound:** the function sits behind APIM or uses Entra ID authentication instead of function keys.
- **Observability:** a correlation id (the CRM record id) is passed to the legacy call as a header and logged. Alerts fire on dead-letter count and circuit-breaker opens.
