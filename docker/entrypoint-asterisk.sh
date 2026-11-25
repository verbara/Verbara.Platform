#!/bin/sh
PJSIP_CONF="/etc/asterisk/pjsip.conf"
RTP_CONF="/etc/asterisk/rtp.conf"

MANAGER_CONF="/etc/asterisk/manager.conf"
ARI_CONF="/etc/asterisk/ari.conf"
HTTP_CONF="/etc/asterisk/http.conf"
# Asterisk's res_config_pgsql.so reads res_pgsql.conf (NOT res_config_pgsql.conf) —
# using the wrong filename left realtime silently unconfigured, so Asterisk never
# loaded ps_endpoints/ps_endpoint_id_ips/queues and inbound voice could not work.
PGSQL_CONF="/etc/asterisk/res_pgsql.conf"

# ── Render runtime config from versioned templates ───────────────────────────
# In bind-mounted (docker) deploys, each <conf>.template is the VERSIONED source
# (placeholders only); the live <conf> is REGENERATED from it on every boot and is
# gitignored, so the secrets/IPs the sed blocks below inject never pollute the
# committed tree (and real secrets can never be committed by accident). Stacks that
# supply the final <conf> directly (e.g. the k8s asterisk ConfigMap — no template
# present) are left completely untouched: the loop is a no-op there.
for _b in manager ari http res_pgsql; do
    if [ -f "/etc/asterisk/${_b}.conf.template" ]; then
        cp "/etc/asterisk/${_b}.conf.template" "/etc/asterisk/${_b}.conf"
    fi
done

# Inject AMI/ARI passwords from environment so .env secrets propagate into Asterisk
if [ -n "$AMI_PASSWORD" ] && [ -f "$MANAGER_CONF" ]; then
    sed -i "s/^secret = .*/secret = $AMI_PASSWORD/" "$MANAGER_CONF"
    echo "[entrypoint] AMI password injected from environment"
fi
if [ -n "$ARI_PASSWORD" ] && [ -f "$ARI_CONF" ]; then
    sed -i "s/^password = .*/password = $ARI_PASSWORD/" "$ARI_CONF"
    echo "[entrypoint] ARI password injected from environment"
fi

# Realtime Postgres rewiring. Required when Asterisk runs with
# `network_mode: host` (reference SMB) — the docker DNS name `postgres` is
# unreachable from host network, so dbhost has to be the loopback / LAN IP
# where Postgres is bound. Optional in all other stacks (defaults preserved
# when env vars unset).
#
# set_realtime KEY VALUE writes `KEY = VALUE` so that Postgres receives VALUE
# byte for byte. Three readers stand between: res_config_pgsql pastes each
# value into its libpq connection string unquoted ("dbname=%s"), where a
# backslash takes the next character literally and whitespace would end the
# value (so \ and ' get a backslash, and a value holding whitespace is
# single-quoted); Asterisk's config parser, where a bare ; starts a comment
# (so ; becomes \;); and sed's replacement (so \, & and the | delimiter get a
# backslash). A plain value is written unchanged.
set_realtime() {
    _rt=$(printf '%s' "$2" | sed -e "s/[\\\\']/\\\\&/g")
    case "$_rt" in
        *" "*|*"	"*) _rt="'$_rt'" ;;
    esac
    _rt=$(printf '%s' "$_rt" | sed -e 's/[;]/\\&/g' -e 's/[\\&|]/\\&/g')
    sed -i "s|^$1 = .*|$1 = $_rt|" "$PGSQL_CONF"
}
if [ -n "$PG_REALTIME_HOST" ] && [ -f "$PGSQL_CONF" ]; then
    set_realtime dbhost "$PG_REALTIME_HOST"
    echo "[entrypoint] Realtime dbhost set to $PG_REALTIME_HOST"
fi
if [ -n "$PG_REALTIME_PORT" ] && [ -f "$PGSQL_CONF" ]; then
    set_realtime dbport "$PG_REALTIME_PORT"
fi
if [ -n "$PG_REALTIME_DB" ] && [ -f "$PGSQL_CONF" ]; then
    set_realtime dbname "$PG_REALTIME_DB"
fi
if [ -n "$PG_REALTIME_USER" ] && [ -f "$PGSQL_CONF" ]; then
    set_realtime dbuser "$PG_REALTIME_USER"
fi
if [ -n "$PG_REALTIME_PASSWORD" ] && [ -f "$PGSQL_CONF" ]; then
    set_realtime dbpass "$PG_REALTIME_PASSWORD"
fi

if [ -n "$EXTERNAL_IP" ] && [ -f "$PJSIP_CONF" ]; then
    # Replace ${EXTERNAL_IP} placeholder with actual IP
    cp "$PJSIP_CONF" /tmp/_pjsip_tmp
    sed "s/\${EXTERNAL_IP}/$EXTERNAL_IP/g" /tmp/_pjsip_tmp > "$PJSIP_CONF"
    rm -f /tmp/_pjsip_tmp
    echo "[entrypoint] EXTERNAL_IP set to $EXTERNAL_IP"
elif [ -f "$PJSIP_CONF" ]; then
    # No EXTERNAL_IP: remove external_media/signaling lines so transports still work
    sed -i '/external_media_address/d' "$PJSIP_CONF"
    sed -i '/external_signaling_address/d' "$PJSIP_CONF"
    echo "[entrypoint] No EXTERNAL_IP set — removed external address lines (localhost-only mode)"
fi

# RTP port range from the environment (RTP_PORT_START / RTP_PORT_END, as in
# .env.reference-smb). Unset keeps rtp.conf's own range. The range must match what
# the host lets through: the compose port mapping, or the firewall with host networking.
if [ -n "$RTP_PORT_START" ] || [ -n "$RTP_PORT_END" ]; then
    if [ -f "$RTP_CONF" ] && [ -w "$RTP_CONF" ] && [ -w "$(dirname "$RTP_CONF")" ]; then
        [ -n "$RTP_PORT_START" ] && sed -i "s/^rtpstart = .*/rtpstart = $RTP_PORT_START/" "$RTP_CONF"
        [ -n "$RTP_PORT_END" ] && sed -i "s/^rtpend = .*/rtpend = $RTP_PORT_END/" "$RTP_CONF"
        echo "[entrypoint] RTP range $(sed -n 's/^rtpstart = //p' "$RTP_CONF")-$(sed -n 's/^rtpend = //p' "$RTP_CONF")"
    else
        echo "[entrypoint] WARNING: RTP_PORT_START/RTP_PORT_END set but $RTP_CONF is not writable — the range stays as the file says"
    fi
fi

if [ -n "$EXTERNAL_IP" ] && [ -f "$RTP_CONF" ]; then
    CONTAINER_IP=$(hostname -i | awk '{print $1}')
    cat >> "$RTP_CONF" <<EOF

[ice_host_candidates]
${CONTAINER_IP} => ${EXTERNAL_IP}
EOF
    echo "[entrypoint] ice_host_candidates: ${CONTAINER_IP} => ${EXTERNAL_IP}"
fi

# ── TLS cert for WSS/HTTPS (port 8089) ───────────────────────────────────────
# WebRTC softphones (Phase 3A) REGISTER over wss://host:8089 — Asterisk's HTTP
# TLS listener (http.conf tlsbindaddr 0.0.0.0:8089) needs a usable cert. Prefer
# the http.conf default dir (/etc/asterisk/keys); when that is a read-only /
# root-owned bind mount (reference-smb mounts ./asterisk-config:/etc/asterisk,
# whose keys/ subdir is root-owned), generating there fails — fall back to an
# asterisk-writable runtime dir and repoint http.conf. A real mounted cert
# (e.g. demo's :ro certs, or Let's Encrypt in production) is left untouched.
DEFAULT_KEYS="/etc/asterisk/keys"
RUNTIME_KEYS="/var/lib/asterisk/keys"
CERT_CN="${EXTERNAL_IP:-$(hostname -i 2>/dev/null | awk '{print $1}')}"
[ -z "$CERT_CN" ] && CERT_CN="localhost"

if /usr/local/bin/gen-asterisk-cert.sh "$DEFAULT_KEYS" "$CERT_CN" 2>/dev/null; then
    echo "[entrypoint] TLS cert ready at $DEFAULT_KEYS (CN=$CERT_CN)"
else
    /usr/local/bin/gen-asterisk-cert.sh "$RUNTIME_KEYS" "$CERT_CN"
    if [ -f "$HTTP_CONF" ]; then
        sed -i "s#^tlscertfile = .*#tlscertfile = $RUNTIME_KEYS/asterisk.pem#" "$HTTP_CONF"
        sed -i "s#^tlsprivatekey = .*#tlsprivatekey = $RUNTIME_KEYS/asterisk.key#" "$HTTP_CONF"
    fi
    echo "[entrypoint] TLS cert ready at $RUNTIME_KEYS (CN=$CERT_CN), http.conf repointed"
fi

exec /usr/sbin/asterisk -f
