-- ============================================================================
-- Demo Platform Seed — Users, Agents, Queues, API Keys, Auth Config, Channels
-- Run AFTER platform-api is healthy (migrations + Pro EnsureSchema complete)
-- ============================================================================

-- ── Admin User ──────────────────────────────────────────────────────────────
INSERT INTO users (tenant_id, user_id, email, display_name, role, status, password_hash, created_at)
VALUES ('demo', 'demo-user-admin', 'admin@demo.local', 'Demo Admin', 2, 0,
        '$2a$11$RJjd/LqOVr0COg3Ki.atFeBytYje1vuyPcsO2y8iLXZX8f0eyUx8O', NOW())
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- ── Supervisor User ─────────────────────────────────────────────────────────
INSERT INTO users (tenant_id, user_id, email, display_name, role, status, password_hash, created_at)
VALUES ('demo', 'demo-user-supervisor', 'supervisor@demo.local', 'Demo Supervisor', 1, 0,
        '$2a$11$aovX58nHv17W71BqlnPgV.xEmtdsK09NtUFPzYsiuox.7Doakr/vK', NOW())
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- ── Agent Users (6 agents mapped to SIP extensions) ─────────────────────────
INSERT INTO users (tenant_id, user_id, email, display_name, role, status, password_hash, created_at) VALUES
    ('demo', 'demo-user-maria',  'maria.garcia@demo.local',    'Maria Garcia',    0, 0, '$2a$11$iOmIatF1FfHuvA0QS3R4zup/Am4ouJyvquW8FQEoM0pdsvLfBVpeS', NOW()),
    ('demo', 'demo-user-carlos', 'carlos.lopez@demo.local',    'Carlos Lopez',    0, 0, '$2a$11$iOmIatF1FfHuvA0QS3R4zup/Am4ouJyvquW8FQEoM0pdsvLfBVpeS', NOW()),
    ('demo', 'demo-user-ana',    'ana.martinez@demo.local',     'Ana Martinez',    0, 0, '$2a$11$iOmIatF1FfHuvA0QS3R4zup/Am4ouJyvquW8FQEoM0pdsvLfBVpeS', NOW()),
    ('demo', 'demo-user-pedro',  'pedro.ruiz@demo.local',       'Pedro Ruiz',      0, 0, '$2a$11$iOmIatF1FfHuvA0QS3R4zup/Am4ouJyvquW8FQEoM0pdsvLfBVpeS', NOW()),
    ('demo', 'demo-user-lucia',  'lucia.fernandez@demo.local',  'Lucia Fernandez', 0, 0, '$2a$11$iOmIatF1FfHuvA0QS3R4zup/Am4ouJyvquW8FQEoM0pdsvLfBVpeS', NOW()),
    ('demo', 'demo-user-demo',   'demo.agent@demo.local',       'Demo Agent',      0, 0, '$2a$11$iOmIatF1FfHuvA0QS3R4zup/Am4ouJyvquW8FQEoM0pdsvLfBVpeS', NOW())
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- ── API Keys (SHA-256 hashed) ───────────────────────────────────────────────
-- Role enum: Agent=0, Supervisor=1, Admin=2
INSERT INTO api_keys (tenant_id, key_id, key_hash, name, scopes, user_id, created_at) VALUES
    ('demo', 'demo-key-admin',      '0de55245c225238646d6fdb99a60b6971bf5eb0b0280237830d66e0a8b54b4ac', 'Demo Admin Key',      '["*"]', 'demo-user-admin',      NOW()),
    ('demo', 'demo-key-supervisor', 'ab7de34f6500694130faf3da4fb7356972d6f11349cd8bb42c7e0be189884f59', 'Demo Supervisor Key',  '["*"]', 'demo-user-supervisor', NOW()),
    ('demo', 'demo-key-maria',      '44facecfa1be86fd2e67477a0eb04811973449ad16ce1c0f9dc1e20c5823bac2', 'Demo Maria Key',      '["*"]', 'demo-user-maria',      NOW()),
    ('demo', 'demo-key-carlos',     '6ee67e283bf90a40a5f16b132aa0a1937d2d35d9a2e79f1b34f1e357fce216e2', 'Demo Carlos Key',     '["*"]', 'demo-user-carlos',     NOW()),
    ('demo', 'demo-key-ana',        '2f3dd62a58d23d30b8e97b221b7b7a386e3b5ba14ee27a9f73b5a253aa74a95f', 'Demo Ana Key',        '["*"]', 'demo-user-ana',        NOW()),
    ('demo', 'demo-key-pedro',      'c0f226223b6c8f0a09ff5da8c1b1d976e0d2d4c8e435822071c84243fb02478b', 'Demo Pedro Key',      '["*"]', 'demo-user-pedro',      NOW()),
    ('demo', 'demo-key-lucia',      'f2b5ca4427c22424144f5f6ca9bc9ceecb6fd196de5a2dde58ca5b4bec0fc72b', 'Demo Lucia Key',      '["*"]', 'demo-user-lucia',      NOW()),
    ('demo', 'demo-key-demo',       '7aab7bb6df2641a4860f1f55108e23c5a5a2031d7cf1a3591ab850c23275c800', 'Demo Agent Key',      '["*"]', 'demo-user-demo',       NOW())
ON CONFLICT (tenant_id, key_id) DO NOTHING;

-- ── Agents ──────────────────────────────────────────────────────────────────
INSERT INTO agents (tenant_id, agent_id, user_id, display_name, state, skills, extension, sip_password, created_at) VALUES
    ('demo', 'demo-agent-maria',  'demo-user-maria',  'Maria Garcia',    1, '["sales"]',   '2001', 'demo2001', NOW()),
    ('demo', 'demo-agent-carlos', 'demo-user-carlos', 'Carlos Lopez',    1, '["sales"]',   '2002', 'demo2002', NOW()),
    ('demo', 'demo-agent-ana',    'demo-user-ana',    'Ana Martinez',    1, '["sales"]',   '2003', 'demo2003', NOW()),
    ('demo', 'demo-agent-pedro',  'demo-user-pedro',  'Pedro Ruiz',      1, '["support"]', '3001', 'demo3001', NOW()),
    ('demo', 'demo-agent-lucia',  'demo-user-lucia',  'Lucia Fernandez', 1, '["support"]', '3002', 'demo3002', NOW()),
    ('demo', 'demo-agent-demo',   'demo-user-demo',   'Demo Agent',      1, '["support"]', '3003', 'demo3003', NOW())
ON CONFLICT (tenant_id, agent_id) DO NOTHING;

-- ── Queues ──────────────────────────────────────────────────────────────────
INSERT INTO queue_configs (tenant_id, queue_id, name, is_active, created_at) VALUES
    ('demo', 'demo-queue-sales',   'Sales',   true, NOW()),
    ('demo', 'demo-queue-support', 'Support', true, NOW())
ON CONFLICT (tenant_id, queue_id) DO NOTHING;

-- ── Channel Config (WebChat active for setup wizard) ────────────────────────
-- channel enum: WebChat=2
INSERT INTO tenant_channel_configs (tenant_id, channel, is_active, credentials) VALUES
    ('demo', 2, true, '{}')
ON CONFLICT (tenant_id, channel) DO NOTHING;

-- ── Tenant Auth Config ──────────────────────────────────────────────────────
INSERT INTO tenant_auth_config (
    tenant_id, mfa_policy, password_min_length, password_require_uppercase,
    password_require_number, password_require_special, lockout_threshold,
    lockout_duration_minutes, session_idle_timeout_minutes,
    session_absolute_timeout_hours, updated_at
) VALUES (
    'demo', 'optional', 12, true, true, false, 5, 15, 30, 12, NOW()
) ON CONFLICT (tenant_id) DO NOTHING;
