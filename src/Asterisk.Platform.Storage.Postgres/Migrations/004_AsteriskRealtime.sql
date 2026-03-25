-- =============================================================================
-- Asterisk.Platform — Asterisk Realtime Tables + Agent SIP Fields (004)
-- =============================================================================

-- ---------------------------------------------------------------------------
-- PJSIP Endpoints
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
    dtmf_mode VARCHAR(7) DEFAULT 'rfc4733',
    callerid VARCHAR(40),
    webrtc VARCHAR(3) DEFAULT 'no',
    max_audio_streams INT DEFAULT 1
);

CREATE TABLE IF NOT EXISTS ps_auths (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_type VARCHAR(10) DEFAULT 'userpass',
    username VARCHAR(40), password VARCHAR(80)
);

CREATE TABLE IF NOT EXISTS ps_aors (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    max_contacts INT DEFAULT 1,
    remove_existing VARCHAR(3) DEFAULT 'yes',
    qualify_frequency INT DEFAULT 30,
    minimum_expiration INT DEFAULT 60,
    default_expiration INT DEFAULT 3600
);

CREATE TABLE IF NOT EXISTS ps_contacts (
    id VARCHAR(255) NOT NULL PRIMARY KEY,
    uri VARCHAR(255), expiration_time BIGINT,
    qualify_frequency INT, endpoint_name VARCHAR(40),
    user_agent VARCHAR(255), reg_server VARCHAR(20)
);

CREATE TABLE IF NOT EXISTS ps_registrations (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    client_uri VARCHAR(255), server_uri VARCHAR(255),
    transport VARCHAR(40), outbound_auth VARCHAR(40),
    retry_interval INT DEFAULT 60, expiration INT DEFAULT 3600,
    contact_user VARCHAR(40)
);

-- ---------------------------------------------------------------------------
-- Realtime Queues
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS rt_queues (
    name VARCHAR(128) NOT NULL PRIMARY KEY,
    musiconhold VARCHAR(128) DEFAULT 'default',
    strategy VARCHAR(128) DEFAULT 'rrmemory',
    timeout INT DEFAULT 30, retry INT DEFAULT 5,
    wrapuptime INT DEFAULT 15, maxlen INT DEFAULT 0,
    servicelevel INT DEFAULT 20,
    joinempty VARCHAR(128) DEFAULT 'yes',
    leavewhenempty VARCHAR(128) DEFAULT 'no',
    ringinuse VARCHAR(3) DEFAULT 'no',
    autopause VARCHAR(3) DEFAULT 'no'
);

CREATE TABLE IF NOT EXISTS rt_queue_members (
    uniqueid SERIAL PRIMARY KEY,
    membername VARCHAR(40), queue_name VARCHAR(128),
    interface VARCHAR(128), state_interface VARCHAR(128),
    penalty INT DEFAULT 0, paused INT DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_rtqm_queue ON rt_queue_members (queue_name);
CREATE INDEX IF NOT EXISTS idx_rtqm_iface ON rt_queue_members (interface);

-- ---------------------------------------------------------------------------
-- Agent SIP fields
-- ---------------------------------------------------------------------------

ALTER TABLE agents ADD COLUMN IF NOT EXISTS extension VARCHAR(20);
ALTER TABLE agents ADD COLUMN IF NOT EXISTS sip_password VARCHAR(80);
