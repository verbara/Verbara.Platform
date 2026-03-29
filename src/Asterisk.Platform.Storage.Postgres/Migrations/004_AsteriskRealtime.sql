-- =============================================================================
-- Asterisk.Platform — Asterisk Realtime Tables + Agent SIP Fields (004)
-- =============================================================================
-- NOTE: PJSIP tables (ps_endpoints, ps_auths, ps_aors, ps_contacts,
-- ps_registrations) and queue tables (queues, queue_members) are created by
-- the authoritative Asterisk Realtime schema (000_asterisk_realtime.sql in
-- demo, or equivalent in production). This migration only creates them as
-- a fallback with CREATE IF NOT EXISTS for standalone deployments.
-- The authoritative schema has the full Asterisk 22 column set (~150 cols
-- for ps_endpoints); this fallback has a minimal subset.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- PJSIP Endpoints (fallback — authoritative schema preferred)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS ps_endpoints (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    transport VARCHAR(40), aors VARCHAR(200), auth VARCHAR(40),
    context VARCHAR(40) DEFAULT 'from-internal',
    disallow VARCHAR(200) DEFAULT 'all',
    allow VARCHAR(200) DEFAULT 'opus,g722,ulaw,alaw',
    direct_media VARCHAR(3) DEFAULT 'no',
    force_rport VARCHAR(3) DEFAULT 'yes',
    rewrite_contact VARCHAR(3) DEFAULT 'yes',
    rtp_symmetric VARCHAR(3) DEFAULT 'yes',
    dtmf_mode VARCHAR(10) DEFAULT 'rfc4733',
    callerid VARCHAR(100),
    webrtc VARCHAR(3) DEFAULT 'no',
    max_audio_streams INTEGER DEFAULT 1
);

CREATE TABLE IF NOT EXISTS ps_auths (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_type VARCHAR(40) DEFAULT 'userpass',
    username VARCHAR(40), password VARCHAR(80)
);

CREATE TABLE IF NOT EXISTS ps_aors (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    max_contacts INTEGER DEFAULT 1,
    remove_existing VARCHAR(3) DEFAULT 'yes',
    qualify_frequency INTEGER DEFAULT 30,
    minimum_expiration INTEGER DEFAULT 60,
    default_expiration INTEGER DEFAULT 3600
);

CREATE TABLE IF NOT EXISTS ps_contacts (
    id VARCHAR(255) NOT NULL PRIMARY KEY,
    uri VARCHAR(255), expiration_time BIGINT,
    qualify_frequency INTEGER, endpoint VARCHAR(40),
    user_agent VARCHAR(255), reg_server VARCHAR(20)
);

CREATE TABLE IF NOT EXISTS ps_registrations (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    client_uri VARCHAR(255), server_uri VARCHAR(255),
    transport VARCHAR(40), outbound_auth VARCHAR(40),
    retry_interval INTEGER DEFAULT 60, expiration INTEGER DEFAULT 3600,
    contact_user VARCHAR(40)
);

-- ---------------------------------------------------------------------------
-- Realtime Queues (fallback — authoritative schema preferred)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS queues (
    name VARCHAR(128) NOT NULL PRIMARY KEY,
    musiconhold VARCHAR(128) DEFAULT 'default',
    strategy VARCHAR(128) DEFAULT 'rrmemory',
    timeout INTEGER DEFAULT 30, retry INTEGER DEFAULT 5,
    wrapuptime INTEGER DEFAULT 15, maxlen INTEGER DEFAULT 0,
    servicelevel INTEGER DEFAULT 20,
    joinempty VARCHAR(128) DEFAULT 'yes',
    leavewhenempty VARCHAR(128) DEFAULT 'no',
    ringinuse VARCHAR(3) DEFAULT 'no',
    autopause VARCHAR(3) DEFAULT 'no'
);

CREATE TABLE IF NOT EXISTS queue_members (
    queue_name VARCHAR(128) NOT NULL,
    interface VARCHAR(128) NOT NULL,
    membername VARCHAR(128),
    state_interface VARCHAR(128),
    penalty INTEGER DEFAULT 0,
    paused INTEGER DEFAULT 0,
    uniqueid VARCHAR(40) NOT NULL DEFAULT gen_random_uuid()::VARCHAR(40),
    PRIMARY KEY (queue_name, interface)
);
CREATE INDEX IF NOT EXISTS idx_qm_queue ON queue_members (queue_name);
CREATE INDEX IF NOT EXISTS idx_qm_iface ON queue_members (interface);

-- ---------------------------------------------------------------------------
-- Agent SIP fields
-- ---------------------------------------------------------------------------

ALTER TABLE agents ADD COLUMN IF NOT EXISTS extension VARCHAR(20);
ALTER TABLE agents ADD COLUMN IF NOT EXISTS sip_password VARCHAR(80);
