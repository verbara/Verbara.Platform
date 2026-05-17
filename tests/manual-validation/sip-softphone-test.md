# Validación manual SIP con softphone (Linphone / Zoiper)

> **Para qué:** validar el canal Voz/SIP de un deploy Verbara registrando un softphone "real" como agente, sin necesitar un trunk SIP externo todavía. Útil cuando:
> - Estás haciendo el primer setup y todavía no provisionaste el trunk con el carrier.
> - Querés aislar si el problema es Asterisk vs el trunk.
> - Estás training a un agente nuevo y querés que pruebe el flow sin gastar minutos del trunk real.
>
> **Tiempo:** 15-20 minutos.

## 1. Pre-requisitos

- Stack Verbara arriba con manual [02](../../docs/manuales/smb/02-arranque-stack.md).
- Al menos 1 agente provisionado (manual [03](../../docs/manuales/smb/03-setup-inicial.md)).
- Endpoint WebRTC/SIP provisionado al agente (manual [06](../../docs/manuales/smb/06-canal-voz-sip.md) §6).
- Conexión LAN o WiFi (no hace falta IP pública para este test).

Tener a mano (del provisioning del agente):
- **Extension:** ej. `1001`
- **PJSIP username:** ej. `agente1`
- **PJSIP password:** el que devolvió `provision-webrtc`
- **Server hostname/IP:** la IP LAN del server Verbara (ej. `192.168.40.100`)
- **SIP port:** `5060`

## 2. Linphone (recomendado — multiplataforma libre)

Disponible en Linux, macOS, Windows, Android, iOS. Descargar desde
<https://www.linphone.org/releases/>.

### 2.1 Setup de la cuenta SIP

1. Abrir Linphone → **Cuentas → Asistente → Yo uso una cuenta SIP**.
2. Completar:

   | Campo | Valor |
   |---|---|
   | **Username** | `agente1` |
   | **Password** | `{el password de provision-webrtc}` |
   | **Domain** | `192.168.40.100` (IP LAN del server) |
   | **Display name** | `Agente Test` |
   | **Transport** | UDP |

3. Click **Usar la cuenta**.

4. Settings adicionales (engranaje → Settings de la cuenta):
   - **Port** (opcional): `5060`
   - **Codecs** → Audio: dejar sólo `PCMU` (G.711 µ-law) y `PCMA` (G.711 a-law) — desactivar Opus para evitar transcoding overhead en el test.
   - **NAT and Firewall**: dejar default `Use STUN` desactivado para LAN.

### 2.2 Validar registro

En la barra superior de Linphone:
- Indicador verde + texto `agente1 — connected` → registro OK.
- Indicador rojo + `Registration failed` → ir a §5 Troubleshooting.

Confirmar desde el server Verbara:
```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show contacts'
agente1/sip:agente1@192.168.10.50:54321    Available    35.123ms
```

### 2.3 Hacer llamada de prueba (loopback agente→agente)

Si tenés 2 agentes provisionados:

1. Repetir el setup en otro device/máquina con `agente2`.
2. En Linphone de `agente1`: marcar `1002` (la extensión de `agente2`).
3. `agente2` debe sonar (ringtone Linphone).
4. Atender → audio bidireccional → hablar → escuchar.
5. Colgar.

### 2.4 Validar desde el lado de Verbara

En el server:
```bash
$ docker exec verbara-asterisk asterisk -rx 'core show channels'
2 active channels
Channel              Location           State     ...
PJSIP/agente1-...    1002@verbara:1    Up        Dial(PJSIP/agente2)
PJSIP/agente2-...    1002@verbara:1    Up        Bridge(...)
```

En la Web del agente (Verbara `/agent`):
- Si el agente Web está logueado, vería la llamada activa.
- El CDR aparece en `/admin/analytics/calls` después del hangup.

## 3. Zoiper (alternativa)

También multiplataforma, free tier sin limitaciones críticas para tests.
Descargar desde <https://www.zoiper.com/en/voip-softphone/download/current>.

### 3.1 Setup

1. Abrir Zoiper → **Add new SIP account**.
2. **Account name**: `agente1`
3. Credentials:
   - Username: `agente1`
   - Password: `{el password}`
   - Domain: `192.168.40.100`
4. **Network → Transport**: `UDP`
5. **Codecs** → drag-and-drop sólo `G.711 a-law` + `G.711 u-law` arriba.
6. Save → indicador verde abajo = `Registered`.

### 3.2 Llamada

Tab "Dial" → escribir `1002` → click **Call**. Misma validación que Linphone §2.3-2.4.

## 4. WebRTC desde browser (paralelo)

Si querés comparar SIP nativo (softphone) vs WebRTC:

1. Logueate al Web Verbara como `agente1` en una pestaña.
2. Click **Conectar** → indicador verde = WebRTC registrado.
3. Validar en server que tenés DOS contactos al mismo endpoint:
   ```bash
   $ docker exec verbara-asterisk asterisk -rx 'pjsip show contacts'
   agente1/sip:agente1@192.168.10.50:54321    Available    UDP     35ms
   agente1/sip:f9a8...@browser:443            Available    WSS     12ms
   ```
4. Una llamada entrante va a tocar **ambos** simultáneamente (configurable
   en el endpoint con `max_contacts`).

> 💡 Para tests es útil **dejar sólo uno conectado a la vez** — apagar
> Linphone cuando estás probando WebRTC y viceversa, así sabés cuál
> respondió.

## 5. Troubleshooting

### 5.1 Registration failed (rojo en softphone)

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip set logger on'
# Reintentar registro desde el softphone
$ docker logs --tail 30 verbara-asterisk
```

Buscar:
- `407 Proxy Authentication Required` con `400 Bad Request` después → password mal.
- `404 Not Found` → username no existe (no provisionaste el endpoint).
- `Connection refused` → no llega al server. Validá:
  - El server está reachable: `ping 192.168.40.100`.
  - Puerto 5060 abierto: `nc -uvz 192.168.40.100 5060 < /dev/null`.
  - Firewall del host no bloquea desde tu device.

### 5.2 Registrado pero la llamada no entra

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoint agente1'
```

Buscar:
- `Status: In use` → el endpoint está atendiendo otra llamada.
- `Status: Not in use` → debería sonar. Si no suena:
  - El softphone tiene "Do Not Disturb" activado.
  - El audio output está muteado.

### 5.3 Llamada entra pero sin audio

Caso clásico de NAT/RTP. Si estás en la misma LAN que el server:
```bash
$ docker exec verbara-asterisk asterisk -rx 'rtcp set debug on'
# Reintentar llamada → ver paquetes RTCP en el log
```

Si no aparecen paquetes desde tu device → firewall del device bloquea
los puertos del rango RTP (en LAN no debería pasar pero algunos
antivirus son agresivos).

Más detalles en [08-troubleshooting-sip.md](../../docs/manuales/smb/08-troubleshooting-sip.md) §1.

## 6. Cleanup post-test

Apagar los softphones para que no consuman un "contact slot" en el
endpoint. Si querés borrar las credenciales del PJSIP:

```bash
$ curl -X DELETE \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: platform" \
    http://192.168.40.100:5000/api/v1/admin/agents/agente1/webrtc
```

> No es necesario hacerlo después de cada test — los endpoints son
> idempotentes y se pueden reusar.

## 7. Comparativa Linphone vs Zoiper vs WebRTC para tests

| Aspecto | Linphone | Zoiper | WebRTC Web |
|---|---|---|---|
| Multiplataforma | ✓ | ✓ | ✓ (browser) |
| Open source | ✓ | ✗ | ✓ |
| Codec Opus | ✓ | ✓ (pago) | ✓ (default) |
| Setup time | 3 min | 2 min | 0 (ya provisionado) |
| Reproduce el flujo real del agente | parcial | parcial | **sí** |
| Útil para isolation testing | ✓ | ✓ | ✗ (requiere todo) |

**Recomendación:** Linphone para testing/troubleshooting (más simple +
open source). WebRTC para validación final del agente real.
