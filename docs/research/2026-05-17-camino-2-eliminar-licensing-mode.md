# Camino 2 — Eliminar `LICENSING_MODE`, requerir license, dev-mode auto-detect

**Fecha:** 2026-05-17
**Status:** Research — propuesta arquitectural, no implementada
**Relacionado:**
- [Research previo (Camino 1)](2026-05-17-pro-image-binding-independence-from-licensing-mode.md) — image-binding al boot
- [Pro ADR-0011 — Image-Digest Binding](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md)
- [Platform ADR-0016 — Licencia + Rebrand](decisions/0016-license-and-rebrand-to-verbara.md) — Apache 2.0 + Pro EULA dual-license
- [Platform ADR-0018 — Visibility Decision](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md)
- verbara-website `functions/api/developer-license/index.ts` — issuer unattended

## TL;DR

**Camino 2 elimina el enum `EnforcementMode` (`Enforce` / `WarnOnly` / `Disabled`) y reemplaza el modelo de configuración por uno con UNA variable:**

```env
LICENSE_PATH=/etc/verbara/license.lic    # set → license loaded
LICENSE_PATH=                            # empty → Pro features return 501
```

**Regla única:** sin license → sin Pro (excepto dev mode local). La app SIEMPRE arranca; sólo las features Pro devuelven 501 cuando no hay license.

**Comparado con el research del Camino 1** (image-binding-independence sin tocar el enum), Camino 2:
- ✅ Cierra el mismo loophole técnico (`Disabled + imagen oficial = Pro free`).
- ✅ Limpia ~500 líneas de código del path `Disabled`/`WarnOnly`.
- ✅ Modelo mental único — "necesitás license para Pro, como cualquier servicio comercial".
- ✅ Alinea con el funnel real ya construido (website Tier 0.5 unattended issuer, <60s para obtener license).
- ⚠️ Requiere transition window (deprecation warning durante 1-2 minors Pro) para no romper clientes que usan `Disabled` hoy.
- ⚠️ "Dev mode permissive" sigue existiendo (sin él se rompe `dotnet run` local) — técnicamente es `Disabled` rebrandeado a `auto-detect`, pero el cambio de naming es el punto.

**El esfuerzo total estimado** (~5 días maintainer) es similar al Camino 1, pero el resultado es **un modelo arquitectural más limpio** en lugar de "agregar otra capa al stack existente". Camino 2 hace lo que Camino 1 hace + simplifica el modelo mental.

## 1. Problema del enum `LICENSING_MODE` actual

### 1.1 Confusión del naming

`Disabled` significa "**desactivado el chequeo** de licencia" — **NO** "desactivadas las features Pro". Los operators leen "Disabled" y piensan "esto desactiva Pro", cuando en realidad lo activa sin barreras.

Evidencia: durante el desarrollo del SMB reference deploy (2026-05-17 conversation), el maintainer mismo (que escribió el código) tuvo que aclarar el significado dos veces en formato dummy. **Si el creador necesita explicárselo a sí mismo, los clientes están perdidos.**

### 1.2 Tres valores, tres caminos divergentes en el código

```csharp
public enum EnforcementMode { Enforce, WarnOnly, Disabled }
```

Cada valor produce una rama distinta en:
- `LicenseRevalidationService.StartAsync` (early-exit en `Disabled`)
- `LicenseGateMiddleware.InvokeAsync` (skip en `Disabled`, log en `WarnOnly`, block en `Enforce`)
- `LicenseValidationHostedService.StartAsync` (validación obligatoria en `Enforce`, opcional en otros)
- `LicenseStatusTracker.UpdateStatus` (status semantics distintos por modo)

**Resultado:** ~500 líneas de código de branching + manejo de los 3 estados + ~150 líneas de WarnOnly path específico + ~24 tests de combinaciones (`Enforce + valid`, `Enforce + expired`, `WarnOnly + valid`, `WarnOnly + expired`, `Disabled + anything`, …).

### 1.3 `Disabled` es un loophole de revenue

Documentado exhaustivamente en el [research del Camino 1](2026-05-17-pro-image-binding-independence-from-licensing-mode.md) §2 como **TA-Disabled-Loophole**. Resumen: cliente pulla imagen oficial firmada + `LICENSING_MODE=Disabled` → todas las features Pro funcionan sin license, sin telemetry, sin payment-wall. **Sólo el EULA legal cubre este caso técnicamente.**

### 1.4 `WarnOnly` casi nadie lo usa

Verificación empírica: el path `WarnOnly` está documentado para "staging / migración" pero sin uso real conocido. La doc del enum lo describe como "intermedio entre Disabled y Enforce" sin guidance clara de cuándo usarlo. Es un valor que existe **por simetría conceptual**, no por necesidad operacional.

Si removemos `WarnOnly` + `Disabled` quedan sólo dos estados conceptuales reales:
- "Hay license → usá Pro"
- "No hay license → no usés Pro"

Que es justo lo que el operator espera.

## 2. La propuesta — modelo de configuración nuevo

### 2.1 Una sola variable de entorno

```env
# .env.reference-smb (Camino 2)
LICENSE_PATH=/etc/verbara/license.lic    # path al .lic — vacío = sin license
```

Elimina: `LICENSING_MODE=Disabled/WarnOnly/Enforce`.

### 2.2 Reglas del comportamiento

| Condición | Resultado |
|---|---|
| `LICENSE_PATH` apunta a `.lic` válido | Pro features cargan normal |
| `LICENSE_PATH` vacío en producción | Pro features registran como stubs (501 al usar); core sigue OK |
| `LICENSE_PATH` apunta a archivo inexistente / inválido / expirado | Pro features stubs (501) + warning al boot |
| Dev mode auto-detect (ver §2.3) | Pro features cargan normal sin importar `LICENSE_PATH` |
| Imagen tampered (image-binding del Camino 1) | Pro features stubs (501); image-binding tiene prioridad sobre license |

### 2.3 Dev mode auto-detect

```csharp
internal static class EnvironmentDetector
{
    public static bool IsDevMode()
    {
        var noContainerDigest = ContainerImageDigest.ReadFromEnvironment() is null;
        var dotnetEnvIsDev = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
        return noContainerDigest || dotnetEnvIsDev;
    }
}
```

**Trigger:** ausencia de `IMAGE_DIGEST` env **O** `ASPNETCORE_ENVIRONMENT=Development`. Cualquiera de las dos basta.

**Casos cubiertos:**
- `dotnet run` local de contributor → no hay IMAGE_DIGEST + Development → permissive ✓
- `docker compose up` con compose dev (`full.yml`) → si el operator no setea IMAGE_DIGEST → permissive ✓
- Producción con `docker-compose.reference-smb.yml` → IMAGE_DIGEST setteado por quickstart → NO permissive ✓
- Producción mal configurada (olvidó IMAGE_DIGEST) → permissive — riesgo, mitigación en §5.4

**Esto ES un `Disabled` rebrandeado a `auto-detect`.** El cambio es de naming + visibilidad, no técnico. Pero el naming es el punto:
- `Disabled` aparece en `.env` de producción → operator activamente lo escribió → mensaje "esto es soportado"
- Auto-detect dev mode es invisible → operator de producción nunca lo ve → no es una configuración deliberada

### 2.4 Comportamiento por escenario (matriz completa)

| Entorno | `LICENSE_PATH` | `IMAGE_DIGEST` | `ASPNETCORE_ENVIRONMENT` | Pro features |
|---|---|---|---|---|
| Dev local `dotnet run` | (cualquiera) | (no set) | `Development` | ✅ activo (dev mode) |
| Container dev / loadtest | (cualquiera) | (no set) | `Development` | ✅ activo (dev mode) |
| Producción + sin license | (vacío) | `sha256:...` | `Production` | 🚫 stubs 501 |
| Producción + license válida | `/path/to/lic` | `sha256:...` | `Production` | ✅ activo |
| Producción + license expirada | `/path/to/lic` (caducó) | `sha256:...` | `Production` | 🚫 stubs 501 + warning |
| Producción + license válida + imagen tampered | `/path/to/lic` | `sha256:WRONG` | `Production` | 🚫 stubs 501 (image-binding wins) |
| Producción **mal configurada** (olvidó digest) | (cualquiera) | (no set) | `Production` | ⚠️ activo (false-positive dev mode) |

El último caso es el único edge case nuevo — se mitiga en §5.4.

## 3. Cambios concretos en el código

### 3.1 Pro side — qué se borra

| Archivo | Acción |
|---|---|
| `Verbara.Sdk.Pro.Licensing/LicenseOptions.cs` | Borrar enum `EnforcementMode` + property `EnforcementMode` |
| `Verbara.Sdk.Pro.Licensing/LicenseRevalidationService.cs` | Borrar early-exit en `Disabled` |
| `Verbara.Sdk.Pro.Licensing/LicenseGateMiddleware.cs` | Simplificar a "license ok → pass / no license → 501" |
| `Verbara.Sdk.Pro.Licensing/LicenseValidationHostedService.cs` | Borrar branching por enum, single path |
| `tests/Verbara.Sdk.Pro.Licensing.Tests/EnforcementModeTests.cs` y similares | Borrar (~24 tests) |
| `Verbara.Sdk.Pro.Licensing/LicenseTier.cs` xml-doc | Quitar referencia "siempre WarnOnly para Developer" |

**Total:** -500 a -600 líneas de código.

### 3.2 Pro side — qué se agrega

`Verbara.Sdk.Pro.Licensing/EnvironmentDetector.cs` (nuevo, ~30 líneas):

```csharp
namespace Verbara.Sdk.Pro.Licensing;

internal static class EnvironmentDetector
{
    public static bool IsDevMode() =>
        ContainerImageDigest.ReadFromEnvironment() is null ||
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
}
```

`Verbara.Sdk.Pro.Licensing/ProFeatureStubs.cs` (nuevo, ~80 líneas):

```csharp
internal sealed class ProFeatureUnavailableStub<T> : T where T : class
{
    public ProFeatureUnavailableStub(string featureName, ILogger logger)
    {
        _featureName = featureName;
        _logger = logger;
        _logger.LogWarning(
            "Pro feature {Feature} called but no valid license is loaded. " +
            "Get a Developer license at https://verbara.io/developer-license",
            featureName);
    }
    // All interface methods throw ProFeatureUnavailableException
}
```

Helper para los `AddPro*()` extensions, en `Verbara.Sdk.Pro.Licensing.DependencyInjection`:

```csharp
public static IServiceCollection AddProFeatureGuarded<TService, TImplementation>(
    this IServiceCollection services,
    string featureName,
    Action<IServiceCollection> registerReal)
    where TService : class
    where TImplementation : class, TService
{
    if (LicenseStatusSnapshot.IsValid() || EnvironmentDetector.IsDevMode())
    {
        registerReal(services);
    }
    else
    {
        services.AddSingleton<TService, ProFeatureUnavailableStub<TService>>(...);
    }
    return services;
}
```

### 3.3 Pro side — qué se modifica

Los 14 `AddPro*()` extension methods identificados (Dialer, EventStore, AgentAssist, Analytics, CallAnalytics, Cluster, Realtime, MultiTenant, Routing, Push.SignalR, + 4 Storage.Postgres) se reescriben con el patrón:

```csharp
// Antes
public static IServiceCollection AddProDialer(this IServiceCollection services, Action<DialerOptions> configure)
{
    services.Configure(configure);
    services.AddSingleton<IDialerService, DialerService>();
    services.AddSingleton<ICampaignManager, CampaignManager>();
    // ... 20 more registrations
    return services;
}

// Después
public static IServiceCollection AddProDialer(this IServiceCollection services, Action<DialerOptions> configure)
{
    return services.AddProFeatureGuarded<IDialerService, DialerService>(
        featureName: "Dialer",
        registerReal: s =>
        {
            s.Configure(configure);
            s.AddSingleton<IDialerService, DialerService>();
            s.AddSingleton<ICampaignManager, CampaignManager>();
            // ... 20 more registrations (sin cambio)
        });
}
```

Pattern repetible, ~14 cambios mecánicos.

### 3.4 Platform side — qué se modifica

`Verbara.Platform.Api/Program.cs` (línea ~1231):

```csharp
// Antes
app.UseMiddleware<LicenseGateMiddleware>();

// Después
app.UseMiddleware<LicenseGateMiddleware>();  // sin cambio — el middleware se simplificó internamente
```

`Program.cs` config section que lee `LICENSING_MODE`:

```csharp
// Antes
builder.Services.AddProLicensing(o =>
{
    o.LicenseFilePath = licensePath;
    o.EnforcementMode = Enum.Parse<EnforcementMode>(
        builder.Configuration["Licensing:EnforcementMode"] ?? "Enforce");
});

// Después
builder.Services.AddProLicensing(o =>
{
    o.LicenseFilePath = licensePath;  // single setting
});
```

### 3.5 Compose files — `LICENSING_MODE` desaparece

`docker/docker-compose.reference-smb.yml`:

```diff
       # Licensing — Disabled = community/OSS; Enforce = require Pro license.
-      Licensing__EnforcementMode: ${LICENSING_MODE:-Disabled}
+      # Pro features require a valid license at LICENSE_PATH. Without one, Pro
+      # features return 501 but core features work normally. Get a free
+      # Developer license at https://verbara.io/developer-license
+      Licensing__LicenseFilePath: ${LICENSE_PATH:-}
```

`docker/.env.reference-smb.example`:

```diff
-LICENSING_MODE=Disabled
-IMAGE_DIGEST=
+# Path to your Verbara Pro license file (.lic). Leave empty to run without
+# Pro features (core features work without license). Free Developer license:
+# https://verbara.io/developer-license
+LICENSE_PATH=
+
+IMAGE_DIGEST=
```

Mismas modificaciones en:
- `docker/docker-compose.full.yml`
- `docker/demo/docker-compose.demo.yml`

## 4. Comportamiento que el cliente final ve

### 4.1 Cliente que no usa Pro (OSS path)

```bash
$ cat .env.reference-smb
LICENSE_PATH=

$ docker compose -f docker-compose.reference-smb.yml --env-file .env.reference-smb up -d
```

Logs:
```
[boot] Verbara.Platform.Api v2.4.0 starting...
[boot] warn: Verbara.Sdk.Pro.Licensing.LicenseManager
       LICENSE_PATH not configured. Pro features will be unavailable.
       Get a free Developer license at https://verbara.io/developer-license
[boot] info: Pro packages registered as stubs:
              Dialer, EventStore, CallAnalytics, AgentAssist, Cluster,
              MultiTenant, Realtime, Routing, Analytics
[boot] info: Now listening on http://[::]:5000
```

Cliente usa Web UI → WebChat funciona, Email funciona, Voz/SIP funciona. **Sólo si llama a `/api/v1/dialer/*` recibe 501:**

```
GET /api/v1/dialer/campaigns
HTTP/1.1 501 Not Implemented
Content-Type: application/json

{
  "error": "ProFeatureUnavailable",
  "feature": "Dialer",
  "message": "This feature requires a valid Verbara Pro license.",
  "documentation": "https://verbara.io/docs/licensing",
  "trial_signup": "https://verbara.io/developer-license"
}
```

### 4.2 Cliente con license

```bash
$ curl https://verbara.io/api/developer-license \
    -d '{"email":"cliente@empresa.com","turnstileToken":"..."}'
$ wget {link-from-email} -O /etc/verbara/license.lic

$ cat .env.reference-smb
LICENSE_PATH=/etc/verbara/license.lic
```

Logs:
```
[boot] info: License loaded: Tier=Developer, MaxAgents=5, Expires=2026-06-17
[boot] info: Pro features enabled: Dialer, EventStore, CallAnalytics, ...
```

Todo funciona normal. Cliente puede usar Dialer hasta 5 agents. Cuando intenta crear el 6º:
```
POST /api/v1/dialer/agents
HTTP/1.1 402 Payment Required
{ "error": "TierLimitReached", "currentTier": "Developer", "limit": 5, ... }
```

(Esto es comportamiento existente — los limits del tier ya están enforced por `LicenseGuard`.)

### 4.3 Dev contributor (Platform repo)

```bash
$ cd src/Verbara.Platform.Api
$ ASPNETCORE_ENVIRONMENT=Development dotnet run
```

Logs:
```
[boot] info: Development mode detected (no IMAGE_DIGEST, ASPNETCORE_ENVIRONMENT=Development)
[boot] info: Pro features enabled in dev mode (no license required)
[boot] info: Now listening on http://localhost:5000
```

Todo funciona. **Cero fricción para contributors.**

## 5. Trade-offs y edge cases

### 5.1 Compatibilidad con clientes existentes que usan `Disabled`

**Riesgo:** clientes corriendo `LICENSING_MODE=Disabled` con la imagen v2.1.0 hoy. Si en v2.4.0-pro removemos el enum, su `.env` queda con una variable que nada lee → comportamiento default es "sin license" → Pro features dejan de funcionar.

**Mitigación:** transition window de 2 minors Pro.
- **Pro v2.4.0-pro**: enum `EnforcementMode` deprecated, lee el valor pero logea warning a boot. Si `Disabled` o `WarnOnly` → comporta como si fuera "license OK" (preserva comportamiento anterior + emit deprecation warning a logs).
- **Pro v2.5.0-pro** (~6 semanas después): remove el enum completamente. Clientes que no actualizaron su `.env` en la transición → Pro features stubs 501. Mensaje en error response apunta a docs de migration.

Warning template:
```
[2026-06-15] warn: Verbara.Sdk.Pro.Licensing
  DEPRECATED: Licensing:EnforcementMode setting detected.
  This setting will be removed in Pro v2.5.0-pro (target: 2026-07-01).
  Migration: remove Licensing__EnforcementMode from your config and set
  Licensing__LicenseFilePath (or LICENSE_PATH env var) instead.
  Free Developer license: https://verbara.io/developer-license
  Docs: https://verbara.io/docs/migration/v2.4-licensing
```

### 5.2 Air-gap deployments

**Sin cambio respecto a Camino 1.** Cliente air-gap:
- Si usa Tier 1+ (license permanente) → genera license una vez, la mountan, funciona indefinidamente.
- Si usa Tier 0.5 Developer (expira 30 días) → necesita renovar mensual via website. Mismo problema que hoy.

**Para clientes core-only air-gap:** sin cambio. Sin `LICENSE_PATH` → Pro stubs → core funciona.

### 5.3 Kill-switch operacional rápido

**Hoy:** cliente con bug en Pro Dialer → cambia `LICENSING_MODE=Disabled` + restart → toda la app vuelve a OSS-only.

**Camino 2:** cliente con bug en Pro Dialer → cambia `LICENSE_PATH=` (vacío) + restart → Pro features stubs → mismo efecto.

**Equivalencia funcional preservada.** El kill-switch existe, sólo cambia el cómo se invoca.

### 5.4 Producción mal configurada (falsa detección de dev mode)

**Riesgo identificado en §2.4 matriz:** si operator productivo olvida setear `IMAGE_DIGEST` Y `ASPNETCORE_ENVIRONMENT` está en `Production`, el dev-mode auto-detect lo trata como dev → Pro features activan sin license.

**Análisis del riesgo:**
- En el SMB reference deploy actual, `quickstart-smb.sh` setea `IMAGE_DIGEST` automáticamente. Si el operator lo usa → no hay false-positive.
- En deploys manuales (sin quickstart) → riesgo real.

**Mitigaciones combinables:**

**Mitigación A** — `IsDevMode()` requiere AMBAS condiciones (no OR):
```csharp
public static bool IsDevMode() =>
    ContainerImageDigest.ReadFromEnvironment() is null &&
    string.Equals(env, "Development", ...);
```
Pero esto rompe `docker compose -f full.yml up` que no setea IMAGE_DIGEST pero corre con `ASPNETCORE_ENVIRONMENT=Production` (default de la imagen).

**Mitigación B** — boot-time validation explícita:
```csharp
if (env == "Production" && noImageDigest)
{
    logger.LogError("PRODUCTION misconfigured: ASPNETCORE_ENVIRONMENT=Production but no IMAGE_DIGEST. " +
                    "Pro features will not load. Set IMAGE_DIGEST to match your image manifest digest.");
    // Refuse Pro registration (treat as no-license)
}
```
Productivo con olvido → Pro stubs (no false-positive), error claro.

**Mitigación C** — Quickstart script + manual hard-recommend que `IMAGE_DIGEST` esté siempre setteado en producción.

**Recomendación:** combinación B + C. La mitigación A es muy estricta y rompe el dev compose.

### 5.5 Onboarding fricción (60s extra del trial form)

**Riesgo:** developer que prueba Verbara por primera vez. Hoy → 5 min con `Disabled`. Camino 2 → 5 min + 60s del trial signup.

**Mitigación:** integrar el trial signup AL quickstart-smb.sh:

```bash
$ bash scripts/quickstart-smb.sh

▶ 4/12  Verificando license Pro
  ℹ No se encontró license en /etc/verbara/license.lic
  ℹ Para usar features Pro (Dialer, AgentAssist, Cluster, EventStore, ...)
    necesitás una license. La versión Developer es gratis (30 días renovables).
  
  ¿Querés generar una license Developer ahora? [Y/n] _
  Email: _
  
  > Generando license... ✓
  > License guardada en /etc/verbara/license.lic
  > Pro features estarán disponibles tras el arranque.
```

Total fricción: 2 inputs (Y + email). Antes era 0 inputs con `Disabled`. **Sigue siendo bajo.**

Para los que prefieren auto-skip:
```bash
$ bash scripts/quickstart-smb.sh --no-license
# Procede sin license, Pro features = 501
```

### 5.6 Bot abuse del trial endpoint

**Riesgo:** sin `Disabled` como opción, el volumen del endpoint `/api/developer-license` se multiplica.

**Cálculo conservador:** si hoy 80% de los deploys usan `Disabled` y mañana 80% pide license → volumen 5×. Si volumen actual es ~100 licenses/mes → ~500/mes. Resend cobra ~$0.001/email → $0.50/mes en emails. Trivial.

**Si el growth se acelera a 10k/mes** (extremo) → $10/mes. Aún trivial. **No es un riesgo operacional real** dada la rate-limit ya existente (5/IP/24h + dedup email 30d + Turnstile).

### 5.7 Apache 2.0 + binario que requiere license

**Riesgo legal:** Apache 2.0 garantiza el derecho a correr el binario. Si la imagen oficial requiere license para Pro → ¿estamos restringiendo el derecho de uso del binario?

**Análisis:**
- El binario **arranca y opera** sin license — solo las features Pro se gate.
- Core features (WebChat, Email, Voz/SIP, RBAC, MultiTenant lite, etc.) **funcionan sin license**.
- Las features Pro son código adicional bundled — el cliente puede usar el binario sin tocarlas.
- **Esto NO viola Apache 2.0.** Es exactamente cómo Redis Stack (Apache 2.0 base + Source Available modules) o Elastic OSS distributions funcionan.

**Para reforzar la honestidad legal:** publicar también una imagen `verbara/platform/api-oss` sin los Pro NuGets bundled (Camino 3 del comparativo). **Pero esto es opt-in futuro**, no requerido para Camino 2.

## 6. Plan de ejecución

### Fase 1 — Pro v2.4.0-pro (transition: `Disabled`/`WarnOnly` deprecated)

| Tarea | Esfuerzo | Detalle |
|---|---|---|
| Add `EnvironmentDetector.IsDevMode()` | 1h | + 4 unit tests |
| Add `ProFeatureUnavailableStub<T>` + `ProFeatureUnavailableException` | 3h | Generic stub generator |
| Add `AddProFeatureGuarded<TService, TImpl>(featureName, registerReal)` helper | 2h | DI extension |
| Refactor 14 `AddPro*()` extensions para usar el helper | 8h | Mecánico repetitivo |
| Add deprecation warning logger en `LicensingServiceCollectionExtensions.AddProLicensing` cuando `EnforcementMode` está set | 1h | + test |
| Marcar enum `EnforcementMode` con `[Obsolete]` attribute | 30min | Compile-time deprecation |
| Actualizar XML docs de `LicenseOptions`, `LicenseTier` | 1h | Heads-up a v2.5.0 removal |
| Tests de transition (asegurar back-compat — `Disabled` sigue funcionando con warning) | 4h | 8-10 tests |
| Update `CHANGELOG-pro.md` con sección migration | 1h | |
| **Subtotal Fase 1** | **~21h** | **~3 días** |

### Fase 2 — Pro v2.5.0-pro (remove)

| Tarea | Esfuerzo | Detalle |
|---|---|---|
| Remove `EnforcementMode` enum + property | 30min | |
| Remove `LicenseRevalidationService` early-exit branch | 30min | |
| Simplify `LicenseGateMiddleware` (single path) | 1h | |
| Remove `WarnOnly` path completo | 2h | |
| Remove ~24 tests de Disabled/WarnOnly específicos | 1h | Mecánico |
| Add upgrade-warning hostservice — si encuentra `EnforcementMode` config refusa boot con mensaje migration | 2h | Safety net |
| Update Pro docs/manuales para reflejar single-variable model | 2h | |
| **Subtotal Fase 2** | **~9h** | **~1.5 días** |

### Fase 3 — Platform v2.4.0 (consumer-side cambios)

| Tarea | Esfuerzo | Detalle |
|---|---|---|
| Update `Program.cs` `AddProLicensing` call para no leer `EnforcementMode` | 30min | |
| Update `docker/docker-compose.reference-smb.yml` env vars | 30min | |
| Update `docker/.env.reference-smb.example` con `LICENSE_PATH` + comentarios | 1h | |
| Update `docker/docker-compose.full.yml` | 30min | |
| Update `docker/demo/docker-compose.demo.yml` | 30min | |
| Update `scripts/quickstart-smb.sh` con prompt de license opt-in | 2h | Section 5.5 mitigation |
| **Subtotal Fase 3** | **~5h** | **~0.5 día** |

### Fase 4 — Docs

| Tarea | Esfuerzo |
|---|---|
| Update `docs/manuales/smb/02-arranque-stack.md` § licensing | 1h |
| Update `docs/manuales/smb/03-setup-inicial.md` agregar paso opcional "get license" | 1h |
| Update `docs/manuales/smb/06-canal-voz-sip.md` (no debería mencionar LICENSING_MODE, verificar) | 30min |
| Update `docs/manuales/smb/99-troubleshooting.md` § Pro features 501 | 1h |
| Update `docs/manuales/smb/checklist-validacion-cliente.md` § licensing | 30min |
| Write Platform ADR-0020 documentando la decisión | 3h |
| Write Pro ADR-0012 (Pro-side decision record) | 2h |
| Migration guide en `docs/migration/v2.4-licensing.md` (nuevo) | 2h |
| **Subtotal Fase 4** | **~11h** | **~1.5 días** |

**Total proyecto:** **~46h** (~6 días maintainer time). Spread over 2 Pro minor releases (v2.4.0 transition + v2.5.0 cleanup).

## 7. Comparativa final — Camino 1 vs Camino 2 vs Camino 3

| Aspecto | Camino 1 (image-binding-independence) | **Camino 2 (eliminar enum)** | Camino 3 (dual image OSS/Pro) |
|---|---|---|---|
| **Cierra TA-Disabled-Loophole** | ✅ | ✅ | ✅ |
| **Simplifica modelo mental** | ❌ (sigue habiendo enum) | ✅ una variable | ✅ dos imágenes claras |
| **Líneas de código netas** | +200 | **-450** | +200 + duplica matrix |
| **Trabajo CI/CD adicional** | mínimo | mínimo | 2× build/test/sign |
| **Riesgo back-compat** | bajo (back-compat completa) | medio (transition 2 minors) | alto (cliente debe elegir imagen) |
| **DX local preservada** | ✅ ya hoy | ✅ con dev-mode auto-detect | ✅ usa imagen OSS local |
| **Air-gap viable** | ✅ | ✅ | ✅ (con OSS) |
| **Honest open-core narrativa** | medio (sigue ambiguo) | alto (license obligatoria es claro) | máximo (separación física) |
| **Esfuerzo maintainer** | ~35h | **~46h** | ~80h+ ongoing |
| **Riesgo de bugs introducidos** | bajo (agrega capa) | medio (refactor amplio) | bajo (separación clara) |
| **Mejora del funnel ventas** | bajo (no cambia el path) | alto (todos pasan por trial signup) | máximo (separación visible) |

**Camino 2 es el sweet spot:** mejor narrativa + funnel que Camino 1, mucho menos trabajo que Camino 3.

## 8. Decisión recomendada

**Ejecutar Camino 2** en el próximo release train Pro:

1. **Pro v2.4.0-pro** (~junio 2026): ship con dev-mode auto-detect + `ProFeatureUnavailableStub` + deprecation warning en `EnforcementMode`. Back-compat completa.
2. **Platform v2.4.0** (matching): consume Pro v2.4.0-pro, update compose templates + quickstart con license opt-in.
3. **Pro v2.5.0-pro** (~julio 2026, 6 semanas después): remove enum completamente. Clientes que no migraron reciben error claro a boot.

**Razones para preferir Camino 2 sobre el Camino 1 (research del 2026-05-17 mañana):**

1. **Mejor narrativa** — el modelo "necesitás license para Pro" es lo que los operators ESPERAN; el modelo "Disabled/WarnOnly/Enforce" requiere documentar 3 estados.
2. **Funnel real** — el website ya entrega licenses unattended en <60s; el modelo `Disabled` está saboteando ese funnel.
3. **Limpieza arquitectural** — -450 líneas netas reducen surface area de bugs y mantenimiento.
4. **Mismo loophole cerrado** — ambos caminos cierran TA-Disabled-Loophole; pero Camino 2 lo hace simplificando, no agregando.

**Razones para NO preferir Camino 3 (dual image):**

1. **Trabajo ongoing duplicado** — cada release necesita build/test/sign 2 imágenes; en un solo-maintainer setup es insostenible.
2. **Camino 2 entrega el 80% del beneficio** — la separación conceptual sigue siendo "OSS sin Pro vs Pro con license" pero implementada con UN binario y UNA variable.
3. **Camino 3 sigue siendo opcional futuro** — si en 2027 el growth lo justifica, se agrega `verbara/platform/api-oss` sin breaking changes adicionales.

## 9. Riesgos residuales no cubiertos por esta propuesta

1. **IL-edit del binario Pro para skipear el license check** — sigue siendo el "bypass class unchanged" documentado en ADR-0011. La única defensa es legal (EULA) + atribución (audit metrics emit even when license check fails).
2. **Cliente con setup auto-builds que rompe en v2.5.0** — depende de leer release notes; mitigación es la transition warning durante v2.4.0.
3. **False positive dev mode en producción** — cubierto por mitigación §5.4 (boot-time validation explícita).
4. **Si Verbara cambia el modelo de Tier 0.5 a paid en el futuro** — clientes que dependían del trial unattended se verían afectados. Pero esto es independiente de Camino 2; el modelo de tiers vive en `LicenseTier` enum.

## 10. Próximos pasos (si se aprueba)

1. Mover este research a `docs/plans/active/2026-06-XX-camino-2-execution-plan.md` cuando se decida la fecha.
2. Crear Pro ADR-0012 y Platform ADR-0020 documentando la decisión.
3. Crear `docs/migration/v2.4-licensing.md` con la guía paso a paso.
4. Ejecutar Fase 1 + Fase 3 en paralelo (Pro y Platform — Pro releases first, Platform consume).
5. Comunicar la transition en release notes de Pro v2.4.0-pro + Platform v2.4.0.
6. Re-evaluar 6 semanas post-Pro v2.4.0-pro → si métricas de adopción de license dev son sanas, proceder con Pro v2.5.0-pro y la remoción.

---

## Referencias

- [Research del Camino 1 (image-binding-independence)](2026-05-17-pro-image-binding-independence-from-licensing-mode.md)
- [Pro ADR-0011 — Image-Digest Binding](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md)
- [Platform ADR-0016 — Apache 2.0 + Pro EULA dual-license](decisions/0016-license-and-rebrand-to-verbara.md)
- `Verbara.Sdk.Pro.Licensing/LicenseOptions.cs` (current enum)
- `Verbara.Sdk.Pro.Licensing/LicenseGateMiddleware.cs` (current middleware)
- `Verbara.Platform/src/Verbara.Platform.Api/Program.cs:1231` (middleware registration)
- 14 `AddPro*()` extension methods (impacted by §3.3 refactor)
- `verbara-website/functions/api/developer-license/index.ts` (issuer unattended)
- `Verbara.Platform/docker/docker-compose.reference-smb.yml` (consumer-side env vars to update)
