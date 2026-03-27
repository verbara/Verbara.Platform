-- ============================================================
-- Demo Asterisk Seed Data
-- Runs after Platform migrations 001-006 create the Realtime tables
-- ============================================================

-- WebRTC-ready extensions: ventas team (2001-2003)
INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media, callerid, webrtc, dtmf_mode, rtp_symmetric, force_rport, rewrite_contact, ice_support, media_encryption)
VALUES
    ('2001', 'transport-wss', '2001', '2001', 'default', 'all', 'opus,ulaw,alaw', 'no', '"Maria Garcia" <2001>', 'yes', 'rfc4733', 'yes', 'yes', 'yes', 'yes', 'dtls'),
    ('2002', 'transport-wss', '2002', '2002', 'default', 'all', 'opus,ulaw,alaw', 'no', '"Carlos Lopez" <2002>', 'yes', 'rfc4733', 'yes', 'yes', 'yes', 'yes', 'dtls'),
    ('2003', 'transport-wss', '2003', '2003', 'default', 'all', 'opus,ulaw,alaw', 'no', '"Ana Martinez" <2003>', 'yes', 'rfc4733', 'yes', 'yes', 'yes', 'yes', 'dtls')
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_auths (id, auth_type, password, username) VALUES
    ('2001', 'userpass', 'demo2001', '2001'),
    ('2002', 'userpass', 'demo2002', '2002'),
    ('2003', 'userpass', 'demo2003', '2003')
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency) VALUES
    ('2001', 1, 'yes', 60),
    ('2002', 1, 'yes', 60),
    ('2003', 1, 'yes', 60)
ON CONFLICT (id) DO NOTHING;

-- WebRTC-ready extensions: soporte team (3001-3003)
INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media, callerid, webrtc, dtmf_mode, rtp_symmetric, force_rport, rewrite_contact, ice_support, media_encryption)
VALUES
    ('3001', 'transport-wss', '3001', '3001', 'default', 'all', 'opus,ulaw,alaw', 'no', '"Pedro Ruiz" <3001>', 'yes', 'rfc4733', 'yes', 'yes', 'yes', 'yes', 'dtls'),
    ('3002', 'transport-wss', '3002', '3002', 'default', 'all', 'opus,ulaw,alaw', 'no', '"Lucia Fernandez" <3002>', 'yes', 'rfc4733', 'yes', 'yes', 'yes', 'yes', 'dtls'),
    ('3003', 'transport-wss', '3003', '3003', 'default', 'all', 'opus,ulaw,alaw', 'no', '"Demo Agent" <3003>', 'yes', 'rfc4733', 'yes', 'yes', 'yes', 'yes', 'dtls')
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_auths (id, auth_type, password, username) VALUES
    ('3001', 'userpass', 'demo3001', '3001'),
    ('3002', 'userpass', 'demo3002', '3002'),
    ('3003', 'userpass', 'demo3003', '3003')
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency) VALUES
    ('3001', 1, 'yes', 60),
    ('3002', 1, 'yes', 60),
    ('3003', 1, 'yes', 60)
ON CONFLICT (id) DO NOTHING;

-- Queues: sales + support
INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen) VALUES
    ('sales', 'ringall', 15, 'no', 10, 20, 0),
    ('support', 'leastrecent', 20, 'no', 15, 20, 0)
ON CONFLICT DO NOTHING;

-- Queue members: sales
INSERT INTO queue_members (queue_name, interface, membername, penalty) VALUES
    ('sales', 'PJSIP/2001', 'Maria Garcia', 0),
    ('sales', 'PJSIP/2002', 'Carlos Lopez', 0),
    ('sales', 'PJSIP/2003', 'Ana Martinez', 1)
ON CONFLICT DO NOTHING;

-- Queue members: support
INSERT INTO queue_members (queue_name, interface, membername, penalty) VALUES
    ('support', 'PJSIP/3001', 'Pedro Ruiz', 0),
    ('support', 'PJSIP/3002', 'Lucia Fernandez', 0),
    ('support', 'PJSIP/3003', 'Demo Agent', 1)
ON CONFLICT DO NOTHING;

-- IVR queues (for Spanish IVR menu destinations)
INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen, musiconhold) VALUES
    ('ventas-nuevos', 'ringall', 15, 'no', 2, 30, 10, 'default'),
    ('ventas-existentes', 'leastrecent', 15, 'no', 2, 30, 10, 'default'),
    ('soporte-urgente', 'ringall', 15, 'no', 2, 30, 10, 'default'),
    ('soporte-general', 'leastrecent', 15, 'no', 2, 30, 10, 'default'),
    ('facturacion', 'ringall', 15, 'no', 2, 30, 10, 'default'),
    ('rrhh', 'ringall', 15, 'no', 2, 30, 10, 'default')
ON CONFLICT DO NOTHING;

-- IVR queue members (virtual agents via Local channels)
INSERT INTO queue_members (queue_name, interface, membername, penalty) VALUES
    ('ventas-nuevos', 'Local/maria@virtual-agent', 'Maria', 0),
    ('ventas-nuevos', 'Local/carlos@virtual-agent', 'Carlos', 0),
    ('ventas-existentes', 'Local/ana@virtual-agent', 'Ana', 0),
    ('ventas-existentes', 'Local/pedro@virtual-agent', 'Pedro', 0),
    ('soporte-urgente', 'Local/carlos@virtual-agent', 'Carlos', 0),
    ('soporte-urgente', 'Local/lucia@virtual-agent', 'Lucia', 0),
    ('soporte-general', 'Local/maria@virtual-agent', 'Maria', 0),
    ('soporte-general', 'Local/ana@virtual-agent', 'Ana', 0),
    ('facturacion', 'Local/pedro@virtual-agent', 'Pedro', 0),
    ('facturacion', 'Local/lucia@virtual-agent', 'Lucia', 0),
    ('rrhh', 'Local/ana@virtual-agent', 'Ana', 0),
    ('rrhh', 'Local/carlos@virtual-agent', 'Carlos', 0)
ON CONFLICT DO NOTHING;

-- PSTN trunk endpoint (connects to pstn-emulator container)
INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow, direct_media, rtp_symmetric, force_rport, callerid)
VALUES ('pstn-trunk', 'transport-udp', 'pstn-trunk', 'from-pstn', 'all', 'ulaw,alaw', 'no', 'yes', 'yes', '"Demo PBX" <8888>')
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_aors (id, max_contacts, contact, qualify_frequency)
VALUES ('pstn-trunk', 1, 'sip:pstn-emulator:5060', 30)
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_endpoint_id_ips (id, endpoint, match)
VALUES ('pstn-trunk-ip', 'pstn-trunk', 'pstn-emulator')
ON CONFLICT (id) DO NOTHING;
