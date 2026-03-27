#!/bin/sh
PJSIP_CONF="/etc/asterisk/pjsip.conf"
RTP_CONF="/etc/asterisk/rtp.conf"

if [ -n "$EXTERNAL_IP" ] && [ -f "$PJSIP_CONF" ]; then
    cp "$PJSIP_CONF" /tmp/_pjsip_tmp
    sed "s/\${EXTERNAL_IP}/$EXTERNAL_IP/g" /tmp/_pjsip_tmp > "$PJSIP_CONF"
    rm -f /tmp/_pjsip_tmp
fi

if [ -n "$EXTERNAL_IP" ] && [ -f "$RTP_CONF" ]; then
    CONTAINER_IP=$(hostname -i | awk '{print $1}')
    cat >> "$RTP_CONF" <<EOF

[ice_host_candidates]
${CONTAINER_IP} => ${EXTERNAL_IP}
EOF
    echo "[entrypoint] ice_host_candidates: ${CONTAINER_IP} => ${EXTERNAL_IP}"
else
    echo "[entrypoint] No EXTERNAL_IP set, WebRTC from remote machines may not work"
fi

exec /usr/sbin/asterisk -f
