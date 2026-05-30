# Setup multi-tenant (Platform + Customer obligatorio) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `POST /api/setup` create `platform` tenant + Platform Admin **and** a `Customer` tenant + Customer Admin in one operation, with password-policy enforcement and distinct-email validation, plus a fully-i18n Web wizard.

**Architecture:** Extend the single `Setup` handler in `SetupEndpoints.cs` (no new service, no new AOT JSON types — only add fields to the already-registered `SetupRequest`/`SetupResponse` records). Customer creation mirrors the proven pattern in `ManagementTenantEndpoints.CreateTenant`. Password validation reuses `PasswordService.ValidatePolicy`. Frontend migrates `setup-page.tsx` to `useTranslation()` with keys in all 3 locales.

**Tech Stack:** .NET 10 Minimal API (AOT, System.Text.Json source-gen), xunit + FluentAssertions, React 19 + react-hook-form + zod + react-i18next, Vitest.

**Spec:** [docs/specs/2026-05-30-setup-multitenant-platform-customer.md](../../specs/2026-05-30-setup-multitenant-platform-customer.md)

---

## File Structure

**Backend (`Verbara.Platform`):**
- Modify: `src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs` — extend `SetupRequest`/`SetupResponse` records + the `Setup` handler.
- Test: `tests/Verbara.Platform.Api.Tests/SetupEndpointTests.cs` — update 3 existing + add 5 new tests.
- (No change needed to `ApiJsonContext.cs` — `SetupRequest`/`SetupResponse`/`ErrorDetailResponse`/`ErrorResponse` already registered.)

**Frontend (`Verbara.Platform.Web`):**
- Modify: `src/core/api/hooks/use-system.ts` — extend `SetupInput`/`SetupResponse` interfaces.
- Modify: `src/core/auth/setup-page.tsx` — `useTranslation()`, Customer fieldset, zod schema, distinct-email + policy rules.
- Modify: `src/core/auth/setup-page.test.tsx` — new fields + validation.
- Modify: `public/locales/en-US/common.json`, `public/locales/es-419/common.json`, `public/locales/pt-BR/common.json` — `setupPage` i18n block (parity enforced in CI).

**Docs:**
- Modify: `docs/manuales/smb/03-setup-inicial.md` — document the Customer step.

---

## Reference: existing facts (read before coding)

- `SetupRequest` / `SetupResponse` records live at the bottom of `SetupEndpoints.cs` and are registered in `ApiJsonContext.cs:250-251`.
- Error shapes (`src/Verbara.Platform.Api/Endpoints/Shared/ErrorResponses.cs`, both registered in `ApiJsonContext`):
  - `internal sealed record ErrorResponse(string Error);`
  - `internal sealed record ErrorDetailResponse(string Message, IReadOnlyList<string> Details);`
- Password validator (`src/Verbara.Platform.Api/Services/PasswordService.cs:148`):
  - `public static PasswordValidationResult ValidatePolicy(string password, TenantAuthConfig config)` → `PasswordValidationResult(bool IsValid, IReadOnlyList<string> Errors)`.
  - `TenantAuthConfig` defaults: `PasswordMinLength=12`, `PasswordRequireUppercase=true`, `PasswordRequireNumber=true`, `PasswordRequireSpecial=false`.
- `Tenant` (from `Verbara.Sdk.Pro.MultiTenant`): `{ TenantId, Name, Status, Type, ParentTenantId, Options, Metadata, CreatedAt, UpdatedAt }`. `TenantOptions { MaxConcurrentChannels, MaxActiveCampaigns }`.
- `User` (`src/Verbara.Platform.Identity/Identity/User.cs`): `{ UserId, TenantId, Email, DisplayName, Role, Status, PasswordHash, CreatedAt }`.
- Helpers used by the existing handler: `EntityId.New()`, `new TenantId(string)`, `UserRole.Admin`, `UserStatus.Active`, `TenantStatus.Active`, `TenantType.{Platform,Customer}`, `PasswordService.HashPassword(string)`.
- Role-clone helpers: `tenantRoleStore.CloneFromTemplateAsync(TenantId tenantId, string roleId, string templateId, string name, string? description, CancellationToken ct)` and `userRoleStore.AssignAsync(TenantId, EntityId userId, string roleId, string? assignedBy, CancellationToken ct)`. Valid template ids include `"admin"` ("Full administrative access except cluster and auth configuration").
- Test factory: `PlatformApiFactory.GetService<T>()` resolves a service (e.g. `ITenantStore`, `IUserStore`) from the running host for assertions. `PlatformAdminApiFactory` already has a host tenant (used for the 409 test).

---

## Task 1: Extend the setup contract (records)

**Files:**
- Modify: `src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs` (the `SetupRequest` and `SetupResponse` records at the bottom of the file)

- [ ] **Step 1: Update the `SetupRequest` record**

Replace the existing record:

```csharp
internal sealed record SetupRequest(
    string Email,
    string Password,
    string? DisplayName,
    string? PlatformName,
    string CustomerTenantId,
    string CustomerName,
    string CustomerAdminEmail,
    string CustomerAdminPassword,
    string? CustomerAdminDisplayName);
```

- [ ] **Step 2: Update the `SetupResponse` record**

Replace the existing record:

```csharp
internal sealed record SetupResponse(
    string TenantId,
    string UserId,
    string AccessToken,
    string ManagementApiKey,
    string CustomerTenantId,
    string CustomerUserId);
```

- [ ] **Step 3: Build to verify the contract compiles (handler will be updated in Task 3)**

Run: `dotnet build src/Verbara.Platform.Api -c Release 2>&1 | tail -5`
Expected: FAIL — `Setup` handler's `return Results.Created(...)` no longer matches the 6-arg `SetupResponse`. This is expected; Task 3 fixes the handler. (Do not commit yet.)

---

## Task 2: Write the failing tests (TDD red)

**Files:**
- Modify: `tests/Verbara.Platform.Api.Tests/SetupEndpointTests.cs`

- [ ] **Step 1: Replace the test file with updated + new tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

public sealed class SetupEndpointTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public SetupEndpointTests(PlatformApiFactory factory) => _factory = factory;

    private static object ValidBody() => new
    {
        email = "admin@setup-test.com",
        password = "PlatformPass2026!",
        displayName = "Platform Admin",
        platformName = "Test Platform",
        customerTenantId = "acme",
        customerName = "Acme Corp",
        customerAdminEmail = "ops@acme.com",
        customerAdminPassword = "CustomerPass2026!",
        customerAdminDisplayName = "Acme Admin",
    };

    private sealed record SetupResponseDto(
        string TenantId,
        string UserId,
        string AccessToken,
        string ManagementApiKey,
        string CustomerTenantId,
        string CustomerUserId);

    [Fact]
    public async Task Setup_ShouldCreateBothTenantsAndAdmins_WhenValid()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SetupResponseDto>();
        body.Should().NotBeNull();
        body!.TenantId.Should().Be("platform");
        body.CustomerTenantId.Should().Be("acme");
        body.UserId.Should().NotBeNullOrEmpty();
        body.CustomerUserId.Should().NotBeNullOrEmpty();
        body.AccessToken.Should().NotBeNullOrEmpty();
        body.ManagementApiKey.Should().StartWith("mgmt_");

        var tenantStore = factory.GetService<ITenantStore>();
        var platform = await tenantStore.GetAsync("platform", default);
        platform.Should().NotBeNull();
        platform!.Type.Should().Be(TenantType.Platform);

        var customer = await tenantStore.GetAsync("acme", default);
        customer.Should().NotBeNull();
        customer!.Type.Should().Be(TenantType.Customer);
        customer.ParentTenantId.Should().Be("platform");

        var userStore = factory.GetService<IUserStore>();
        var platformAdmin = await userStore.GetByEmailAsync(new TenantId("platform"), "admin@setup-test.com", default);
        platformAdmin.Should().NotBeNull();
        var customerAdmin = await userStore.GetByEmailAsync(new TenantId("acme"), "ops@acme.com", default);
        customerAdmin.Should().NotBeNull();
    }

    [Fact]
    public async Task Setup_ShouldReturn409_WhenHostTenantAlreadyExists()
    {
        using var factory = new PlatformAdminApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenEmailMissing()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "",
            password = "PlatformPass2026!",
            customerTenantId = "acme",
            customerName = "Acme Corp",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenCustomerNameMissing()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "PlatformPass2026!",
            customerTenantId = "acme",
            customerName = "",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenCustomerTenantIdIsPlatform()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "PlatformPass2026!",
            customerTenantId = "platform",
            customerName = "Acme Corp",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenEmailsMatch()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "same@acme.com",
            password = "PlatformPass2026!",
            customerTenantId = "acme",
            customerName = "Acme Corp",
            customerAdminEmail = "same@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenPasswordBelowPolicy()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "short1A",
            customerTenantId = "acme",
            customerName = "Acme Corp",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Verify `IUserStore.GetByEmailAsync` signature matches the test**

Run: `grep -n "GetByEmailAsync" src/Verbara.Platform.Identity/IUserStore.cs`
Expected: a method `GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct)`. If the actual signature differs (e.g. takes a `string` tenantId or different order), adjust the two `GetByEmailAsync` calls in Step 1 accordingly before proceeding.

- [ ] **Step 3: Run the tests to confirm they fail (red)**

Run: `dotnet test tests/Verbara.Platform.Api.Tests --filter "FullyQualifiedName~SetupEndpointTests" 2>&1 | tail -15`
Expected: build/test FAIL (handler still returns 4-arg response / doesn't create Customer). This is the TDD red state.

---

## Task 3: Implement the handler (TDD green)

**Files:**
- Modify: `src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs` (the `Setup` method)

- [ ] **Step 1: Add validation block after the existing email/password check**

Find the existing block (around line 36):

```csharp
        // Validate input
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new ErrorResponse("Email and password are required."));
```

Replace it with:

```csharp
        // Validate platform admin input
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new ErrorResponse("Email and password are required."));

        // Validate customer input (hard requirement — every install is Platform + Customer)
        if (string.IsNullOrWhiteSpace(body.CustomerTenantId)
            || string.IsNullOrWhiteSpace(body.CustomerName)
            || string.IsNullOrWhiteSpace(body.CustomerAdminEmail)
            || string.IsNullOrWhiteSpace(body.CustomerAdminPassword))
            return Results.BadRequest(new ErrorResponse(
                "Customer tenant id, name, admin email and admin password are required."));

        // Customer tenant id must be a valid slug and must not collide with the host tenant
        var customerTenantIdNormalized = body.CustomerTenantId.Trim().ToLowerInvariant();
        if (!IsValidSlug(customerTenantIdNormalized) || customerTenantIdNormalized == "platform")
            return Results.BadRequest(new ErrorResponse(
                "Customer tenant id must be a lowercase slug (letters, digits, hyphens) and cannot be 'platform'."));

        // The two admins are distinct identities — different emails required
        if (string.Equals(body.Email.Trim(), body.CustomerAdminEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse(
                "Platform admin and customer admin must use different emails."));

        // Enforce the password policy on BOTH passwords (platform defaults: min 12, upper, number)
        var policyConfig = new TenantAuthConfig { TenantId = "platform" };
        var platformPwdCheck = PasswordService.ValidatePolicy(body.Password, policyConfig);
        if (!platformPwdCheck.IsValid)
            return Results.BadRequest(new ErrorDetailResponse(
                "Platform admin password does not meet policy", platformPwdCheck.Errors));
        var customerPwdCheck = PasswordService.ValidatePolicy(body.CustomerAdminPassword, policyConfig);
        if (!customerPwdCheck.IsValid)
            return Results.BadRequest(new ErrorDetailResponse(
                "Customer admin password does not meet policy", customerPwdCheck.Errors));
```

- [ ] **Step 2: Add the Customer-creation block after the Management API Key step**

Find the JWT-generation step near the end:

```csharp
        // 5. Generate JWT for the new admin
        var (accessToken, _) = jwtTokenService.GenerateAccessToken(user);

        return Results.Created("/management/system/info", new SetupResponse(
            hostTenantId,
            userId.Value,
            accessToken,
            rawApiKey));
```

Replace it with:

```csharp
        // 5. Generate JWT for the new platform admin
        var (accessToken, _) = jwtTokenService.GenerateAccessToken(user);

        // 6. Create the operational Customer tenant (Type=Customer, parent=platform)
        var customerTenant = new Tenant
        {
            TenantId = customerTenantIdNormalized,
            Name = body.CustomerName,
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = hostTenantId,
            Options = new TenantOptions
            {
                MaxConcurrentChannels = 100,
                MaxActiveCampaigns = 10,
            },
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await tenantStore.UpsertAsync(customerTenant, ct);

        // 7. Create the Customer admin user (lives inside the Customer tenant)
        var customerTenantId = new TenantId(customerTenantIdNormalized);
        var customerUserId = EntityId.New();
        var customerAdmin = new User
        {
            UserId = customerUserId,
            TenantId = customerTenantId,
            Email = body.CustomerAdminEmail,
            DisplayName = body.CustomerAdminDisplayName ?? "Administrator",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            PasswordHash = PasswordService.HashPassword(body.CustomerAdminPassword),
            CreatedAt = clock.UtcNow,
        };
        await userStore.SaveAsync(customerAdmin, ct);

        // 7.5. Best-effort: clone the tenant `admin` role template for the Customer admin.
        //      Same tolerance as the platform admin RBAC wiring — UserRole.Admin
        //      fallback still grants day-1 tenant admin perms if the store is partial.
        try
        {
            var customerAdminRoleId = $"admin-{customerTenantIdNormalized}";
            await tenantRoleStore.CloneFromTemplateAsync(
                customerTenantId,
                customerAdminRoleId,
                templateId: "admin",
                name: "Admin",
                description: "Full administrative access except cluster and auth configuration",
                ct);
            await userRoleStore.AssignAsync(
                customerTenantId, customerUserId, customerAdminRoleId, assignedBy: null, ct);
        }
        catch
        {
            // Tolerated — UserRole.Admin fallback grants day-1 tenant admin perms.
        }

        return Results.Created("/management/system/info", new SetupResponse(
            hostTenantId,
            userId.Value,
            accessToken,
            rawApiKey,
            customerTenantIdNormalized,
            customerUserId.Value));
```

- [ ] **Step 3: Add the `IsValidSlug` private helper inside `SetupEndpoints`**

Add this method to the `SetupEndpoints` class (e.g. just below the `Setup` method, before the records):

```csharp
    private static bool IsValidSlug(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63)
            return false;
        foreach (var c in value)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' || c == '-'))
                return false;
        }
        return value[0] != '-' && value[^1] != '-';
    }
```

- [ ] **Step 4: Add the `TenantAuthConfig` using directive if missing**

Run: `grep -n "using Verbara.Platform.Identity;" src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs`
Expected: present (the file already uses `Verbara.Platform.Identity`). `TenantAuthConfig` lives in `Verbara.Platform.Identity`, so no new using is needed. If the build later reports `TenantAuthConfig` not found, add `using Verbara.Platform.Identity;`.

- [ ] **Step 5: Build**

Run: `dotnet build src/Verbara.Platform.Api -c Release 2>&1 | tail -5`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Run the setup tests (green)**

Run: `dotnet test tests/Verbara.Platform.Api.Tests --filter "FullyQualifiedName~SetupEndpointTests" 2>&1 | tail -10`
Expected: all 7 tests PASS.

- [ ] **Step 7: Run the full Api.Tests suite (no regressions)**

Run: `dotnet test tests/Verbara.Platform.Api.Tests -c Release 2>&1 | tail -5`
Expected: PASS (1017 → ~1023, 0 failed). If any other test posted to `/api/setup` with the old 4-field body, update it to include the Customer fields (search: `grep -rln '"/api/setup"\|/api/setup' tests/`).

- [ ] **Step 8: Commit**

```bash
git add src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs tests/Verbara.Platform.Api.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): create Customer tenant + admin in /api/setup with policy + distinct-email validation"
```

---

## Task 4: Frontend — extend the API hook types

**Files:**
- Modify: `Verbara.Platform.Web/src/core/api/hooks/use-system.ts`

- [ ] **Step 1: Update the `SetupInput` interface**

Find (around line 123):

```typescript
export interface SetupInput {
  email: string;
  password: string;
  displayName?: string;
  platformName?: string;
}
```

Replace with:

```typescript
export interface SetupInput {
  email: string;
  password: string;
  displayName?: string;
  platformName?: string;
  customerTenantId: string;
  customerName: string;
  customerAdminEmail: string;
  customerAdminPassword: string;
  customerAdminDisplayName?: string;
}
```

- [ ] **Step 2: Update the `SetupResponse` interface**

Find the `SetupResponse` interface (around line 130) and ensure it includes the new fields:

```typescript
export interface SetupResponse {
  tenantId: string;
  userId: string;
  accessToken: string;
  managementApiKey: string;
  customerTenantId: string;
  customerUserId: string;
}
```

- [ ] **Step 3: Type-check**

Run: `cd Verbara.Platform.Web && npx tsc --noEmit 2>&1 | tail -10`
Expected: errors only in `setup-page.tsx` (it still references the old schema) — those are fixed in Task 5. No errors in `use-system.ts`.

---

## Task 5: Frontend — i18n keys in all 3 locales

**Files:**
- Modify: `Verbara.Platform.Web/public/locales/en-US/common.json`
- Modify: `Verbara.Platform.Web/public/locales/es-419/common.json`
- Modify: `Verbara.Platform.Web/public/locales/pt-BR/common.json`

- [ ] **Step 1: Add the `setupPage` block to `en-US/common.json`**

Add a top-level `"setupPage"` key (sibling to existing top-level keys; pick a stable alphabetical-ish location and ensure valid JSON):

```json
  "setupPage": {
    "title": "Platform Setup",
    "subtitle": "Configure your contact center platform",
    "platformAdminLegend": "Platform Admin Account",
    "email": "Email",
    "password": "Password",
    "displayName": "Display Name",
    "platformLegend": "Platform",
    "platformName": "Platform Name",
    "platformNamePlaceholder": "My Contact Center",
    "customerLegend": "Your Company (Customer)",
    "customerName": "Company Name",
    "customerTenantId": "Tenant Id",
    "customerTenantIdPlaceholder": "acme",
    "customerAdminEmail": "Company Admin Email",
    "customerAdminPassword": "Company Admin Password",
    "submit": "Initialize Platform",
    "submitting": "Initializing...",
    "genericError": "Setup failed. The platform may already be configured.",
    "apiKeyTitle": "Management API Key",
    "apiKeyWarning": "Save this key now — it cannot be retrieved again.",
    "apiKeyDone": "I've saved my key — Continue",
    "successToast": "Platform initialized successfully",
    "alreadyConfigured": "Already configured?",
    "signIn": "Sign in",
    "validation": {
      "emailInvalid": "Please enter a valid email address",
      "passwordPolicy": "Password must be at least 12 characters with an uppercase letter and a number",
      "required": "This field is required",
      "tenantIdSlug": "Use lowercase letters, digits and hyphens only",
      "emailsMustDiffer": "Company admin email must differ from the platform admin email"
    }
  }
```

- [ ] **Step 2: Add the same block to `es-419/common.json` (translated)**

```json
  "setupPage": {
    "title": "Configuración de la Plataforma",
    "subtitle": "Configurá tu plataforma de contact center",
    "platformAdminLegend": "Cuenta de Administrador de Plataforma",
    "email": "Email",
    "password": "Contraseña",
    "displayName": "Nombre para mostrar",
    "platformLegend": "Plataforma",
    "platformName": "Nombre de la Plataforma",
    "platformNamePlaceholder": "Mi Contact Center",
    "customerLegend": "Tu Empresa (Customer)",
    "customerName": "Nombre de la Empresa",
    "customerTenantId": "Identificador (Tenant Id)",
    "customerTenantIdPlaceholder": "acme",
    "customerAdminEmail": "Email del Administrador de la Empresa",
    "customerAdminPassword": "Contraseña del Administrador de la Empresa",
    "submit": "Inicializar Plataforma",
    "submitting": "Inicializando...",
    "genericError": "Falló la configuración. La plataforma puede que ya esté configurada.",
    "apiKeyTitle": "Clave de API de Gestión",
    "apiKeyWarning": "Guardá esta clave ahora — no se puede recuperar después.",
    "apiKeyDone": "Guardé mi clave — Continuar",
    "successToast": "Plataforma inicializada correctamente",
    "alreadyConfigured": "¿Ya está configurada?",
    "signIn": "Iniciar sesión",
    "validation": {
      "emailInvalid": "Ingresá un email válido",
      "passwordPolicy": "La contraseña debe tener al menos 12 caracteres, una mayúscula y un número",
      "required": "Este campo es obligatorio",
      "tenantIdSlug": "Usá solo minúsculas, dígitos y guiones",
      "emailsMustDiffer": "El email del admin de la empresa debe ser distinto al del admin de plataforma"
    }
  }
```

- [ ] **Step 3: Add the same block to `pt-BR/common.json` (translated)**

```json
  "setupPage": {
    "title": "Configuração da Plataforma",
    "subtitle": "Configure sua plataforma de contact center",
    "platformAdminLegend": "Conta de Administrador da Plataforma",
    "email": "Email",
    "password": "Senha",
    "displayName": "Nome de exibição",
    "platformLegend": "Plataforma",
    "platformName": "Nome da Plataforma",
    "platformNamePlaceholder": "Meu Contact Center",
    "customerLegend": "Sua Empresa (Customer)",
    "customerName": "Nome da Empresa",
    "customerTenantId": "Identificador (Tenant Id)",
    "customerTenantIdPlaceholder": "acme",
    "customerAdminEmail": "Email do Administrador da Empresa",
    "customerAdminPassword": "Senha do Administrador da Empresa",
    "submit": "Inicializar Plataforma",
    "submitting": "Inicializando...",
    "genericError": "Falha na configuração. A plataforma pode já estar configurada.",
    "apiKeyTitle": "Chave de API de Gerenciamento",
    "apiKeyWarning": "Salve esta chave agora — ela não pode ser recuperada depois.",
    "apiKeyDone": "Salvei minha chave — Continuar",
    "successToast": "Plataforma inicializada com sucesso",
    "alreadyConfigured": "Já está configurada?",
    "signIn": "Entrar",
    "validation": {
      "emailInvalid": "Insira um email válido",
      "passwordPolicy": "A senha deve ter pelo menos 12 caracteres, uma letra maiúscula e um número",
      "required": "Este campo é obrigatório",
      "tenantIdSlug": "Use apenas minúsculas, dígitos e hifens",
      "emailsMustDiffer": "O email do admin da empresa deve ser diferente do admin da plataforma"
    }
  }
```

- [ ] **Step 4: Validate JSON + i18n parity**

Run: `cd Verbara.Platform.Web && node -e "['en-US','es-419','pt-BR'].forEach(l=>{const j=require('./public/locales/'+l+'/common.json'); if(!j.setupPage) throw new Error('missing setupPage in '+l); console.log(l, Object.keys(j.setupPage).length, 'keys')})"`
Expected: each locale prints the same key count. If the repo has a dedicated i18n-parity script (`grep -n "i18n" package.json`), run that too.

---

## Task 6: Frontend — migrate `setup-page.tsx` to i18n + Customer fieldset

**Files:**
- Modify: `Verbara.Platform.Web/src/core/auth/setup-page.tsx`

- [ ] **Step 1: Add the i18n import and hook**

At the top of the file, add to the imports:

```typescript
import { useTranslation } from 'react-i18next';
```

Inside the component, after `const navigate = useNavigate();`, add:

```typescript
  const { t } = useTranslation('common');
```

- [ ] **Step 2: Replace the zod schema with policy-aligned + Customer fields + distinct-email rule**

Replace the existing `setupSchema`:

```typescript
const setupSchema = z
  .object({
    email: z.string().email('common:setupPage.validation.emailInvalid'),
    password: z
      .string()
      .min(12, 'common:setupPage.validation.passwordPolicy')
      .regex(/[A-Z]/, 'common:setupPage.validation.passwordPolicy')
      .regex(/[0-9]/, 'common:setupPage.validation.passwordPolicy'),
    displayName: z.string().optional(),
    platformName: z.string().optional(),
    customerName: z.string().min(1, 'common:setupPage.validation.required'),
    customerTenantId: z
      .string()
      .min(1, 'common:setupPage.validation.required')
      .regex(/^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/, 'common:setupPage.validation.tenantIdSlug'),
    customerAdminEmail: z.string().email('common:setupPage.validation.emailInvalid'),
    customerAdminPassword: z
      .string()
      .min(12, 'common:setupPage.validation.passwordPolicy')
      .regex(/[A-Z]/, 'common:setupPage.validation.passwordPolicy')
      .regex(/[0-9]/, 'common:setupPage.validation.passwordPolicy'),
    customerAdminDisplayName: z.string().optional(),
  })
  .refine((v) => v.email.toLowerCase() !== v.customerAdminEmail.toLowerCase(), {
    path: ['customerAdminEmail'],
    message: 'common:setupPage.validation.emailsMustDiffer',
  });
```

> Note: zod error messages here are i18n keys; render them with `t(errors.field?.message ?? '')`. The `FieldError` component receives the resolved string (see Step 4). Verify how existing i18n forms resolve zod messages — if `login-page.tsx` resolves differently (e.g. plain messages), match that pattern instead and keep messages as literal `t('...')` calls at render time.

- [ ] **Step 3: Update `defaultValues`**

Replace the `defaultValues`:

```typescript
    defaultValues: {
      email: '',
      password: '',
      displayName: '',
      platformName: '',
      customerName: '',
      customerTenantId: '',
      customerAdminEmail: '',
      customerAdminPassword: '',
      customerAdminDisplayName: '',
    },
```

- [ ] **Step 4: Replace the JSX text + add the Customer fieldset**

Replace every hardcoded string with `t('setupPage.<key>')` and add a third `<fieldset>` for the Customer. Concretely:
- `<h1>` → `{t('setupPage.title')}`; subtitle `<p>` → `{t('setupPage.subtitle')}`.
- First fieldset legend → `{t('setupPage.platformAdminLegend')}`; labels Email/Password/Display Name → `t('setupPage.email')` / `t('setupPage.password')` / `t('setupPage.displayName')`.
- Second fieldset legend → `{t('setupPage.platformLegend')}`; label → `t('setupPage.platformName')`; placeholder → `t('setupPage.platformNamePlaceholder')`.
- Add a third fieldset after the Platform one:

```tsx
          <fieldset className="space-y-4">
            <legend className="text-sm font-medium">{t('setupPage.customerLegend')}</legend>
            <div className="space-y-1.5">
              <Label htmlFor="customerName" required>
                {t('setupPage.customerName')}
              </Label>
              <Input
                id="customerName"
                data-testid="setup-customer-name"
                {...register('customerName')}
              />
              <FieldError id="customerName-error" message={t(errors.customerName?.message ?? '')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="customerTenantId" required>
                {t('setupPage.customerTenantId')}
              </Label>
              <Input
                id="customerTenantId"
                data-testid="setup-customer-tenant-id"
                placeholder={t('setupPage.customerTenantIdPlaceholder')}
                {...register('customerTenantId')}
              />
              <FieldError id="customerTenantId-error" message={t(errors.customerTenantId?.message ?? '')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="customerAdminEmail" required>
                {t('setupPage.customerAdminEmail')}
              </Label>
              <Input
                id="customerAdminEmail"
                type="email"
                data-testid="setup-customer-admin-email"
                {...register('customerAdminEmail')}
              />
              <FieldError id="customerAdminEmail-error" message={t(errors.customerAdminEmail?.message ?? '')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="customerAdminPassword" required>
                {t('setupPage.customerAdminPassword')}
              </Label>
              <Input
                id="customerAdminPassword"
                type="password"
                data-testid="setup-customer-admin-password"
                {...register('customerAdminPassword')}
              />
              <FieldError id="customerAdminPassword-error" message={t(errors.customerAdminPassword?.message ?? '')} />
            </div>
          </fieldset>
```

- Submit button → `{setup.isPending ? t('setupPage.submitting') : t('setupPage.submit')}`.
- Error `<p>` → `{setup.error?.message ?? t('setupPage.genericError')}`.
- Dialog title → `{t('setupPage.apiKeyTitle')}`; warning → `{t('setupPage.apiKeyWarning')}`; done button → `{t('setupPage.apiKeyDone')}`.
- `handleDone` toast → `toast.success(t('setupPage.successToast'))`.
- Footer "Already configured? Sign in" → `{t('setupPage.alreadyConfigured')}` + `{t('setupPage.signIn')}`.

> Keep all existing `data-testid` attributes (`setup-email`, `setup-password`, `setup-display-name`, `setup-platform-name`, `setup-submit`, `setup-error`, `api-key-dialog`, `api-key-value`, `api-key-done`) unchanged.

- [ ] **Step 5: Type-check + lint**

Run: `cd Verbara.Platform.Web && npx tsc --noEmit 2>&1 | tail -10 && npx eslint src/core/auth/setup-page.tsx 2>&1 | tail -10`
Expected: 0 errors.

---

## Task 7: Frontend — update the page test

**Files:**
- Modify: `Verbara.Platform.Web/src/core/auth/setup-page.test.tsx`

- [ ] **Step 1: Inspect the existing test to learn the render + mock pattern**

Run: `cat Verbara.Platform.Web/src/core/auth/setup-page.test.tsx`
Expected: shows how `SetupPage` is rendered (providers, i18n test wrapper) and how `useSetup` is mocked. Match this pattern.

- [ ] **Step 2: Update the test to fill the Customer fields and assert the payload**

Update the happy-path test so it fills the new fields by `data-testid` and asserts the mutation is called with the Customer fields. Example shape (adapt to the file's existing helpers/queries):

```typescript
  it('submits platform + customer fields', async () => {
    // ...render with i18n test provider + mocked useSetup mutate...
    fireEvent.change(screen.getByTestId('setup-email'), { target: { value: 'admin@x.com' } });
    fireEvent.change(screen.getByTestId('setup-password'), { target: { value: 'PlatformPass2026!' } });
    fireEvent.change(screen.getByTestId('setup-customer-name'), { target: { value: 'Acme Corp' } });
    fireEvent.change(screen.getByTestId('setup-customer-tenant-id'), { target: { value: 'acme' } });
    fireEvent.change(screen.getByTestId('setup-customer-admin-email'), { target: { value: 'ops@acme.com' } });
    fireEvent.change(screen.getByTestId('setup-customer-admin-password'), { target: { value: 'CustomerPass2026!' } });
    fireEvent.click(screen.getByTestId('setup-submit'));

    await waitFor(() => expect(mutateMock).toHaveBeenCalled());
    expect(mutateMock).toHaveBeenCalledWith(
      expect.objectContaining({
        email: 'admin@x.com',
        customerTenantId: 'acme',
        customerName: 'Acme Corp',
        customerAdminEmail: 'ops@acme.com',
        customerAdminPassword: 'CustomerPass2026!',
      }),
      expect.anything(),
    );
  });
```

- [ ] **Step 3: Run the test**

Run: `cd Verbara.Platform.Web && npx vitest run src/core/auth/setup-page.test.tsx 2>&1 | tail -15`
Expected: PASS.

- [ ] **Step 4: Commit the frontend**

```bash
cd Verbara.Platform.Web
git add src/core/api/hooks/use-system.ts src/core/auth/setup-page.tsx src/core/auth/setup-page.test.tsx public/locales/en-US/common.json public/locales/es-419/common.json public/locales/pt-BR/common.json
git commit -m "feat(setup): collect Customer tenant + admin in setup wizard (i18n EN/ES/PT)"
```

---

## Task 8: Docs — SMB manual

**Files:**
- Modify: `Verbara.Platform/docs/manuales/smb/03-setup-inicial.md`

- [ ] **Step 1: Read the current setup-wizard section**

Run: `sed -n '1,80p' docs/manuales/smb/03-setup-inicial.md`
Expected: shows the steps the operator follows on the setup page (Admin account + Platform name).

- [ ] **Step 2: Add a "Datos de tu empresa (Customer)" subsection**

Add a subsection (in Spanish, matching the manual's tone) documenting that the setup now also asks for: nombre de la empresa, identificador (tenant id — minúsculas/dígitos/guiones, no puede ser `platform`), email y contraseña del administrador de la empresa (distinto al de plataforma; contraseña ≥ 12 con mayúscula y número). Explain that this Customer is where agentes/colas/conversaciones live, and that the Platform admin is administrative-only (must impersonate the Customer or log in as the Customer admin to operate).

- [ ] **Step 3: Commit the docs**

```bash
git add docs/manuales/smb/03-setup-inicial.md
git commit -m "docs(smb): document Customer step in setup wizard manual"
```

---

## Task 9: Full verification

- [ ] **Step 1: Backend build + full suite**

Run: `dotnet build Verbara.Platform.slnx -c Release 2>&1 | tail -3 && dotnet test Verbara.Platform.slnx -c Release 2>&1 | tail -5`
Expected: 0 warnings, 0 failed tests.

- [ ] **Step 2: Frontend full check**

Run: `cd Verbara.Platform.Web && npx tsc --noEmit && npx vitest run 2>&1 | tail -5 && npx eslint . 2>&1 | tail -5`
Expected: 0 type errors, all vitest green, 0 eslint errors.

- [ ] **Step 3: Move the plan to completed**

```bash
cd Verbara.Platform
git mv docs/plans/active/2026-05-30-setup-multitenant-platform-customer.md docs/plans/completed/
git commit -m "docs(plan): mark setup multi-tenant plan as completed"
```
