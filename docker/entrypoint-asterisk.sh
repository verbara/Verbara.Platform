#!/bin/sh
PJSIP_CONF="/etc/asterisk/pjsip.conf"
RTP_CONF="/etc/asterisk/rtp.conf"

MANAGER_CONF="/etc/asterisk/manager.conf"
ARI_CONF="/etc/asterisk/ari.conf"

# Inject AMI/ARI passwords from environment so .env secrets propagate into Asterisk
if [ -n "$AMI_PASSWORD" ] && [ -f "$MANAGER_CONF" ]; then
    sed -i "s/^secret = .*/secret = $AMI_PASSWORD/" "$MANAGER_CONF"
    echo "[entrypoint] AMI password injected from environment"
fi
if [ -n "$ARI_PASSWORD" ] && [ -f "$ARI_CONF" ]; then
    sed -i "s/^password = .*/password = $ARI_PASSWORD/" "$ARI_CONF"
    echo "[entrypoint] ARI password injected from environment"
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

if [ -n "$EXTERNAL_IP" ] && [ -f "$RTP_CONF" ]; then
    CONTAINER_IP=$(hostname -i | awk '{print $1}')
    cat >> "$RTP_CONF" <<EOF

[ice_host_candidates]
${CONTAINER_IP} => ${EXTERNAL_IP}
EOF
    echo "[entrypoint] ice_host_candidates: ${CONTAINER_IP} => ${EXTERNAL_IP}"
fi

exec /usr/sbin/asterisk -f
