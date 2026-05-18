# Plan — Phase A: extract SignalR Hub into `Verbara.Platform.Realtime`

(Phase A of [ADR-0022](../../media/Data/Source/Verbara/Verbara.Platform/docs/decisions/0022-platform-api-aot-shipping-path.md))

## Context

El AOT-publish empírico ejecutado 2026-05-18 sobre `Verbara.Platform.Api` falló con 3 errores `IL3050` en `src/Verbara.Platform.Api/Services/PushToHubRelay.cs:163,179,195` — `IHubContext<THub, T>.Clients.get` está anotado `[RequiresDynamicCode]` (genera proxies de cliente en runtime). Mientras esa dependencia siga dentro del proceso de Platform.Api, el host no puede shipear como Native AOT, y la imagen pública seguirá distribuyendo 68 DLLs `Verbara.*` (incluyendo `Verbara.Sdk.Pro.*` cerrado/comercial) como IL decompilable — la fuga de IP catastrófica que motiva ADR-0022.

El requisito del maintainer es absoluto: *"esta imagen siempre debe ser AOT"*. Phase A extrae toda la superficie SignalR del `Verbara.Platform.Api` a un microservicio nuevo `Verbara.Platform.Realtime` (non-AOT — la imagen pública crítica es solo la del API). Phase B (EF Core DataProtection → Dapper) y Phase C (flip AOT + verify single binary) siguen después.

Constraint operacional reforzado por el maintainer 2026-05-18:
> *"api en k8s puede tener 1 a n pods. mirar si el servicio de realtime en k8s tambien puede tener 1 o n pods"*

→ **Realtime debe ser horizontalmente escalable** (1–N pods, HPA-capable). Esto añade requisitos arquitectónicos descritos en §4.

## Análisis IPC profundo (en respuesta al pedido del maintainer)

Pedido: identificar si HTTP+JSON / gRPC / RSocket eran realmente las únicas opciones al alcance.

### Shortlist completa evaluada

| # | Transporte | AOT-safe `.NET 10` | Madurez ecosistema | Operacional | Calzada en Verbara |
|---|---|---|---|---|---|
| 1 | **HTTP+JSON via IHttpClientFactory + System.Text.Json source-gen** | ✅ Total | 10/10 | Trivial | ✅ Renderer + Mail ya lo usan |
| 2 | **gRPC + Protobuf (Grpc.AspNetCore 2.60+ con AOT source-gen)** | ✅ Total en .NET 10 (verificación empírica pendiente para Platform.Api) | 9/10 | Medio (tooling .proto) | ⚠️ Cambio de patrón vs Renderer/Mail |
| 3 | **MagicOnion (Cysharp gRPC code-first con MessagePack)** | ⚠️ AOT-claim pero menos validado en ASP.NET hosting | 7/10 | Medio-alto | ❌ Tercer patrón |
| 4 | **HTTP + MessagePack** (mismo transporte, serializer binario) | ✅ Total (MessagePack source-gen) | 8/10 | Bajo | ❌ Inconsistente con Renderer/Mail |
| 5 | **NATS / NATS JetStream** (pub-sub broker con request-reply) | ✅ (NATS.Client.Core source-gen friendly) | 9/10 | Alto — broker adicional al stack | ❌ Operacional overhead grande |
| 6 | **RSocket over WebSocket** (Reactive Streams + backpressure) | ❌ probable bloqueo — rsocket-net reflection-heavy | 4/10 en .NET (community-maintained) | Muy alto — debug + ingress + observability | ❌ Ecosistema inmaduro |
| 7 | **Apache Kafka / Pulsar** | ✅ clientes maduros | 10/10 broker pero | Muy alto | ❌ Overkill total |
| 8 | **ZeroMQ (NetMQ)** | ✅ pero zero opinions sobre framing/auth | 8/10 | Alto — todo manual | ❌ Maintainer burden |
| 9 | **Raw WebSocket + JSON** (kestrel sin SignalR) | ✅ | 9/10 | Medio — sin abstracción request/reply | ❌ Reinventar gRPC |
| 10 | **HTTP/3 (QUIC) + JSON** | ✅ | 8/10 (servers maduran) | Medio | ❌ Optimización prematura |

### Patrón de tráfico real medido en el código

- **Realtime → Platform.Api**: `IAgentTenantResolver.GetAgentTenantAsync(agentId)` ~5–10 calls/sec por pod en steady-state (la mayoría cached 5 min en `CachedAgentTenantResolver`) + `IHubAuditSink.RecordAsync(...)` ~0.1 calls/sec fire-and-forget (solo cuando un usuario hace cross-tenant subscribe).
- **Platform.Api → Realtime**: cero. Los T27 bridges (`WithClusterEventBridge / WithConversationBridge / WithAgentBridge` en `Program.cs:133-135`) se mueven a Realtime; el `IPushEventBus` (Pro.Push, Redis-backed) cruza pods transparentemente.
- **Cliente browser → Realtime**: SignalR/WebSocket vía Cilium Gateway HTTPRoute (`/hubs/*` → `platform-realtime:5030`).

Conclusión: el boundary IPC inter-service maneja ~10 RPS sparse + req/resp + fire-and-forget. **El criterio decisivo es operacional, no de performance.**

### Decisión: HTTP+JSON via IHttpClientFactory

1. **Consistencia operacional**: Renderer (5010) y Mail (5020) ya usan exactamente este patrón — IHttpClientFactory named-client + X-Service-Key header + Results.Json + ResiliencePolicy keyed.
2. **AOT-cleanness empíricamente verificada**: Platform.Api ya usa System.Text.Json source-gen contexts (ApiJsonContext) sin warnings.
3. **Tooling cero-friction**: curl, browser DevTools, Wireshark, Prometheus scraping, OTEL W3C trace context auto-propagation — todo just works.
4. **Cilium Gateway / nginx ingress**: routing por path prefix nativo.
5. **gRPC sería defendible** pero re-abre incertidumbre AOT que Phase A trata de cerrar e introduce `Grpc.Tools` + `.proto` codegen.
6. **NATS sería elegante** para event flows pero introducir NATS server cluster solo para 10 RPS de RPC sparse es overkill operacional.
7. **RSocket descartado**: ecosistema .NET inmaduro (rsocket-net community ~700★), AOT no-validado, tooling K8s/observability no-existente.

**El maintainer dijo "si se tiene que actualizar otros microservicios se actualizan"** — válido pero el delta real (Renderer/Mail → gRPC) costaría ~12h sin beneficio funcional para nuestro perfil de tráfico. La consistencia HTTP+JSON ES la elección correcta para el producto final.

## Decisiones de diseño (locked)

| Punto | Decisión |
|---|---|
| Transporte | HTTP/1.1 + JSON via IHttpClientFactory |
| Puerto Realtime | 5030 (avoid 5010/5020 collision) |
| Service-to-service auth | X-Service-Key header (mismo secret que Renderer/Mail) |
| JWT validation en Realtime | **Reusar `IJwtKeyStore` via Redis** — Realtime agrega ProjectReference a `Verbara.Platform.Identity` + `Verbara.Platform.Identity.Redis`, consume `RedisJwtKeyStore` directamente. Rotación automática vía ADR-0012 pool. Fallback JWKS endpoint documentado si se rechaza el peso de Identity.Redis. |
| Reverse-proxy | Cilium Gateway HTTPRoute matchea `/hubs/*` → `platform-realtime:5030`. Hostname compartido `r55.local`. URL del cliente JS no cambia. |
| T27 bridges | **Mover a Realtime**. IPushEventBus consumer (PushToHubRelay) + producers (bridges) viven juntos in-process en Realtime. |
| `CachedAgentTenantResolver` | **Permanece en Platform.Api** (fuente de verdad Postgres + Pg-LISTEN invalidation). Realtime lo consume vía HTTP en `GET /api/v1/internal/agent-tenant/{agentId}` con cache local de 5min. |
| `PlatformHubAuditSink` | **Permanece en Platform.Api**. Realtime lo invoca vía `POST /api/v1/internal/hub-audit`. |
| Rollback | Chart-versioned. `helm rollback platform <prior-revision>` re-introduce el Hub en Platform.Api. |
| Shared contracts | **NUEVO proyecto `Verbara.Platform.Realtime.Contracts`** con DTOs (`AgentTenantResponse`, `HubAuditEntry`) anotados `[JsonSerializable]` en un `RealtimeContractsJsonContext`. Ambos `Platform.Api` y `Platform.Realtime` lo referencian. Drift = compile error inmediato. |
| Compose reverse proxy | **NUEVO service `nginx-gateway`** en `docker-compose.full.yml` + `docker-compose.production.yml` + `docker-compose.demo.yml`. Reglas: `location /hubs/ { proxy_pass http://realtime:5030; proxy_http_version 1.1; ... }` + `location / { proxy_pass http://platform-api:5000; }`. URL del cliente JS idéntica entre K8s y compose. Manual SMB actualizado. |
| IPC empirical validation | **Phase A.0 (1h)** — AOT publish smoke con `services.AddGrpc()` añadido. Si pasa → re-evaluar; si falla → HTTP+JSON confirmado por evidencia (no por inercia). |

## 4. Horizontal scaling — multi-pod en K8s

Constraint: tanto `platform-api` (1–N pods, HPA actual 2→8) como `platform-realtime` (1–N pods) deben funcionar horizontalmente.

### Platform.Api multi-pod — ya soportado

- ADR-0012 JWT rotation pool en Redis (`Identity__JwtKeyRotation__UseRotationPool=true` + `Identity__JwtKeyRotation__RequireRedisStore=true`) ya garantiza que tokens firmados por pod A son aceptados por pod B.
- DataProtection keyring en Postgres (V022 migration) — multi-pod safe (será reemplazado por Dapper en Phase B, mismo property se preserva).
- Pro Cluster module maneja membership + leader election para tareas que requieren single-leader.
- Endpoints REST stateless — K8s Service ClusterIP load-balancea per request.
- HPA actual: `minReplicas: 2, maxReplicas: 8, targetCpuPercent: 70` (`values.yaml`).

### Realtime multi-pod — requisitos NUEVOS

Tres concerns críticos al escalar Realtime horizontalmente:

#### 4.1 SignalR backplane (REQUIRED)

Sin backplane, cada pod Realtime tiene sus propios grupos de clientes en memoria. Si el T27 bridge dispara en pod-A pero un cliente del grupo `tenant:{tid}` está conectado a pod-B, el broadcast no le llega.

**Solución**: `Microsoft.AspNetCore.SignalR.StackExchangeRedis` + reusar el Redis ya desplegado (`r55-data/redis-0`).

```csharp
services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration["ConnectionStrings:Redis"]!,
        opt => opt.Configuration.ChannelPrefix = RedisChannel.Literal("verbara:signalr"));
```

Esto crea un canal pub/sub Redis "verbara:signalr:*" donde cada pod publica sus broadcasts; todos los pods consumen y reenvían a sus clientes locales. La latencia añadida es típicamente <5ms en cluster local; aceptable para el patrón de uso.

**Decisión de PackageReference**: Realtime adiciona `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (oficial Microsoft, AOT-irrelevante porque Realtime es non-AOT).

#### 4.2 T27 bridge deduplication (CRITICAL)

Si N pods Realtime corren los mismos T27 bridges + se suscriben a IPushEventBus, cada evento Pro dispara N relays → N broadcasts duplicados a clientes vía backplane.

Dos approaches:

**(a) Leader-election via Pro Cluster** — Realtime se une al Pro Cluster (Postgres-backed transport, ya desplegado). Solo el pod líder activa los bridges/relay. Pros: aprovecha infra existente; failover automático. Cons: añade dependencia Pro.Cluster a Realtime (~50 KB adicional + Cluster table en Postgres).

**(b) IPushEventBus consumer-group semantics** — Pro.Push tiene Redis-backed transport (`Verbara.Sdk.Pro.Push.Redis.RedisEventRelay`). Si Realtime usa transport Redis con consumer groups (à la Redis Streams XGROUP), solo un pod recibe cada mensaje. Verificar implementación: el `PostgresEventRelay` y `RedisEventRelay` actuales son broadcast (todos los consumers reciben).

**Decisión**: **(a) Leader-election via Pro.Cluster**. Razones:
- Pro.Cluster YA está deployed (Postgres transport — `ConnectionStrings__Cluster`).
- Pro.Cluster.Storage.Postgres ya es PackageReference de Platform.Api hoy → operadores familiares.
- Failover automático si el líder cae: Pro.Cluster detecta + promueve nuevo líder en ≤30s.
- Implementación: Realtime registra `AddVerbaraCluster() + UsePostgresClusterTransport()` y envuelve la activación de bridges con `if (clusterManager.IsLeader)` check. Si pierde leadership en flight, llama `relay.StopAsync()`.

#### 4.3 Presence CRDT cross-pod

`PresenceTracker` (Pro) es estado in-memory por pod. Si agente A se conecta a pod-1 y agente B a pod-2, sus presencias divergen.

**Solución**: ya resuelto por Pro. `PresenceFanoutService` + `PresenceMergeConsumer` (los hardened en Phase G-PRE 2026-05-18) usan IPushEventBus para sincronizar trackers cross-pod via CRDT. La fanout publica `PresenceSnapshotEvent`; el merge consumer aplica deltas remotos al tracker local. **Multi-pod-safe out of the box.**

Verificación: integración smoke test post-deploy debe validar que un cliente en pod-A ve la presencia de un cliente conectado a pod-B en <2s.

### Helm HPA + sticky sessions

- **HPA Realtime**: `minReplicas: 1, maxReplicas: 4, targetCpuPercent: 70` (más conservador que el API porque cada WebSocket connection consume RAM ~80KB).
- **Sticky sessions**: **NO requeridas** (gracias al backplane + presence CRDT). SignalR auto-reconnect maneja el caso de pod-restart. Esto simplifica la configuración del Gateway — usa default round-robin.
- **PodDisruptionBudget**: `minAvailable: 1` para que voluntary disruption no tire todos los pods Realtime simultáneamente.

## Arquitectura objetivo (con multi-pod explícito)

```
┌──────────────────┐      wss://r55.local/hubs/platform?access_token=...
│ Browser SignalR  │
└──────┬───────────┘  (Cilium Gateway HTTPRoute /hubs/* → Service round-robin)
       │
       ▼
┌──────────────────────────────────────────────────────────────┐
│ Service: platform-realtime ClusterIP :5030 (round-robin)    │
└────────┬─────────────────┬─────────────────┬─────────────────┘
         │                 │                 │
         ▼                 ▼                 ▼
   ┌──────────┐      ┌──────────┐      ┌──────────┐
   │realtime-1│      │realtime-2│ ...  │realtime-N│   (HPA 1-4 pods)
   │  Hub     │      │  Hub     │      │  Hub     │
   │  Pro Cluster member ── leader-election ─────│
   │  T27 bridges (leader-only)                  │
   │  Presence (CRDT cross-pod)                  │
   └────┬─────┘      └────┬─────┘      └────┬─────┘
        │                 │                 │
        └──── SignalR backplane (Redis pub/sub) ────┐
              ──── Pro IPushEventBus (Redis) ──── ──┤
              ──── Pro Cluster transport (PG) ─────┤
                                                   │
   ┌───────────────────────────────────────────────┴────┐
   │ HTTP+JSON + X-Service-Key                          │
   │ GET /api/v1/internal/agent-tenant/{id}             │
   │ POST /api/v1/internal/hub-audit                    │
   ▼                                                    │
┌──────────────────────────────────────────────────────┐│
│ Service: platform-api ClusterIP :5000 (round-robin)  ││
└────┬─────────────────┬─────────────────┬─────────────┘│
     │                 │                 │              │
     ▼                 ▼                 ▼              │
   ┌──────┐         ┌──────┐         ┌──────┐          │
   │api-1 │         │api-2 │  ...    │api-N │ ◄────────┘
   │  AOT native binary (post-Phase C)│
   │  /api/v1/* REST                  │
   │  /api/v1/internal/* (X-Service-Key gated)
   │  CachedAgentTenantResolver       │
   │  PlatformHubAuditSink            │
   └──────┘         └──────┘         └──────┘
        │
        ▼
    [postgres / redis] (shared by api + realtime)
```

## Estructura de archivos

### Nuevo proyecto `Verbara.Platform.Realtime.Contracts` (shared DTOs)

- `src/Verbara.Platform.Realtime.Contracts/Verbara.Platform.Realtime.Contracts.csproj` — biblioteca de clases pura, AOT-compatible (`<IsAotCompatible>true</IsAotCompatible>`, `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>`). Sin dependencias runtime, solo `System.Text.Json` (vía framework).
- `src/Verbara.Platform.Realtime.Contracts/Dtos/AgentTenantResponse.cs` — `public sealed record AgentTenantResponse(string AgentId, string TenantId, DateTimeOffset ResolvedAt)`.
- `src/Verbara.Platform.Realtime.Contracts/Dtos/HubAuditEntry.cs` — `public sealed record HubAuditEntry(string ActorId, string SubjectAgentId, string DeniedReason, DateTimeOffset At)`.
- `src/Verbara.Platform.Realtime.Contracts/RealtimeContractsJsonContext.cs` — `[JsonSerializable(typeof(AgentTenantResponse))] [JsonSerializable(typeof(HubAuditEntry))] internal partial class RealtimeContractsJsonContext : JsonSerializerContext`.

Ambos consumers (Platform.Api `InternalIntegrationEndpoints` y Realtime `AgentTenantResolverClient`/`HubAuditSinkClient`) usan este JsonContext en sus llamadas `JsonSerializer.Deserialize / Serialize`. Drift = compile error.

### Nuevo proyecto `Verbara.Platform.Realtime`

- `src/Verbara.Platform.Realtime/Verbara.Platform.Realtime.csproj` — `Microsoft.NET.Sdk.Web`, `IsAotCompatible=false` + analyzers off (copy `src/Verbara.Platform.Renderer/Verbara.Platform.Renderer.csproj:6-9`). Refs:
  - PackageReferences: `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (**NEW para backplane**), `Verbara.Sdk.Pro.Push`, `Verbara.Sdk.Pro.Push.SignalR`, `Verbara.Sdk.Pro.Cluster` (**NEW para leader-election**), `Verbara.Sdk.Pro.Cluster.Storage.Postgres`, `Verbara.Sdk.Hosting`, `Verbara.Sdk.Resilience`.
  - ProjectReferences: `Verbara.Platform.Core`, `Verbara.Platform.Identity`, `Verbara.Platform.Identity.Redis`, **`Verbara.Platform.Realtime.Contracts`**.
- `src/Verbara.Platform.Realtime/Program.cs` — composition root con multi-pod wiring:
  - `AddSignalR().AddStackExchangeRedis(connStr)` para backplane.
  - `AddVerbaraCluster().UsePostgresClusterTransport(...)` para leader-election de bridges.
  - `AddVerbaraProPushSignalR(...)` + `WithClusterEventBridge() / WithConversationBridge() / WithAgentBridge()` (envueltos en `IClusterLeadershipGate` check — solo activa relay si `IClusterManager.IsLeader == true`).
  - JWT auth: configurado con `IJwtKeyStore` (Redis pool) + `?access_token=` query string handler (copy `Auth/AuthSchemeConfiguration.cs:71-85` de Platform.Api).
  - Policies `"Supervisor"` + `"Agent"` (copy `Program.cs:1038-1039`).
  - `MapHub<PlatformHub>("/hubs/platform")`.
  - HttpClient factories: `"platform-api-internal"` con `BaseAddress = Services__PlatformApi__BaseUrl` + `X-Service-Key` default header.
  - `MapGet("/health", ...)` y opcional `/health/ready` que verifica Redis backplane + IsLeader status.
- `src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs` — **MOVIDO** desde `src/Verbara.Platform.Api/Services/PushToHubRelay.cs` (cambio namespace). Modificación importante: el `BackgroundService.ExecuteAsync` debe pollear `IClusterManager.IsLeader` antes de subscribir; si pierde leadership en flight, llama `_subscription.Dispose()`. Los 3 sitios IL3050 (líneas 163/179/195) no cambian.
- `src/Verbara.Platform.Realtime/Authz/RbacSubscriptionAuthorizer.cs` — **MOVIDO**.
- `src/Verbara.Platform.Realtime/Clients/AgentTenantResolverClient.cs` — **NUEVO**. Implementa `IAgentTenantResolver` con HTTP GET + IMemoryCache 5min.
- `src/Verbara.Platform.Realtime/Clients/HubAuditSinkClient.cs` — **NUEVO**. POST fire-and-forget.
- `src/Verbara.Platform.Realtime/Auth/ServiceKeyMiddleware.cs` — copia del middleware Renderer.
- `src/Verbara.Platform.Realtime/Endpoints/HealthEndpoints.cs` — `GET /health` + `GET /health/ready` (chequea Redis + Cluster connectivity).
- `src/Verbara.Platform.Realtime/Dockerfile.realtime` — clone de `Dockerfile.renderer`, `EXPOSE 5030`, entrypoint `dotnet Verbara.Platform.Realtime.dll`.

### Cambios en `Verbara.Platform.Api`

- `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj`: **eliminar** `<PackageReference Include="Verbara.Sdk.Pro.Push.SignalR" />` (línea 102). **Mantener** `Verbara.Sdk.Pro.Push` (línea 101).
- `src/Verbara.Platform.Api/Program.cs`: borrar las registraciones SignalR/bridges/relay/policies (líneas 120, 132-138, 141, 144, 153-156, 1038-1039, 1300). Agregar `app.MapInternalIntegrationEndpoints()`.
- `src/Verbara.Platform.Api/Endpoints/InternalIntegrationEndpoints.cs` — **NUEVO**. Group `/api/v1/internal/*` gated por X-Service-Key middleware: `GET /agent-tenant/{agentId}` + `POST /hub-audit`. Importa `Verbara.Platform.Realtime.Contracts` para usar los DTOs compartidos (`AgentTenantResponse`, `HubAuditEntry`) + `RealtimeContractsJsonContext`.
- `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` — agregar `<ProjectReference Include="..\Verbara.Platform.Realtime.Contracts\Verbara.Platform.Realtime.Contracts.csproj" />`.
- `src/Verbara.Platform.Api/Services/PushToHubRelay.cs` — **DELETE**.
- `src/Verbara.Platform.Api/Hubs/` — DELETE (after grep confirms `PlatformSupervisorCoordinator` puede moverse a Realtime).
- `src/Verbara.Platform.Api/Auth/RbacSubscriptionAuthorizer.cs` — **DELETE**.
- `src/Verbara.Platform.Api/Authz/CachedAgentTenantResolver.cs` — **KEEP** pero refactor mecánico: drop `IAgentTenantResolver` interface; implementar `IAgentTenantLookup` interface local.
- `src/Verbara.Platform.Api/Authz/PlatformHubAuditSink.cs` — **KEEP** mismo treatment.
- `tests/Verbara.Platform.Api.Tests/Services/PushToHubRelayTests.cs` — **MOVER** a `tests/Verbara.Platform.Realtime.Tests/`.
- `tests/Verbara.Platform.Api.Tests/Hubs/PlatformHubWiringTests.cs` — **MOVER**.

### Helm chart + K8s manifests (multi-pod)

- `infra/k8s/helm/platform/templates/realtime-deployment.yaml` — **NUEVO**. Clone de `platform-api-deployment.yaml`:
  - `podAntiAffinity preferredDuringSchedulingIgnoredDuringExecution` para spread Realtime pods en distintos nodos worker.
  - env: `JWT_SIGNING_KEY` + `ConnectionStrings__IdentityRedis` + `ConnectionStrings__Redis` (backplane) + `ConnectionStrings__Cluster` (Pro.Cluster transport) + `Services__ServiceKey` + `Services__PlatformApi__BaseUrl`.
  - readinessProbe: `/health/ready` (chequea backplane + cluster).
  - livenessProbe: `/health`.
  - resources: requests cpu 200m memory 256Mi, limits cpu 1 memory 1Gi (más chico que API porque solo Hub).
- `infra/k8s/helm/platform/templates/realtime-service.yaml` — **NUEVO**. ClusterIP, port 5030, `sessionAffinity: None` (no sticky).
- `infra/k8s/helm/platform/templates/realtime-hpa.yaml` — **NUEVO**. `minReplicas: 1, maxReplicas: 4, targetCPUUtilizationPercentage: 70`.
- `infra/k8s/helm/platform/templates/realtime-pdb.yaml` — **NUEVO**. `minAvailable: 1`.
- `infra/k8s/helm/platform/templates/realtime-httproute.yaml` — **NUEVO**. `/hubs/*` → realtime service.
- `infra/k8s/manifests/network-policies.yaml`:
  - Append `allow-realtime-ingress` en r55-platform (port 5030, any source).
  - Update `allow-prometheus-scrapes` para incluir platform-realtime.
- `infra/k8s/helm/platform/values.yaml` — agregar `realtime:` block con `replicas: 1`, `port: 5030`, `resources`, `hpa: { enabled: true, minReplicas: 1, maxReplicas: 4 }`, `redis` (reused), `identityRedis` (reused), `cluster: { connectionString: ... }`, `platformApiBaseUrl`.

### Docker-compose

Agregar `realtime:` service a `docker/docker-compose.full.yml`, `docker/docker-compose.production.yml`, `docker/demo/docker-compose.demo.yml`. **Single-replica en compose** (deploy.replicas: 1 OR sin replicas, default 1). Compose no orquesta multi-pod naturally; el modo multi-pod aplica solo a K8s.

```yaml
realtime:
  build: { context: .., dockerfile: src/Verbara.Platform.Realtime/Dockerfile.realtime }
  environment:
    ASPNETCORE_URLS: http://+:5030
    ConnectionStrings__Redis: redis:6379       # backplane (single-pod en compose, no-op pero clean)
    ConnectionStrings__IdentityRedis: redis:6379
    ConnectionStrings__Cluster: Host=postgres;Database=verbara;Username=platform;Password=...
    Identity__JwtKeyRotation__UseRotationPool: "true"
    Services__ServiceKey: ${SERVICE_KEY:-platform_internal_secret}
    Services__PlatformApi__BaseUrl: http://platform-api:5000
  depends_on: { redis: { condition: service_healthy }, postgres: { condition: service_healthy } }
  healthcheck: { test: ["CMD-SHELL", "curl -sf http://localhost:5030/health || exit 1"] }
  restart: unless-stopped
```

### Nuevo service `nginx-gateway` (REQUIRED para compose paridad con K8s)

Hoy en compose no existe reverse-proxy unificado. Phase A agrega `nginx-gateway` como service en los 3 compose files con path-based routing que espeja la Cilium HTTPRoute K8s:

```yaml
nginx-gateway:
  image: nginx:1.27-alpine
  ports: ["80:80"]
  volumes:
    - ../docker/nginx-gateway.conf:/etc/nginx/conf.d/default.conf:ro
  depends_on: [ platform-api, realtime, web ]
  restart: unless-stopped
```

`docker/nginx-gateway.conf` (nuevo archivo):

```nginx
upstream realtime { server realtime:5030; }
upstream api { server platform-api:5000; }
upstream web { server web:80; }

server {
  listen 80;
  # SignalR/WebSocket hub
  location /hubs/ {
    proxy_pass http://realtime;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_read_timeout 3600s;
  }
  # REST API
  location /api/ {
    proxy_pass http://api;
    proxy_set_header Host $host;
  }
  # Web frontend (catch-all)
  location / {
    proxy_pass http://web;
    proxy_set_header Host $host;
  }
}
```

**Resultado**: cliente JS conecta a `http://localhost/hubs/platform` (compose) o `https://r55.local/hubs/platform` (K8s) — **misma URL, código frontend idéntico**, topología transparente.

**Manuales SMB**: actualizar `docs/manuales/smb/01-instalacion.md` con la regla nueva del proxy. Operadores que ya tienen Cloudflare Tunnel apuntan a `nginx-gateway:80` en vez de `platform-api:5000` directamente.

## Phasing

Concern detectado: SignalR usa backplane in-process por defecto. Si A.2 mueve la relay a Realtime pero A.3 (cutover del hub) llega después, hay una ventana intermedia donde el hub vive en Platform.Api pero el relay en Realtime → broadcasts cross-process no llegan.

**Decisión: colapsar A.2 + A.3 en un solo commit/release.** Adiciono Phase A.0 empirical gate (2026-05-18 refinement):

| Stage | Goal | Smoke |
|---|---|---|
| **A.0** empirical IPC gate | **Antes de comprometer HTTP+JSON irrevocablemente**, hacer un AOT publish smoke en una copia throwaway de Platform.Api con un endpoint gRPC añadido (`services.AddGrpc()` + un `MapGrpcService<HelloService>` mínimo). Objetivo: medir empíricamente si `Grpc.AspNetCore` 2.60+ es AOT-clean en este proyecto. Si pasa con 0 errores nuevos → re-evaluar gRPC para Realtime. Si genera warnings IL2026/IL3050 → confirma HTTP+JSON como la elección correcta basada en evidencia. **1 hora**, no commits — solo notes en `docs/decisions/0022-platform-api-aot-shipping-path.md` Amendment §6. | Output del AOT publish documentado; decisión IPC ratificada. |
| **A.1** scaffold | Crear proyecto Realtime + proyecto `Verbara.Platform.Realtime.Contracts` (DTOs compartidos con `[JsonSerializable]`) + Dockerfile + Helm templates (deployment, service, HPA, PDB, HTTPRoute) + values block. Wire DI completo (backplane + Cluster) pero NO redireccionar tráfico ingress. Platform.Api sigue dueño del Hub. | `dotnet build` 0/0. `kubectl apply` deployment + 1 pod Ready. `curl localhost:5030/health` → 200. Cluster join visible en Pg `cluster_nodes` table. |
| **A.2+A.3** cutover | Mover relay + bridges + Hub + presence services a Realtime. Borrar Pro.Push.SignalR PackageReference de Platform.Api. Aplicar Cilium HTTPRoute `/hubs/*` → Realtime. Crear InternalIntegrationEndpoints en API. Activar `AddStackExchangeRedis` backplane. Activar leader-gate en bridges. **Agregar `nginx-gateway` service a 3 compose files + actualizar `docs/manuales/smb/01-instalacion.md` con la nueva regla de proxy `/hubs/*`.** | (a) AOT publish Platform.Api: los 3 errores IL3050 SignalR desaparecen. (b) JS client browser conecta `wss://r55.local/hubs/platform?...`, recibe 101 + `OnPresenceUpdated`. (c) **Multi-pod test**: scale realtime a 2 réplicas; cliente conectado a pod-1 ve presence update de cliente en pod-2 en <2s; deletar pod líder → failover Cluster ≤30s → bridges retoman. (d) **Compose test**: `docker compose -f docker-compose.full.yml up` → cliente JS conecta a `http://localhost/hubs/platform` (vía nginx-gateway) → ve presence updates sin tocar el código frontend. |
| **A.4** cleanup | Mover tests SignalR a Realtime.Tests. Borrar archivos huérfanos (`Hubs/`, `RbacSubscriptionAuthorizer.cs`). Drop policies Supervisor+Agent si grep confirma cero consumers REST en Platform.Api. | `dotnet test` ambos green. AOT publish output sin SignalR-relacionados. |

Convención commit: `feat(realtime): A.N — <desc>`.

## Verification

0. **A.0 gate (empirical IPC validation)**: en branch throwaway de Platform.Api añadir `services.AddGrpc()` + `MapGrpcService<HelloService>` minimal. Correr el publish AOT de ADR-0022 §3. Documentar resultado en `docs/decisions/0022-platform-api-aot-shipping-path.md` Amendment §6. Esperado: si pasa con warnings → HTTP+JSON aún recomendado (gRPC abre AOT-surface adicional al SignalR/EF Core ya conocidos). Si pasa limpio → considerar gRPC en futuras iteraciones (NO en Phase A — el plan ya está locked). Descartar el branch throwaway.
1. `dotnet build Verbara.Platform.slnx -c Release` — 0 warnings, 0 errors después de cada stage.
2. `dotnet test tests/Verbara.Platform.Api.Tests/` — 958 minus los 4 tests movidos (~954 green).
3. `dotnet test tests/Verbara.Platform.Realtime.Tests/` — tests movidos pasan.
4. Re-correr ADR-0022 §3 empirical AOT publish:
   ```
   dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj \
     -c Release -r linux-x64 --self-contained true \
     -p:PublishAot=true -p:InvariantGlobalization=true
   ```
   Esperado: 3 errores IL3050 SignalR desaparecen; quedan exactamente los 5 EF Core (Program.cs:515,523,525) — Phase B work.
5. `helm upgrade --install --dry-run platform infra/k8s/helm/platform/` clean. Real install: `platform-api` (2 replicas) + `platform-realtime` (1-N replicas, HPA active) Ready.
6. **Multi-pod smoke (CRITICAL)**:
   - `kubectl scale deployment platform-realtime --replicas=2 -n r55-platform`
   - Login en Web frontend con dos ventanas browser → ambas conectan via Service round-robin a pods diferentes
   - Cambiar presence en ventana A → ventana B recibe `OnPresenceUpdated` en <2s (vía backplane)
   - `kubectl get pods -l app.kubernetes.io/name=platform-realtime -o wide` → confirmar pods en nodos worker distintos (anti-affinity)
   - Identificar pod líder via Pg `SELECT * FROM cluster_nodes WHERE is_leader = true` → matar ese pod → verificar bridges retoman en otro pod ≤30s sin perder eventos
7. WebSocket reconnection: `kubectl delete pod <realtime-pod>` → cliente JS auto-reconnects vía `withAutomaticReconnect()` ≤30s al siguiente pod del Service.
7b. **Compose paridad**: `docker compose -f docker/docker-compose.full.yml up -d` desde una checkout fresh. Browser apunta a `http://localhost/` → debe (a) cargar el web frontend, (b) cliente SignalR conecta a `http://localhost/hubs/platform` vía nginx-gateway → realtime, (c) REST calls a `http://localhost/api/*` vía nginx-gateway → platform-api. Cero diferencia visible para el frontend entre K8s y compose.
8. Service-to-service smoke:
   ```
   kubectl exec deployment/platform-realtime -- \
     curl -sH "X-Service-Key: $SERVICES__SERVICEKEY" \
     http://platform-api:5000/api/v1/internal/agent-tenant/test-agent-id
   ```
   → 200 + JSON con tenant resolution.

## Critical files for implementation

- `/media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Program.cs`
- `/media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Services/PushToHubRelay.cs` (DELETE post-A.3)
- `/media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Auth/AuthSchemeConfiguration.cs:54-99` (copy JWT pattern)
- `/media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Services/JwtTokenService.cs:60-67,117-125` (multi-key resolver pattern para reusar en Realtime)
- `/media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Renderer/Program.cs` (reference pattern for new Realtime Program.cs)
- `/media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Renderer/Dockerfile.renderer` (reference for Dockerfile.realtime)
- `/media/Data/Source/Verbara/Verbara.Platform/infra/k8s/helm/platform/templates/httproute.yaml` (extend con `/hubs/*` route o segundo HTTPRoute file)
- `/media/Data/Source/Verbara/Verbara.Platform/infra/k8s/helm/platform/templates/platform-api-deployment.yaml` (template base para realtime-deployment.yaml)
- `/media/Data/Source/Verbara/Verbara.Platform/infra/k8s/helm/platform/values.yaml` (extend con `realtime:` block)
- `/media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs` (the Hub itself — vive en Pro, solo se mapea desde Realtime)
- `/media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Cluster/` (Cluster module para leader-election, referencia para wire-up)
- `/media/Data/Source/Verbara/Verbara.Platform/docker/docker-compose.full.yml` + `docker-compose.production.yml` + `docker/demo/docker-compose.demo.yml` (agregar `nginx-gateway` + `realtime` services)
- `/media/Data/Source/Verbara/Verbara.Platform/docker/nginx-gateway.conf` — **NUEVO** archivo de config nginx
- `/media/Data/Source/Verbara/Verbara.Platform/docs/manuales/smb/01-instalacion.md` — actualizar con reverse-proxy nuevo

## Estimación de esfuerzo (ajustada por multi-pod + refinamientos 2026-05-18)

| Stage | Horas |
|---|---|
| A.0 gRPC AOT empirical smoke + Amendment ADR-0022 §6 | 1 |
| A.1 scaffold (proyecto Realtime + proyecto Contracts + Dockerfile + Helm con HPA/PDB/HTTPRoute + Pro.Cluster wiring) | 4 (+1 por Contracts assembly) |
| A.2+A.3 cutover (relay + bridges + Hub + HTTPRoute + Pro.Push.SignalR remove + InternalIntegrationEndpoints + leader-gate + backplane + **nginx-gateway compose** + manual SMB) | 8 (+1 por nginx-gateway en 3 compose + manual update) |
| A.4 test migration + multi-pod smoke + compose smoke + cleanup | 4 |
| Slack edge cases (PlatformSupervisorCoordinator decision, JWT/Redis edge cases, Cluster transport tuning) | 2 |
| **Total** | **~19h, calendar 2.5 maintainer-days** |

(Original 12h → multi-pod añadió 4h → A.0 + Contracts + nginx-gateway añaden 3h más. Trade-off: 3h extra evita future churn de drift de contratos + asegura paridad compose↔K8s + ratifica IPC choice con evidencia.)

## Out of scope

- Phase B (EF Core DataProtection → Dapper IXmlRepository) — separate plan.
- Phase C (flip AOT en csproj + Dockerfile + verify single ELF binary) — separate plan.
- Phase D (image-digest regeneration + authorized-digests.json update).
- Phase E (public image tag cutover + deprecate v2.3.1).
- Migrar Renderer/Mail a otro patrón IPC — el análisis profundo demostró que HTTP+JSON es la elección correcta.
- Apache Kafka / NATS / RSocket como transport — descartados por overhead/inmadurez.

## Riesgos + mitigaciones

| Riesgo | Probabilidad | Impacto | Mitigación |
|---|---|---|---|
| `PlatformSupervisorCoordinator` tiene call sites REST además del Hub | Media | Medio | Stage A.2 empieza con `grep -rn 'SupervisorCoordinator'` para decidir move-vs-shim |
| JWT validation en Realtime falla en boot (Redis no reachable) | Media | Alto | Reusar pattern JwtTokenService ctor con fallback file-key; documentar `ConnectionStrings__IdentityRedis` requerido |
| Cilium HTTPRoute orden de matching ambiguo | Baja | Medio | Test empírico post-deploy: `curl -v wss://r55.local/hubs/platform` debe llegar a Realtime pod por logs |
| Backplane Redis falla intermitentemente → broadcasts desincronizados | Media | Alto | Health probe `/health/ready` chequea Redis backplane connectivity; pod marca NotReady → Service quita del rotation |
| Cluster leader election lenta (>60s) post pod-kill → bridges idle | Baja | Medio | Pro.Cluster default election timeout 30s; smoke test confirma; alerta Prometheus si `verbara_cluster_leader_count` ≠ 1 por >60s |
| Tests movidos a Realtime.Tests fallan por DI faltante | Alta | Bajo | Crear `RealtimeApiFactory : WebApplicationFactory<Program>` análogo a `AuthenticatedPlatformApiFactory` |
| HPA scaling rápido → muchos pods Realtime → cost overhead Redis backplane | Baja | Bajo | maxReplicas=4 inicialmente; observability via OTEL para ajustar |
| Multi-pod presence CRDT diverge bajo high-churn | Baja (Pro testeado) | Medio | Smoke test confirma <2s sync; Pro Phase G-PRE (2026-05-18) ya hardened estos paths |
