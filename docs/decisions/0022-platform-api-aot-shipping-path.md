# ADR-0022 — Platform.Api Native AOT shipping path

| | |
|---|---|
| **Status**   | Accepted |
| **Date**     | 2026-05-18 |
| **Deciders** | Maintainer |
| **Supersedes** | — |
| **Related**  | [ADR-0011](0011-image-digest-binding.md), [ADR-0012 (Pro)](../../../Verbara.Sdk.Pro/docs/decisions/0012-eliminate-enforcement-mode-for-license-required-model.md), [ADR-0018](0018-visibility-decision-3-private-now-public-on-trigger.md) |

## Context

While preparing the **Pro v2.5.0-pro compressed-validation** dry-run on 2026-05-18,
the maintainer inspected the canonical published image
`ghcr.io/verbara/platform/api:v2.3.1` and discovered an IP-exposure
regression that had been masked by an inaccurate `CLAUDE.md` claim:

```sh
docker inspect ghcr.io/verbara/platform/api:v2.3.1 --format '{{json .Config}}'
# Entrypoint: ["dotnet", "Verbara.Platform.Api.dll"]
# Env:        DOTNET_VERSION=10.0.8, ASPNET_VERSION=10.0.8

docker run --rm --entrypoint sh ghcr.io/verbara/platform/api:v2.3.1 \
    -c 'ls /app | wc -l; ls /app/Verbara.Sdk.Pro.*.dll | wc -l'
# 175 total files
# 68 Verbara DLLs (all of Verbara.Sdk.Pro is shipped as IL)
```

The image runs the **CLR runtime executing portable IL DLLs**, not a Native-AOT
binary. Anyone who pulls the public image (or extracts the layer tarball via
`docker save`) can decompile the closed-source **Verbara.Sdk.Pro** packages
with ILSpy or dotPeek and recover the commercial source. Per the **Pro
licensing model** (private repo, paid commercial product, signed
`.lic`-gated runtime), this is a catastrophic IP leak.

The misleading `CLAUDE.md` line — *"NativeAOT (`IsAotCompatible=true`)"* —
was true for the **SDK + Pro library packages** (every `.csproj` in those
repos asserts AOT-compatibility) but **false for `Verbara.Platform.Api`**,
the host that bundles those libraries into the published image. The Platform
API csproj explicitly disables AOT:

```xml
<!-- Platform.Api consumes Verbara.Sdk.Pro.Push.SignalR (hub + presence CRDT)
     which depends on Microsoft.AspNetCore.SignalR. SignalR server-side
     dispatch in .NET 10 still relies on reflection that trips the trim/AOT
     analyzers. Keep the analyzers off on this project so warnings-as-errors
     do not gate the build. -->
<IsAotCompatible>false</IsAotCompatible>
<EnableTrimAnalyzer>false</EnableTrimAnalyzer>
<EnableSingleFileAnalyzer>false</EnableSingleFileAnalyzer>
<EnableAotAnalyzer>false</EnableAotAnalyzer>
```

This decision codifies the **canonical shipping constraint**: every public
`ghcr.io/verbara/platform/*` image MUST be a Native-AOT single-binary image.
Non-AOT publishing of Platform.Api ships Pro IP as decompilable IL and is
no longer acceptable.

## Decision

1. **Every public Verbara container image MUST ship as Native AOT.** The
   image filesystem MUST contain a single native ELF binary plus its
   bundled native dependencies and license / config files — **no `.dll`
   files, no `dotnet` runtime in the base image**.

2. **The Pro v2.5.0-pro release train is BLOCKED from shipping a public
   image** until Platform.Api can be published as Native AOT. The compressed
   validation cycle for v2.5.0-pro (Scenarios A–E) executes against a
   non-AOT preview build for behavioural-parity evidence ONLY; the
   actual `ghcr.io/verbara/platform/api:v2.5.0-pro` tag MUST be cut from
   an AOT publish or it MUST NOT be cut at all.

3. **Empirical-validated blockers** to AOT publishing (captured 2026-05-18 via
   `dotnet publish -c Release -r linux-x64 -p:PublishAot=true` against
   the current `main` of `Verbara.Platform`):

   | # | Blocker | File:Line | Code | Root cause |
   |---|---|---|---|---|
   | 1 | SignalR server hub dispatch | `Services/PushToHubRelay.cs:163,179,195` | IL3050 | `IHubContext<THub, T>.Clients.get` is annotated `[RequiresDynamicCode]` — generates client proxy at runtime |
   | 2 | EF Core DataProtection | `Program.cs:515,525` | IL2026 + IL3050 | `AddPlatformDataProtection` configures EF Core which reflects over entity types |
   | 3 | EF Core DbContext ctor | `Program.cs:523` | IL2026 | `PlatformDataProtectionDbContext` inherits `Microsoft.EntityFrameworkCore.DbContext` (reflection over types) |

   Total: **8 errors** (3× SignalR + 5× EF-Core-DataProtection), 0 warnings.
   No framework, third-party (Argon2, BCrypt, Dapper, Npgsql, AWSSDK, etc.),
   Verbara SDK, or Pro-package warnings reached — the publish died on Platform-
   owned code first. The Argon2 native + JSON source-gens + RDG are already
   AOT-clean. Pro packages already assert `IsAotCompatible=true`.

4. **Roadmap** to unblock AOT shipping (separate plan, separate release train):

   - **Phase A — Extract SignalR Hub** (1–2 days, P0):
     - Create a new `Verbara.Platform.Realtime` host project that owns the
       SignalR Hub + `PushToHubRelay` + presence CRDT.
     - Platform.Api communicates with Realtime via gRPC (AOT-safe) or HTTP+JSON.
     - Move `Verbara.Sdk.Pro.Push.SignalR` PackageReference OUT of
       `Verbara.Platform.Api.csproj` and INTO
       `Verbara.Platform.Realtime.csproj`.
     - Realtime host ships as a SEPARATE image
       (`ghcr.io/verbara/platform/realtime:<tag>`). Smaller surface, may
       remain non-AOT for the short term while AOT support for ASP.NET Core
       SignalR matures upstream — but MUST NOT bundle Pro DLLs other than
       `Pro.Push.SignalR` (which is a public API, not the closed-source
       engines like Dialer/Analytics/Realtime/EventStore).

   - **Phase B — Replace EF Core DataProtection** (0.5 day, P0):
     - The DataProtection keyring is the only EF Core consumer in
       Platform.Api. Replace with a **Dapper-backed** `IXmlRepository`
       implementation (already the pattern used by every other Platform
       storage layer per CLAUDE.md "Npgsql + Dapper" gotcha). Drop the
       EF Core + Npgsql.EntityFrameworkCore.PostgreSQL PackageReferences.

   - **Phase C — Re-attempt AOT publish + validate runtime parity** (1 day):
     - Flip the csproj flags to `<IsAotCompatible>true</IsAotCompatible> +
       <PublishAot>true</PublishAot> + <InvariantGlobalization>true</InvariantGlobalization>`.
     - `dotnet publish -c Release -r linux-x64 --self-contained true`
       MUST produce a single ELF binary in `/app` with zero `.dll` files.
     - Final image base: `mcr.microsoft.com/dotnet/runtime-deps:10.0`
       (NOT `aspnet:10.0` — runtime-deps has only libc/libssl/ICU, no CLR).
     - Entrypoint: `["./Verbara.Platform.Api"]` (native binary).
     - Run the existing 958-test Platform.Api.Tests suite against a host
       that loads the published AOT binary as a child process (sanity:
       AOT trim could silently drop a feature path; we trust analyzer
       coverage but cross-check with one end-to-end smoke).
     - Run R5.5 D-LK style 24h soak against the AOT image.

   - **Phase D — Image-binding regeneration** (0.5 day):
     - The new AOT image has a fresh digest. Regenerate
       `verbara-website/data/authorized-digests.json` with the new digest
       added to the AuthorizedImageDigests claim of every active license.
     - Image-binding (ADR-0011 Layer C) continues to work unchanged.

   - **Phase E — Public image cutover** (1 day):
     - Tag + push v2.4.0 (Platform consumer of Pro v2.5.0-pro) + v2.5.0-pro
       (Pro) AS AOT IMAGES.
     - Revoke / deprecate the old non-AOT images on ghcr.io (mark with
       OCI annotations `org.opencontainers.image.deprecated=true` —
       `crane` supports manifest annotation edits without re-uploading).
     - Customer-facing: update `docs/manuales/smb/01-instalacion.md` to
       the new tag.

5. **Estimated total effort**: 4–5 maintainer-days. Conservative — Phase A
   has unknown unknowns around CRDT presence sync between the new
   Realtime host and the Platform.Api (cross-process state must remain
   eventually consistent; existing tests don't cover the IPC boundary).

## Consequences

### Positive

- **Pro IP no longer ships as decompilable IL.** Native AOT compilation
  reduces the binary to optimized machine code; reverse-engineering goes
  from "open in ILSpy, hit Ctrl+S" to "lift assembly with IDA Pro and
  spend weeks". A meaningful raise of the attack cost.
- **Smaller, faster, more secure runtime image.** Native AOT images are
  typically ~75 MB vs ~250 MB for an aspnet runtime image. No JIT compiler
  in the container = smaller attack surface. Cold-start time drops from
  seconds to ~50 ms.
- **Forced architectural cleanup.** Extracting Realtime + replacing EF
  Core DataProtection are improvements we wanted anyway (the Realtime
  microservice has been on the roadmap since v1.7.0 per the MEMORY.md
  "Outstanding from older roadmap" section under "SSE tech debt").

### Negative

- **4–5 days of maintainer time** before Pro v2.5.0-pro can ship publicly.
  In a maintainer-only project this is significant; the compressed-
  validation scenarios A-E can still execute in the local lab against a
  preview build, but the public release tag has to wait for AOT.
- **New microservice to operate.** Verbara.Platform.Realtime adds a pod
  to every Helm deployment, a NetworkPolicy, a PDB, a HPA, etc. Operators
  who deploy bare Docker compose need a second compose service. The SMB
  installation manual (`docs/manuales/smb/`) must be updated.
- **IPC complexity.** The Platform.Api ↔ Realtime contract has to be
  defined (gRPC vs HTTP, AOT-safe serialization, auth between them). One
  more boundary to test, observe, and version.
- **SignalR client-side AOT story is shaky.** The Pro Push.SignalR
  package ships a SignalR Hub server-side; Phase A moves that server-
  side surface to Realtime. But customers who use the SignalR JS client
  to talk to Platform.Api today will now talk to Realtime. A reverse-
  proxy rule (Cilium Gateway or nginx) can mask the topology change at
  the URL level (`r55.local/realtime/hub` → realtime service:port).

### Neutral

- The non-AOT shipping path remains available for **private / lab /
  dev** builds where IP exposure is not a concern (the maintainer's
  local Talos cluster runs the non-AOT image today and that's fine).
  This ADR's "MUST" applies only to **public ghcr.io tags**.

## References

- Empirical AOT publish log: `/tmp/aot-publish.log` (kept until next
  cleanup — copy to `docs/operations/compressed-validation-evidence/`
  if the maintainer wants to retain it).
- IL3050 reference: <https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/il3050>
- IL2026 reference: <https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities>
- ASP.NET Core SignalR AOT status (as of .NET 10): not supported for
  server-side hubs; client-side hub proxy is supported via
  `Microsoft.AspNetCore.SignalR.Client.SourceGenerator`.

## Amendment §6 — 2026-05-18 — gRPC AOT empirical smoke (Phase A.0 gate)

Before locking in HTTP+JSON as the canonical IPC for `Verbara.Platform.Api ↔ Verbara.Platform.Realtime` (per the active Phase A plan at `docs/plans/active/2026-05-18-phase-a-realtime-extraction.md`), the maintainer requested empirical verification that the alternative gRPC server-side stack would NOT have been a better choice today on .NET 10.

### Experiment

Throwaway changes (reverted after the publish):

1. `Directory.Packages.props`: `<PackageVersion Include="Grpc.AspNetCore" Version="2.80.0" />`.
2. `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj`: `<PackageReference Include="Grpc.AspNetCore" />`. AOT flags flipped on (`IsAotCompatible=true`, `EnableTrimAnalyzer=true`, `EnableSingleFileAnalyzer=true`, `EnableAotAnalyzer=true`, `PublishAot=true`, `InvariantGlobalization=true`).
3. `src/Verbara.Platform.Api/Program.cs`: `builder.Services.AddGrpc();` inserted just before `var app = builder.Build();`.

Command:

```sh
dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj \
  -c Release -r linux-x64 --self-contained true \
  -o /tmp/aot-grpc-smoke/ 2>&1 | tee /tmp/aot-grpc-smoke.log
```

### Result

| Bucket | Count | Delta vs §3 baseline |
|---|---|---|
| SignalR `IL3050` at `PushToHubRelay.cs:163,179,195` | 3 | unchanged |
| EF Core `IL2026`+`IL3050` at `Program.cs:515,523,525` | 5 | unchanged |
| **NEW gRPC-related `IL2026`/`IL3050`** | **0** | **none** |
| Grpc / Protobuf string matches in `/tmp/aot-grpc-smoke.log` | **0** | clean |

Total errors: **8** (identical to §3 baseline). Zero net regression from adding `services.AddGrpc()`.

### Caveat

This smoke covered the DI-registration path (`services.AddGrpc()`) but NOT a full `MapGrpcService<TServiceImpl>` wire-up because we did not bring in a `.proto` file + `Grpc.Tools` codegen. The reflection-heavy AOT-risky surface for gRPC is in the per-service dispatch tables. So the result is **necessary but not sufficient** evidence:

- **Necessary**: gRPC's core DI registration is AOT-clean (✅ proven empirically here).
- **Sufficient**: gRPC's full server runtime (per-service base classes + dispatcher) is AOT-clean (NOT tested here — would require ~2h of `.proto` + `Grpc.Tools` scaffolding).

### Verdict

`Grpc.AspNetCore` 2.80.0 in .NET 10 appears AOT-friendly at the level we exercised, consistent with Microsoft's claims since 2.60+. The empirical evidence is positive but partial.

**Phase A decision stands**: HTTP+JSON via `IHttpClientFactory` for `Verbara.Platform.Api ↔ Verbara.Platform.Realtime`. Justifications:

1. **Operational consistency**: same pattern as Renderer + Mail. Single mental model.
2. **Zero new tooling**: no `.proto` files, no codegen step, no `Grpc.Tools` MSBuild integration.
3. **Empirically AOT-clean** at the system-wide level (Phase A removes the SignalR errors; Phase B removes the EF Core errors; both are independently verified).
4. **gRPC's marginal benefits** (binary efficiency, native streaming) are wasted at the ~10 RPS sparse req/resp + fire-and-forget traffic shape measured in the code inventory.
5. **Future-proofing**: if a future microservice (e.g. a Vector/AI inference service) needs streaming, gRPC remains an available choice — this empirical evidence has lowered the perceived risk for that future call.

### Cleanup

All throwaway edits reverted via `git checkout -- Directory.Packages.props src/Verbara.Platform.Api/Verbara.Platform.Api.csproj src/Verbara.Platform.Api/Program.cs`. The `/tmp/aot-grpc-smoke.log` retained for reference; copy to `docs/operations/compressed-validation-evidence/` if desired.
