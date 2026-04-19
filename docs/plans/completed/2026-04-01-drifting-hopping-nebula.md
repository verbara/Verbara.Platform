# Plan: Fix Docker API Build — Stale NuGet Packages

## Context

El Docker build de Platform API falla con `CS0246: ILicenseStatus could not be found`. La causa raíz es que `Asterisk.Platform/local-nuget-feed/` (la copia dentro del repo, usada por Docker) tiene paquetes Pro del **30 de marzo**, pero `ILicenseStatus` se agregó al paquete Pro.Licensing en el **1 de abril** (Plan 30A). El feed compartido `/media/Data/Source/IPcom/local-nuget-feed/` sí tiene los paquetes actualizados.

## Root Cause

Hay **dos** directorios `local-nuget-feed/`:

| Ubicación | Fecha | Tiene ILicenseStatus |
|-----------|-------|---------------------|
| `/media/Data/Source/IPcom/local-nuget-feed/` (shared) | 2026-04-01 | Si |
| `/media/Data/Source/IPcom/Asterisk.Platform/local-nuget-feed/` (repo) | 2026-03-30 | No |

Docker `COPY . .` usa la copia del repo (vieja). El sed reemplaza correctamente la ruta en NuGet.Config, el restore funciona, pero la DLL dentro del nupkg no contiene `ILicenseStatus`.

## Fix

### Paso 1: Sincronizar paquetes Pro del feed compartido al repo

```bash
cp /media/Data/Source/IPcom/local-nuget-feed/Asterisk.Sdk.Pro.*.nupkg \
   /media/Data/Source/IPcom/Asterisk.Platform/local-nuget-feed/
```

### Paso 2: También sincronizar paquetes Sdk (por si hay desync)

```bash
cp /media/Data/Source/IPcom/local-nuget-feed/Asterisk.Sdk.*.nupkg \
   /media/Data/Source/IPcom/Asterisk.Platform/local-nuget-feed/
```

### Paso 3: Verificar Docker build

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
docker compose -f docker/demo/docker-compose.demo.yml build --no-cache platform-api
```

### Paso 4: Reiniciar container API y correr E2E

```bash
docker compose -f docker/demo/docker-compose.demo.yml up -d platform-api
# Esperar health check, luego:
cd /media/Data/Source/IPcom/Asterisk.Platform.Web
npm run e2e
```

## Archivos críticos

- `/media/Data/Source/IPcom/Asterisk.Platform/local-nuget-feed/` — paquetes desactualizados
- `/media/Data/Source/IPcom/local-nuget-feed/` — paquetes actualizados (fuente de verdad)
- `/media/Data/Source/IPcom/Asterisk.Platform/Dockerfile` — Docker build
- `/media/Data/Source/IPcom/Asterisk.Platform/NuGet.Config` — referencia al feed local

## Prevención futura

Considerar crear un script o hook que sincronice automáticamente cuando se hace `dotnet pack` en Sdk o Pro. O eliminar la copia del repo y montar el feed compartido en Docker via volume.
