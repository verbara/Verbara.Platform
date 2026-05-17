# Estudio profundo: independizar el image-binding de Pro de `LICENSING_MODE`

**Fecha:** 2026-05-17
**Autor:** Maintainer + investigación dirigida
**Status:** Research — propuesta arquitectural, no implementada todavía
**Relacionado:**
- [Pro ADR-0011 — Image-Digest Binding](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md)
- [Platform ADR-0018 — Visibility Decision](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) (Trigger 5 closure)
- Pro v2.3.0-pro (LicenseValidator + ContainerImageDigest shipped)
- verbara-website `functions/api/developer-license/index.ts` (unattended trial license issuance)

## TL;DR

**La hipótesis del usuario es correcta y abre una mejora arquitectural significativa.**

Tres hechos verificados durante esta investigación:

1. **Los paquetes `Verbara.Sdk.Pro.*` NO se publican públicamente en ningún feed NuGet.** El workflow `Verbara.Sdk.Pro/.github/workflows/release.yml` solo crea un GitHub Release tag — **no hace `nuget push` a ningún destino**. La única forma en que el código Pro llega al binario consumido por un cliente es a través del build interno de `ghcr.io/verbara/platform/api`, donde la carpeta local `local-nuget-feed/` queda hard-baked en la imagen final.

2. **La imagen `ghcr.io/verbara/platform/api` está firmada con cosign y publica su digest en `verbara-website/data/authorized-digests.json`.** Cualquier modificación al binario invalida la firma + cambia el digest.

3. **Hoy, el check de imagen (`ContainerImageDigest.ReadFromEnvironment` + `LicenseValidator.Validate`) corre dentro del pipeline de licencias, que `LICENSING_MODE=Disabled` desactiva completamente.** Esto significa que un cliente que pulla la imagen legítima firmada + setea `LICENSING_MODE=Disabled` obtiene **todas las features Pro** sin license y sin trigger de ningún check.

La consecuencia es que el modelo actual tiene una asimetría: el rigor de defensa contra forks/tampering (capa C de ADR-0011) sólo se activa cuando el cliente eligió pagar (`Enforce`), pero el caso de "cliente legítimo usando imagen firmada pero pirateando features Pro con `Disabled`" queda completamente cubierto sólo por el contrato legal (EULA) — sin barrera técnica.

**Esta propuesta:** mover el image-binding check al **boot de cada paquete Pro** (`AddProDialer`, `AddProEventStore`, etc.), **independiente de `LICENSING_MODE`**. Resultado:
- **`Disabled` + imagen oficial firmada** → Pro features siguen funcionando (try-before-buy honesto preservado).
- **`Disabled` + imagen modificada / fork-build** → Pro features se auto-rechazan al iniciar (cierra el loophole).
- **`Enforce` + license válido + imagen oficial** → flujo normal.
- **Dev (`dotnet run` local, sin IMAGE_DIGEST)** → permissive (preserva DX de desarrollo).

`LICENSING_MODE` queda con su semántica intacta: controla la **rigor del enforcement de licencia comercial**, no la **autenticidad del binario**.

---

## 1. Hallazgos del repo (estado actual real)

### 1.1 Distribución física de paquetes Pro

```
$ grep -E 'nuget push|gpr push|publish' Verbara.Sdk.Pro/.github/workflows/release.yml
# (sin matches — el workflow sólo hace `gh release create`)
```

`Verbara.Sdk.Pro/.github/workflows/release.yml` (líneas 1-60):
- Trigger: `workflow_run` después de CI green en main.
- Solo paso: `gh release create vX.Y.Z-pro --generate-notes`.
- **No publica .nupkg a nuget.org ni a `nuget.pkg.github.com/verbara/`** — el GitHub Release sólo contiene los release notes.

`Verbara.Platform/NuGet.Config`:
```xml
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
<add key="github" value="https://nuget.pkg.github.com/verbara/index.json" />
<add key="local" value="/media/Data/Source/Verbara/local-nuget-feed/" />
```

El package-source-mapping en ese mismo archivo manda `Verbara.Sdk.Pro.*` exclusivamente al feed `local`. Los .nupkg de Pro existen únicamente en la máquina del maintainer (~50 archivos en `local-nuget-feed/`) y dentro del Docker build context que produce la imagen oficial.

**Implicación operacional:** un tercero no tiene cómo obtener los .nupkg de Pro **legalmente** salvo descargando la imagen oficial y extrayendo los binarios compilados — operación que (a) requiere AOT-unpack, (b) viola el EULA, (c) deja el binario sin signature, (d) cambia el digest de la imagen.

### 1.2 Imagen oficial firmada

`ghcr.io/verbara/platform/api:v2.1.0`:
- Firmada con cosign (`sigstore/cosign-installer@v3`, cosign-release `v2.5.2`).
- Digest manifest-list publicado en `verbara-website/data/authorized-digests.json` (campo `manifest_list_digest`).
- Verificable por cualquier operador: `cosign verify --key cosign.pub ghcr.io/verbara/platform/api:v2.1.0`.

`Verbara.Platform/docker/docker-compose.verified.yml` documenta el patrón para pin-by-digest. El operador setea `IMAGE_DIGEST` env explícitamente; Pro lo lee vía `ContainerImageDigest.ReadFromEnvironment()`.

### 1.3 Pipeline de validación actual (Pro v2.3.0-pro)

`Verbara.Sdk.Pro.Licensing.LicenseValidator.Validate(...)`:

```
1. VerifySignature (ECDSA del LicenseTrustAnchor) → si falla: Invalid
2. Tier-features cross-check → si falla: Invalid
3. AuthorizedImageDigests vs ContainerImageDigest.ReadFromEnvironment() → si falla: UnauthorizedImage
4. Expiry / grace → si falla: Expired / GracePeriodActive
```

Y `LicenseRevalidationService.StartAsync`:

```csharp
if (_options.EnforcementMode == EnforcementMode.Disabled)
{
    LogRevalidationDisabled(_logger);
    return Task.CompletedTask;  // ← bail out total, no se revalida nada
}
```

`LicenseGateMiddleware` (consumido en `Verbara.Platform.Api/Program.cs:1231`):
- Si `EnforcementMode == Disabled` → request pasa sin ningún check.
- Si `Enforce` o `WarnOnly` → consulta `LicenseStatusTracker.LastResult` populado por `LicenseValidationHostedService` al inicio.

**Estado de los `AddPro*()` callsites** (extracto del grep en Program.cs):
```csharp
builder.Services.AddProLicensing(o => { o.LicenseFilePath = licensePath; });
builder.Services.AddProLicenseGuard();
builder.Services.AddProRetention();
// ...
builder.Services.AddProDialer(o => { });
builder.Services.AddProCallAnalytics();
builder.Services.AddProAgentAssist(...);
```

**Ningún `AddPro*()` consulta ContainerImageDigest ni hace check de imagen al registrarse.** Los servicios Pro se registran incondicionalmente. La barrera es 100% el middleware en el data-path.

### 1.4 Unattended license del website

`verbara-website/functions/api/developer-license/index.ts`:
- Endpoint `POST /api/developer-license` (Cloudflare Pages Function).
- Validaciones: Turnstile + rate-limit per-IP (5/24h) + dedup per-email (30 días) + Resend email.
- Genera Tier 0.5 Developer license (Features=511 = todos los bits ON, MaxAgents=5, MaxNodes=1, 30 días).
- **Embebe los últimos 6 `manifest_list_digest` de `data/authorized-digests.json` en `AuthorizedImageDigests`** del payload firmado.
- Firma ECDSA P-256 con la clave en `VERBARA_LICENSE_SIGNING_KEY` (Web Crypto API, server-side).

**Friction analysis:**
- Cualquier dev/cliente puede pedir una license de prueba en < 60s desde la website.
- License dura 30 días, renovable indefinidamente (1 por email per 30 días).
- Con esa license + `LICENSING_MODE=Enforce`, todas las features Pro funcionan en la imagen oficial.
- **No hay payment-wall ni gate humano** para el tier Developer.

**Conclusión sobre adopción:** el modelo "pague para usar Pro" en realidad ya es **"registre un email para usar Pro 30 días renovables indefinidamente"** para el tier Developer. El payment-wall está sólo para tiers 1+ (SelfHostStartup $5k/yr en adelante, con mayor capacidad — más agents/nodes/clusters).

---

## 2. Modelo de amenazas — qué cubre cada capa hoy

Stack defensivo actual de ADR-0011 (capas F + B + C):

| Capa | Mecanismo | Cubre | NO cubre |
|---|---|---|---|
| **F** | ECDSA `LicenseTrustAnchor` firma el payload de licencia | License forgery (atacante inventa una license válida) | Atacante usa license legítima en imagen modificada |
| **B** | Sigstore cosign firma la imagen OCI + admission policy (Helm Kyverno) | Imagen tampered en K8s con admission policy activa | Docker compose / bare-metal sin admission (la mayoría del expected first-12-24m customer base) |
| **C** | `LicenseValidator.AuthorizedImageDigests` vs `IMAGE_DIGEST` env | Customer paying corre imagen NO-oficial CON license válida → bloqueo | Customer NO-paying corre imagen oficial CON `LICENSING_MODE=Disabled` |

**Vector residual sin cobertura técnica (sólo legal vía EULA):**

> **TA-Disabled-Loophole:** Customer pulla `ghcr.io/verbara/platform/api:v2.1.0` (imagen oficial firmada, digest válido en authorized-digests.json), setea `LICENSING_MODE=Disabled` en el `.env`, y obtiene todas las features Pro (Dialer, EventStore Postgres, CallAnalytics, AgentAssist, Cluster, MultiTenant, Realtime, Routing) sin ningún rate-limit, sin telemetría que lo distinga del cliente OSS, sin payment-wall.

Este vector hoy se mitiga **solo legalmente** (Apache 2.0 del binario + EULA del Pro). Ningún check técnico se dispara.

---

## 3. La propuesta — independizar image-binding de `LICENSING_MODE`

### 3.1 Cambio conceptual

**Hoy** (acoplado):
```
LICENSING_MODE controls TODOS los checks (signature + image + expiry + tier)
   ├─ Disabled  → 0 checks
   ├─ WarnOnly  → all checks + log only
   └─ Enforce   → all checks + block on fail
```

**Propuesto** (desacoplado en 2 ejes):

```
Eje 1 — IMAGE AUTHENTICITY (siempre activo, no controlable por LICENSING_MODE)
   Check al init de cada paquete Pro:
     • IMAGE_DIGEST + Pro-embedded pubkey verification
     • IF dev (sin IMAGE_DIGEST) → permissive
     • IF official → allow Pro features to register
     • IF tampered/unknown → refuse Pro registration (returns 501 Not Implemented on use)

Eje 2 — LICENSE ENFORCEMENT (controlado por LICENSING_MODE, semantics actuales preservadas)
   ├─ Disabled  → no license check, no telemetry on license
   ├─ WarnOnly  → validate license + log warnings, features active
   └─ Enforce   → validate license + middleware blocks features without valid license
```

Los dos ejes son **independientes**: el cliente puede tener cualquier combinación.

### 3.2 Tabla de comportamiento resultante

| LICENSING_MODE | Imagen oficial + IMAGE_DIGEST OK | Imagen tampered / IMAGE_DIGEST mismatch | Dev mode (sin IMAGE_DIGEST) |
|---|---|---|---|
| `Disabled` | ✅ Pro activo, sin license | 🚫 Pro auto-disabled | ✅ Pro activo (dev permissive) |
| `WarnOnly` | ✅ Pro activo + warnings si license inválida | 🚫 Pro auto-disabled (image-binding wins) | ✅ Pro activo + warnings |
| `Enforce` | ✅ Pro activo SI license válida; 403 si no | 🚫 Pro auto-disabled | ✅ Pro activo (dev permissive) |

**El loophole queda cerrado:** modificar la imagen para skipear el license check no produce features Pro funcionando — el self-check de los paquetes Pro detecta el cambio de digest y se rehúsa a inicializar.

### 3.3 Mecánica técnica propuesta

#### Opción A — Pro embebe lista de digests known-good (rebuild-per-patch)

Pro v2.4.0-pro hardcodea en `LicensingDefaults` un array de digests autorizados:
```csharp
internal static readonly ImmutableArray<string> KnownGoodPlatformDigests = ImmutableArray.Create(
    "sha256:f82a9041dc7f...",   // v2.1.0
    "sha256:7378a9b2...",        // v2.1.1 (cuando salga)
    // ...
);
```

`ContainerImageDigest.IsKnownGood()` (new):
```csharp
public static bool IsKnownGood(string? digest) =>
    digest is null || KnownGoodPlatformDigests.Contains(digest);
```

Cada `AddPro*()` extension chequea esto en su DI registration:
```csharp
public static IServiceCollection AddProDialer(this IServiceCollection services, Action<DialerOptions> configure)
{
    if (!ContainerImageDigest.IsKnownGood(ContainerImageDigest.ReadFromEnvironment()))
    {
        // Refuse registration — register a stub that throws 501 on use
        services.AddSingleton<IDialerService, UnauthorizedImageDialerStub>();
        services.AddSingleton<UnauthorizedImageStartupWarning>();  // logs at startup
        return services;
    }
    // ... normal registration
}
```

**Ventajas:**
- 0 network calls (air-gap viable).
- Zero-config para el cliente.
- AOT-safe (sólo comparación de strings).

**Desventajas:**
- Cada release de Platform requiere un release de Pro para agregar el nuevo digest a la lista hardcoded.
- Cliente con Pro v2.4.0-pro + Platform v3.5.0 (lanzado después) falla — el digest de v3.5.0 no está en la lista.
- Acopla cadencia de release Pro ↔ Platform (que hoy son independientes).

#### Opción B — Pro embebe pubkey ECDSA + verifica firma del digest

Pro hardcodea sólo la **ECDSA pubkey** (la misma que firma las licenses Developer del website). El image entrypoint escribe `/etc/verbara-image-digest-signature` con una firma del digest hecha con la priv key.

`ContainerImageDigest.VerifySignedDigest()` (new):
```csharp
public static bool VerifySignedDigest(string? digest)
{
    if (digest is null) return true;  // dev mode
    var signature = File.ReadAllBytes("/etc/verbara-image-digest-signature");
    return ECDsaVerify(EmbeddedPublicKey, digest, signature);
}
```

**Ventajas:**
- Cada nueva imagen sólo necesita ser firmada con la priv key (que ya existe — la del LicenseTrustAnchor).
- Pro NO necesita re-release per-patch de Platform.
- AOT-safe (ECDsa.VerifyData es trim-safe en .NET 10).
- Cero network.
- La pubkey es la **misma** que ya está embebida en Pro para verificar licenses — zero new infra.

**Desventajas:**
- Requiere paso extra en el build de la imagen: firmar el digest con la priv ECDSA.
- Rotar la pubkey de Pro implica re-firmar todas las imágenes pasadas O un código de transición — pero rotar el LicenseTrustAnchor ya es así de doloroso, no se agrega gravedad.

#### Opción C — Pro lee + verifica firma de `authorized-digests.json` desde la imagen

Build de Platform incluye `authorized-digests.json` (con firma ECDSA) en `/etc/verbara/authorized-digests.json`. Pro al iniciar lee ese archivo, verifica la firma, y matchea IMAGE_DIGEST contra el array.

Es esencialmente Opción B con el array de digests en lugar de la firma del digest particular. Más útil si la pubkey ya viene del website y se actualiza on-the-fly.

**Ventaja sobre B:** un cliente puede actualizar `authorized-digests.json` montándolo via volume sin re-build de la imagen (útil para clientes que customizan la imagen — agregan certs, fonts, etc.).

**Desventaja:** un poco más de complejidad — file + JSON parse vs single signature verify.

### 3.4 Recomendación: **Opción B** (signed digest, embedded pubkey)

Razones:
1. **Reusa infra existente** — la pubkey ya está embebida en Pro para verificar licenses.
2. **Desacopla release cadence Pro/Platform** — no obliga a parchar Pro cada vez que sale un Platform patch.
3. **AOT-safe nativo** — `ECDsa.VerifyData` ya está probada en el path de license validation.
4. **0 network** — air-gap friendly (mismo principio que ADR-0011).
5. **Simple para customizers** — un cliente que monta certs custom en la imagen NO altera el `/etc/verbara-image-digest-signature` que viene del build oficial (a menos que destruya la firma reconstruyendo la imagen — que es exactamente el comportamiento deseado).

---

## 4. Impacto en la matriz `LICENSING_MODE`

### 4.1 Lo que NO cambia

- Los 3 valores del enum siguen siendo `Disabled` / `WarnOnly` / `Enforce`.
- La default sigue siendo `Enforce` para los tiers comerciales (1+) y `WarnOnly` para Developer.
- El `.env.reference-smb.example` actual recomienda `Disabled` para community/OSS — sigue válido para clientes OSS que NO usan ningún Pro feature.

### 4.2 Lo que SÍ cambia

#### Semántica de `LICENSING_MODE=Disabled` (la más impactada)

**Hoy:** "no validar nada de la licencia + permitir cualquier uso de features Pro".

**Con esta propuesta:** "no validar la licencia comercial + permitir usar features Pro **si y solo si** el binario es la imagen oficial firmada".

**Para el cliente OSS que NO necesita Pro:** sin cambios. Su `.env` con `Disabled` + sin license file sigue funcionando. Si NO llama a ninguna feature Pro, nunca se entera del check.

**Para el cliente que pirateaba con `Disabled` + imagen oficial:** sin cambios. Sigue funcionando — el check de image-binding pasa porque está usando la imagen legítima. **Esta cohorte es el target de revenue actual con `Enforce`, no cambia el negocio.**

**Para el atacante que modifica/forkea la imagen:** ahora rebota. Antes: `Disabled` + imagen modificada = Pro features OK. Después: `Disabled` + imagen modificada = Pro features auto-disabled.

#### Semántica nueva: `IMAGE_DIGEST` se vuelve casi-required en producción

Para SMB on-premise, el operator hoy puede dejar `IMAGE_DIGEST=` vacío (modo permissive). Con esta propuesta:
- **Si lo deja vacío** → `ContainerImageDigest.ReadFromEnvironment()` retorna null → modo dev → Pro features permitidas (back-compat preservada).
- **Si lo setea** → Pro verifica firma → debe matchear.

El `quickstart-smb.sh` puede leer el digest del manifest list y sugerir poner `IMAGE_DIGEST=sha256:...` automáticamente — convierte el "casi required" en "default automático" sin fricción.

#### Mensaje a los clientes/docs

El manual `docs/manuales/smb/02-arranque-stack.md` ya tiene la sección "verify firmas con cosign" antes del pull. Agregar: "si vas a usar features Pro en producción, también setea `IMAGE_DIGEST` en tu `.env` — el script `quickstart-smb.sh` lo hace automáticamente al detectar la imagen oficial".

`.env.reference-smb.example` cambia el comentario:
```diff
- # Disabled = community/OSS path. Pro features (...) require a Verbara Pro license.
- # For the community path: leave LICENSING_MODE=Disabled and IMAGE_DIGEST empty.
+ # Disabled = community/OSS path. Si NO vas a usar features Pro, IMAGE_DIGEST puede
+ # quedar vacío. Si querés usar features Pro (incluso en modo Disabled para evaluar),
+ # debés setear IMAGE_DIGEST al manifest digest de tu imagen — Pro lo valida al
+ # arrancar incluso en modo Disabled (image-binding es independiente del license-mode).
```

---

## 5. Edge cases y trade-offs

### 5.1 Dev experience (DX)

**Riesgo:** romper `dotnet run` local de Platform.Api.

**Mitigación:** la propuesta preserva el path dev permissive — si `IMAGE_DIGEST` no está seteado y `/etc/verbara-image-digest-signature` no existe, Pro asume modo dev y registra normalmente. Pasa el 100% de los unit tests existentes sin cambios.

**Validable:** los tests actuales de `ContainerImageDigestTests` (8 tests) ya cubren el path null → permissive. Los nuevos tests serían:
- `ImageBinding_ShouldRefuseRegistration_WhenSignatureInvalid`
- `ImageBinding_ShouldAllowRegistration_WhenSignatureValid`
- `ImageBinding_ShouldAllowRegistration_WhenDevModeNoDigest`

### 5.2 Helm chart / K8s

`infra/k8s/helm/platform/values.yaml` ya tiene `api.image.digest` que inyecta `IMAGE_DIGEST` env. Lo único nuevo: el image build debe escribir el archivo firmado dentro del container (`/etc/verbara-image-digest-signature`).

Si el operator K8s usa una imagen rebuilt-custom (e.g. añadió fonts corporativos via `Dockerfile FROM ghcr.io/verbara/platform/api`), la firma queda invalidada y Pro se rehúsa a inicializar. **Esto es deseado** — pero hay que documentarlo claramente en el manual K8s (Fase 2) para que no sorprenda.

**Mitigación para customers legítimos que rebuildean:** ofrecer un signing service (Pro Plus tier o más arriba) donde el customer manda el digest de su imagen modificada y Verbara lo firma. O — más simple — la imagen oficial soporta un volume mount `/etc/verbara-custom-pubkey` que reemplaza la pubkey embebida, permitiendo al customer firmar sus propios builds con su propia priv key (después de verificación legal/contractual).

### 5.3 Air-gap deployments

**Air-gap-friendly** porque la verificación es 100% local (firma + pubkey embebida + IMAGE_DIGEST env o archivo local).

Comparado con ADR-0011 original (que también es air-gap-friendly), esta extensión no añade dependencia de red.

### 5.4 Tampering del binario Pro

**Vector residual:** atacante hace IL-edit a la Pro DLL para skipear el image-binding check. Es el mismo vector que ADR-0011 ya documenta como "bypass class unchanged — EULA enforcement territory".

**Pero:** la atribución empeora para el atacante. Cuando el image-binding salta, los metrics OpenTelemetry (`verbara.licensing.image_unauthorized`) que ya existen en Pro v2.3.0-pro emiten el evento via la métrica del cliente. Si el atacante también patchea esa métrica, hace un cambio rastreable a más capas del binario.

Recomendación a futuro (no esta propuesta): publicar checksums SHA-256 de las DLLs Pro firmados con el mismo ECDSA — `LicenseValidationHostedService` los verifica al iniciar. Eleva el costo del IL-edit a "modificar y re-firmar". El atacante necesita la priv key, no la tiene, juego sobre.

### 5.5 Verbara-website unattended trial license

**Sin impacto.** El flow ya embebe `AuthorizedImageDigests` en el payload firmado. La nueva capa de image-binding al boot ES INDEPENDIENTE — usa otra ruta (firma del propio digest, no la license).

Posible mejora paralela: el endpoint del website puede también firmar el digest del cliente y entregárselo como archivo separado para mountar — útil si el cliente customiza la imagen y necesita el signing service mencionado en §5.2. Pero no es necesario para esta propuesta.

### 5.6 Rotación de la pubkey embebida

**Mismo problema que rotación del LicenseTrustAnchor.** Si la priv key se compromete, hay que:
1. Rotar el par.
2. Re-firmar todas las imágenes históricas con la nueva priv key.
3. Liberar Pro patch con la nueva pubkey embebida.
4. Clientes con Pro vieja no pueden validar imágenes con la firma nueva — hay que liberar transition con dual-keys que acepta ambos.

Esto NO se hace nunca a menos que haya breach. La pubkey actual ya cumple 0 rotaciones desde su generación 2026-05-10. Misma operational discipline.

---

## 6. Roadmap si se quiere ejecutar esto

### Fase 1 — Pro v2.4.0-pro: image-binding al boot

| Tarea | Esfuerzo | Notas |
|---|---|---|
| Embed ECDSA pubkey (la misma del LicenseTrustAnchor) en `ImageBindingDefaults` | 1h | Reusa la pubkey existente |
| Add `ContainerImageDigest.VerifySignedDigest(digest)` | 2h | ECDsa.VerifyData, AOT-safe |
| Add `UnauthorizedImageDigest{Service}Stub` por paquete (8 paquetes) | 4h | Throws InvalidOperationException con mensaje accionable |
| Update `AddPro*()` extensions para chequear al registrar (8 extensions) | 6h | Pattern repetible — quizás un helper `services.AddProFeatureGuarded(...)` |
| Tests | 6h | 24+ nuevos tests (3 por paquete × 8 paquetes) |
| ADR-0012 en Pro repo documentando el desacoplamiento | 2h | Append-only, no supersede ADR-0011 — lo extiende |
| **Total** | **~21h** | ~3 días de trabajo focused |

### Fase 2 — Platform: firmar digest al build

| Tarea | Esfuerzo | Notas |
|---|---|---|
| Update `.github/workflows/release.yml` para firmar el digest con la ECDSA priv key (después de cosign) | 3h | Reusa el secret `VERBARA_LICENSE_SIGNING_KEY` o uno separado |
| Update `Dockerfile` entrypoint para escribir `/etc/verbara-image-digest-signature` | 1h | Trivial — file write from env var injected at build |
| Update `quickstart-smb.sh` para auto-detect + setear `IMAGE_DIGEST` desde el manifest pull | 2h | Ya hace la verify cosign — agregar el extract digest + populate |
| Smoke test E2E: build una imagen tampered, validar que Pro se rehúsa a init | 2h | Add to release.yml validation matrix |
| **Total** | **~8h** | ~1 día |

### Fase 3 — Docs + manuales

| Tarea | Esfuerzo |
|---|---|
| Update `.env.reference-smb.example` comments | 30min |
| Update `docs/manuales/smb/02-arranque-stack.md` § verify section | 1h |
| Update `docs/manuales/smb/06-canal-voz-sip.md` § licensing | 30min |
| Update `docs/manuales/smb/99-troubleshooting.md` con nuevo error code "UnauthorizedImage at boot" | 1h |
| Update Platform `docs/decisions/0020-image-binding-independence-from-licensing-mode.md` (nuevo ADR) | 3h |
| **Total** | **~6h** |

**Esfuerzo total proyecto:** ~35h (~5 días). No es trivial pero tampoco enorme.

---

## 7. Implicancias de negocio

### 7.1 Pro becomes "actually Pro"

Hoy el `LICENSING_MODE=Disabled` deshace todo el revenue protection. La asunción implícita es "los clientes serios usan Enforce porque quieren soporte oficial". Esto es **cierto para Tier 1+ ($5k/yr+)** pero deja un grey zone con el self-hosted "voy a probarlo en producción sin pagar" que erosiona el funnel hacia el tier Developer auto-issued (que es la conversion path correcta).

Con esta propuesta:
- Cliente que prueba en producción → website → 30s para license Tier 0.5 → `Enforce` activo → metric `verbara.licensing.tier_developer_active` se emite → Verbara puede saber cuántos developer trials hay en producción real.
- Cliente que decide pirate con `Disabled` → ahora obtiene Pro features sólo si usa imagen oficial — sigue siendo gratis técnicamente pero ya entró al funnel medible.
- Cliente con imagen fork-from-source → Pro features OFF, debe usar el path OSS-only. Esto es exactamente el comportamiento que la dual-license Apache 2.0 + Pro EULA intenta lograr **pero hoy depende de honor system**.

### 7.2 Conversión Tier 0.5 → Tier 1+

El Tier 0.5 Developer con `Features=511` (todos los 9 bits ON) es prácticamente todas las features. La diferencia con Tier 1 ($5k/yr) es **capacidad**: MaxAgents=5 vs MaxAgents=25, MaxNodes=1 vs 1, etc.

Hoy: un cliente puede setear `Disabled` y tener `MaxAgents=∞`. Esto erosiona el upsell.

Con esta propuesta: para que el cliente use Pro debe estar en Developer al menos → si quiere subir agentes, debe upgradear a Tier 1+ → conversion path natural.

**Esta es la verdadera ganancia de negocio:** no es bloquear pirates (siempre habrá un atacante con tiempo de hacer IL-edit), es **hacer que el path normal sea pagar/registrar**, no `Disabled + ∞`.

---

## 8. Decisión recomendada

**Ejecutar la propuesta (Opción B — signed digest, embedded pubkey).** Razones:

1. **Cierra el TA-Disabled-Loophole técnico.** Hoy descansa 100% en EULA legal. Pasarse a EULA + técnica eleva el cost-of-attack y la atribución.
2. **Es relativamente barato** (~5 días maintainer time).
3. **No rompe DX** — dev mode (sin IMAGE_DIGEST) sigue permissive.
4. **Reusa infra existente** — la pubkey ECDSA, el ContainerImageDigest helper, los hostedservice patterns.
5. **Alinea incentivos** — clientes serios van al website por una license Developer; clientes que customizan la imagen entran al funnel de Tier 1+ (signing service / pubkey override).
6. **Es honesto** — el código de Pro queda público (al menos en su contrato), el mecanismo es auditable, no hay security-through-obscurity.
7. **Preserva el principio open-core** — el binario sigue ejecutándose para OSS users sin Pro; sólo las features Pro se gate.

**Trade-off principal:** ~5 días de maintainer time. **Riesgo principal:** un cliente legítimo que ya tenía un setup custom-builds y no contaba con la verificación. Mitigación: comunicar en release notes + transition period de 1-2 Pro minors antes de hacerlo enforced (versión Pro v2.4.0-pro lo agrega como WARN-only; v2.5.0-pro lo hace block).

### Plan de comunicación si se ejecuta

1. **Pro v2.4.0-pro** (~junio 2026): ship con image-binding al boot pero en modo `warn_only` — log un warning si la firma del digest falla, pero sigue registrando Pro normalmente. Esto da 1 minor de transición para que clientes con setups custom se enteren.
2. **Pro v2.5.0-pro** (~julio 2026): cambia a `enforce` — Pro auto-disable si la firma falla. Release notes destacando la fecha.
3. **Platform release matching** — `release.yml` actualizado a firmar el digest desde v2.4.0 onwards (sin cambio para los clientes con images < v2.4.0; el archivo `/etc/verbara-image-digest-signature` simplemente no existe y Pro lo trata como dev mode).

---

## 9. Conclusión

La hipótesis del usuario es correcta y arquitectónicamente importante. El estado actual tiene una asimetría: el image-binding (capa C de ADR-0011) sólo protege a clientes paying (Enforce mode), no a la cohorte que usa la imagen oficial con `Disabled` (que es el caso más común post-flip de visibility 2026-05-10).

Independizar el image-binding de `LICENSING_MODE` cierra el TA-Disabled-Loophole sin romper:
- DX local (`dotnet run` sigue funcionando).
- Air-gap deployments (cero network).
- Back-compat con clientes existentes (transition period 2 minors).
- El principio open-core de [ADR-0016](decisions/0016-license-and-rebrand-to-verbara.md) (el código sigue público y auditable).

`LICENSING_MODE` sigue existiendo y manteniendo su rol de **controlar la rigor del enforcement de licencia comercial**. Pasa de ser un kill-switch global de toda la defensa a ser el kill-switch específico del eje legal (license validation), dejando el eje técnico (image authenticity) siempre activo.

El esfuerzo estimado es razonable (~5 días) y el resultado convierte el grey zone "Pro sin pagar via Disabled" en un camino que requiere actively tampering la imagen oficial — algo que es legalmente más claro de perseguir y técnicamente más detectable.

**Recomiendo aprobar el camino y planificarlo en un siguiente train Pro v2.4.0-pro / Platform v2.2.0.**

---

## Referencias

- [Pro ADR-0011](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md) — el ADR base de image-binding
- [Pro research 2026-05-09](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/research/2026-05-09-pro-image-binding-research.md) — F+B+C threat model
- [Platform ADR-0016](decisions/0016-license-and-rebrand-to-verbara.md) — Apache 2.0 + Pro EULA dual-license
- [Platform ADR-0018](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) — Visibility flip
- `Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Licensing/ContainerImageDigest.cs` — helper ya shipped
- `Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Licensing/LicenseValidator.cs` — pipeline actual
- `verbara-website/functions/api/developer-license/index.ts` — issuer flow unattended
- `Verbara.Platform/.github/workflows/release.yml` — cosign signing
- `Verbara.Platform/docker/docker-compose.verified.yml` — IMAGE_DIGEST pinning template
