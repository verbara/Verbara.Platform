-- =============================================================================
-- Authoritative Asterisk 22 Realtime Schema for PostgreSQL
-- =============================================================================
-- Based on PbxAdmin 001-asterisk-realtime-schema.sql + Asterisk 22 sorcery
-- fields not present in PbxAdmin. Compatible with res_config_pgsql / Sorcery.
--
-- Execution order (demo-reset.sh):
--   1. Platform migrations 001-006 run during Postgres init
--      (004 creates minimal PJSIP tables — 15 cols for ps_endpoints)
--   2. THIS FILE drops minimal tables and recreates with full schema
--   3. 008_pro_tables.sql adds Pro package tables + endpoint_profiles
--   4. 010_demo_asterisk_seed.sql inserts demo endpoints/queues
--
-- This file is the SINGLE SOURCE OF TRUTH for Asterisk Realtime table
-- definitions. All other codebases (Platform, Pro.Realtime, PbxAdmin) are
-- consumers of this schema, not creators.
--
-- Safety net: res_pgsql.conf uses requirements=createclose so any columns
-- missed here will be auto-created by Asterisk at startup.
-- =============================================================================


-- ---------------------------------------------------------------------------
-- Drop tables created by Platform migration 004 (minimal columns)
-- ---------------------------------------------------------------------------
DROP TABLE IF EXISTS ps_contacts CASCADE;
DROP TABLE IF EXISTS ps_endpoint_id_ips CASCADE;
DROP TABLE IF EXISTS ps_domain_aliases CASCADE;
DROP TABLE IF EXISTS ps_registrations CASCADE;
DROP TABLE IF EXISTS ps_aors CASCADE;
DROP TABLE IF EXISTS ps_auths CASCADE;
DROP TABLE IF EXISTS ps_endpoints CASCADE;
DROP TABLE IF EXISTS rt_queue_members CASCADE;
DROP TABLE IF EXISTS rt_queues CASCADE;
DROP TABLE IF EXISTS queue_members CASCADE;
DROP TABLE IF EXISTS queues CASCADE;


-- ---------------------------------------------------------------------------
-- PJSIP Endpoints — Asterisk 22 full sorcery schema
-- ---------------------------------------------------------------------------
-- PbxAdmin 001 (132 cols) + Asterisk 22 additions (~20 cols)
-- Total: ~152 columns. Asterisk only queries columns it knows about;
-- extra columns are harmless.

CREATE TABLE ps_endpoints (
    id                                  VARCHAR(40) NOT NULL PRIMARY KEY,
    transport                           VARCHAR(40),
    aors                                VARCHAR(200),
    auth                                VARCHAR(40),
    context                             VARCHAR(40) DEFAULT 'default',
    disallow                            VARCHAR(200) DEFAULT 'all',
    allow                               VARCHAR(200) DEFAULT 'ulaw,alaw',
    direct_media                        VARCHAR(3) DEFAULT 'no',
    connected_line_method               VARCHAR(40),
    direct_media_method                 VARCHAR(40),
    direct_media_glare_mitigation       VARCHAR(40),
    disable_direct_media_on_nat         VARCHAR(3),
    dtmf_mode                           VARCHAR(10) DEFAULT 'rfc4733',
    external_media_address              VARCHAR(40),
    force_rport                         VARCHAR(3) DEFAULT 'yes',
    ice_support                         VARCHAR(3),
    identify_by                         VARCHAR(80),
    mailboxes                           VARCHAR(200),
    moh_suggest                         VARCHAR(40),
    outbound_auth                       VARCHAR(40),
    outbound_proxy                      VARCHAR(256),
    rewrite_contact                     VARCHAR(3) DEFAULT 'yes',
    rtp_ipv6                            VARCHAR(3),
    rtp_symmetric                       VARCHAR(3) DEFAULT 'yes',
    send_diversion                      VARCHAR(3),
    send_pai                            VARCHAR(3),
    send_rpid                           VARCHAR(3),
    timers_min_se                       INTEGER,
    timers                              VARCHAR(40),
    timers_sess_expires                 INTEGER,
    callerid                            VARCHAR(100),
    callerid_privacy                    VARCHAR(40),
    callerid_tag                        VARCHAR(40),
    "100rel"                            VARCHAR(40),
    aggregate_mwi                       VARCHAR(3),
    trust_id_inbound                    VARCHAR(3),
    trust_id_outbound                   VARCHAR(3),
    use_ptime                           VARCHAR(3),
    use_avpf                            VARCHAR(3),
    media_encryption                    VARCHAR(40),
    inband_progress                     VARCHAR(3),
    call_group                          VARCHAR(200),
    pickup_group                        VARCHAR(200),
    named_call_group                    VARCHAR(200),
    named_pickup_group                  VARCHAR(200),
    device_state_busy_at                INTEGER,
    fax_detect                          VARCHAR(3),
    t38_udptl                           VARCHAR(3),
    t38_udptl_ec                        VARCHAR(40),
    t38_udptl_maxdatagram               INTEGER,
    t38_udptl_nat                       VARCHAR(3),
    t38_udptl_ipv6                      VARCHAR(3),
    tone_zone                           VARCHAR(40),
    language                            VARCHAR(10),
    one_touch_recording                 VARCHAR(3),
    record_on_feature                   VARCHAR(40),
    record_off_feature                  VARCHAR(40),
    rtp_engine                          VARCHAR(40),
    allow_transfer                      VARCHAR(3),
    allow_subscribe                     VARCHAR(3),
    sdp_owner                           VARCHAR(40),
    sdp_session                         VARCHAR(40),
    tos_audio                           VARCHAR(10),
    tos_video                           VARCHAR(10),
    cos_audio                           INTEGER,
    cos_video                           INTEGER,
    sub_min_expiry                      INTEGER,
    from_domain                         VARCHAR(40),
    from_user                           VARCHAR(40),
    mwi_from_user                       VARCHAR(40),
    dtls_verify                         VARCHAR(40),
    dtls_rekey                          VARCHAR(40),
    dtls_cert_file                      VARCHAR(200),
    dtls_private_key                    VARCHAR(200),
    dtls_cipher                         VARCHAR(200),
    dtls_ca_file                        VARCHAR(200),
    dtls_ca_path                        VARCHAR(200),
    dtls_setup                          VARCHAR(40),
    srtp_tag_32                         VARCHAR(3),
    media_address                       VARCHAR(40),
    redirect_method                     VARCHAR(40),
    set_var                             TEXT,
    message_context                     VARCHAR(40),
    force_avp                           VARCHAR(3),
    media_use_received_transport        VARCHAR(3),
    accountcode                         VARCHAR(80),
    user_eq_phone                       VARCHAR(3),
    moh_passthrough                     VARCHAR(3),
    media_encryption_optimistic         VARCHAR(3),
    rpid_immediate                      VARCHAR(3),
    g726_non_standard                   VARCHAR(3),
    rtp_keepalive                       INTEGER,
    rtp_timeout                         INTEGER,
    rtp_timeout_hold                    INTEGER,
    bind_rtp_to_media_address           VARCHAR(3),
    voicemail_extension                 VARCHAR(40),
    mwi_subscribe_replaces_unsolicited  VARCHAR(3),
    deny                                VARCHAR(95),
    permit                              VARCHAR(95),
    acl                                 VARCHAR(40),
    contact_deny                        VARCHAR(95),
    contact_permit                      VARCHAR(95),
    contact_acl                         VARCHAR(40),
    subscribe_context                   VARCHAR(40),
    fax_detect_timeout                  INTEGER,
    contact_user                        VARCHAR(80),
    preferred_codec_only                VARCHAR(3),
    asymmetric_rtp_codec                VARCHAR(3),
    rtcp_mux                            VARCHAR(3),
    allow_overlap                       VARCHAR(3),
    refer_blind_progress                VARCHAR(3),
    notify_early_inuse_ringing          VARCHAR(3),
    max_audio_streams                   INTEGER,
    max_video_streams                   INTEGER,
    webrtc                              VARCHAR(3),
    dtls_fingerprint                    VARCHAR(40),
    incoming_mwi_mailbox                VARCHAR(40),
    bundle                              VARCHAR(3),
    dtls_auto_generate_cert             VARCHAR(3),
    suppress_q850_reason_headers        VARCHAR(3),
    ignore_uri_user_options             VARCHAR(3),
    trust_connected_line                VARCHAR(3),
    send_connected_line                 VARCHAR(3),
    ignore_183_without_sdp              VARCHAR(3),
    follow_early_media_fork             VARCHAR(3),
    accept_multiple_sdp_answers         VARCHAR(3),
    max_contacts                        INTEGER DEFAULT 1,
    -- Asterisk 22 additions (not in PbxAdmin 001)
    tenantid                            VARCHAR(40),
    stir_shaken                         VARCHAR(3),
    stir_shaken_profile                 VARCHAR(80),
    send_aoc                            VARCHAR(3),
    security_negotiation                VARCHAR(40),
    security_mechanisms                 VARCHAR(512),
    geoloc_incoming_call_profile        VARCHAR(80),
    geoloc_outgoing_call_profile        VARCHAR(80),
    incoming_call_offer_pref            VARCHAR(128),
    outgoing_call_offer_pref            VARCHAR(128),
    codec_prefs_incoming_offer          VARCHAR(128),
    codec_prefs_outgoing_offer          VARCHAR(128),
    codec_prefs_incoming_answer         VARCHAR(128),
    codec_prefs_outgoing_answer         VARCHAR(128),
    overlap_context                     VARCHAR(40),
    suppress_moh_on_sendonly            VARCHAR(3),
    send_history_info                   VARCHAR(3),
    allow_unauthenticated_options       VARCHAR(3),
    follow_redirect_methods             VARCHAR(200),
    t38_bind_udptl_to_media_address     VARCHAR(3)
);


-- ---------------------------------------------------------------------------
-- PJSIP Auth
-- ---------------------------------------------------------------------------

CREATE TABLE ps_auths (
    id              VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_type       VARCHAR(40) DEFAULT 'userpass',
    nonce_lifetime  INTEGER,
    md5_cred        VARCHAR(40),
    password        VARCHAR(80),
    realm           VARCHAR(40),
    username        VARCHAR(40),
    refresh_token   VARCHAR(255),
    oauth_clientid  VARCHAR(255),
    oauth_secret    VARCHAR(255)
);


-- ---------------------------------------------------------------------------
-- PJSIP AORs (Address of Record)
-- ---------------------------------------------------------------------------

CREATE TABLE ps_aors (
    id                      VARCHAR(40) NOT NULL PRIMARY KEY,
    contact                 VARCHAR(255),
    default_expiration      INTEGER DEFAULT 3600,
    mailboxes               VARCHAR(80),
    max_contacts            INTEGER DEFAULT 1,
    minimum_expiration      INTEGER DEFAULT 60,
    remove_existing         VARCHAR(3) DEFAULT 'yes',
    qualify_frequency       INTEGER DEFAULT 60,
    authenticate_qualify    VARCHAR(3),
    maximum_expiration      INTEGER DEFAULT 7200,
    outbound_proxy          VARCHAR(40),
    support_path            VARCHAR(3),
    qualify_timeout         FLOAT DEFAULT 3.0,
    voicemail_extension     VARCHAR(40)
);


-- ---------------------------------------------------------------------------
-- PJSIP Registrations (outbound trunk registration)
-- ---------------------------------------------------------------------------

CREATE TABLE ps_registrations (
    id                          VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_rejection_permanent    VARCHAR(3) DEFAULT 'yes',
    client_uri                  VARCHAR(255),
    contact_user                VARCHAR(40),
    expiration                  INTEGER DEFAULT 3600,
    max_retries                 INTEGER DEFAULT 10,
    outbound_auth               VARCHAR(40),
    outbound_proxy              VARCHAR(40),
    retry_interval              INTEGER DEFAULT 60,
    forbidden_retry_interval    INTEGER DEFAULT 10,
    server_uri                  VARCHAR(255),
    transport                   VARCHAR(40),
    support_path                VARCHAR(3),
    fatal_retry_interval        INTEGER,
    line                        VARCHAR(3),
    endpoint                    VARCHAR(40),
    support_outbound            VARCHAR(3),
    contact_header_params       VARCHAR(255)
);


-- ---------------------------------------------------------------------------
-- PJSIP Contacts (managed by Asterisk for registrations)
-- ---------------------------------------------------------------------------
-- Note: For production, consider using astdb instead of Postgres for contacts
-- (too much churn per REGISTER cycle). Demo uses Postgres for simplicity.

CREATE TABLE ps_contacts (
    id                      VARCHAR(255) NOT NULL PRIMARY KEY,
    uri                     VARCHAR(511),
    expiration_time         BIGINT,
    qualify_frequency       INTEGER,
    outbound_proxy          VARCHAR(40),
    path                    TEXT,
    user_agent              VARCHAR(255),
    via_addr                VARCHAR(40),
    via_port                INTEGER,
    call_id                 VARCHAR(255),
    endpoint                VARCHAR(40),
    reg_server              VARCHAR(20),
    authenticate_qualify    VARCHAR(3),
    prune_on_boot           VARCHAR(3),
    qualify_timeout         FLOAT
);


-- ---------------------------------------------------------------------------
-- PJSIP Domain Aliases
-- ---------------------------------------------------------------------------

CREATE TABLE ps_domain_aliases (
    id      VARCHAR(40) NOT NULL PRIMARY KEY,
    domain  VARCHAR(80)
);


-- ---------------------------------------------------------------------------
-- PJSIP Endpoint Identification by IP (trunk PSTN identification)
-- ---------------------------------------------------------------------------

CREATE TABLE ps_endpoint_id_ips (
    id              VARCHAR(40) NOT NULL PRIMARY KEY,
    endpoint        VARCHAR(40),
    match           VARCHAR(80),
    srv_lookups     VARCHAR(3),
    match_header    VARCHAR(255)
);


-- ---------------------------------------------------------------------------
-- Queues (Asterisk app_queue Realtime)
-- ---------------------------------------------------------------------------
-- Table name: 'queues' (Asterisk default). NOT 'rt_queues' or 'queue_table'.
-- Pro.Realtime writes directly via Dapper (bypasses extconfig).
-- Asterisk reads via extconfig mapping: queues => pgsql,general,queues

CREATE TABLE queues (
    name                VARCHAR(128) NOT NULL PRIMARY KEY,
    musiconhold         VARCHAR(128) DEFAULT 'default',
    announce            VARCHAR(128),
    context             VARCHAR(128),
    timeout             INTEGER DEFAULT 15,
    ringinuse           VARCHAR(3) DEFAULT 'no',
    setinterfacevar     VARCHAR(3) DEFAULT 'yes',
    setqueuevar         VARCHAR(3) DEFAULT 'yes',
    setqueueentryvar    VARCHAR(3) DEFAULT 'yes',
    monitor_format      VARCHAR(10),
    membermacro         VARCHAR(128),
    membergosubcontext  VARCHAR(128),
    queue_youarenext    VARCHAR(128),
    queue_thereare      VARCHAR(128),
    queue_callswaiting  VARCHAR(128),
    queue_holdtime      VARCHAR(128),
    queue_minutes       VARCHAR(128),
    queue_seconds       VARCHAR(128),
    queue_thankyou      VARCHAR(128),
    strategy            VARCHAR(20) DEFAULT 'ringall',
    joinempty           VARCHAR(40),
    leavewhenempty      VARCHAR(40),
    eventwhencalled     VARCHAR(3) DEFAULT 'yes',
    eventmemberstatus   VARCHAR(3) DEFAULT 'yes',
    reportholdtime      VARCHAR(3) DEFAULT 'yes',
    weight              INTEGER DEFAULT 0,
    wrapuptime          INTEGER DEFAULT 0,
    maxlen              INTEGER DEFAULT 0,
    servicelevel        INTEGER DEFAULT 60,
    retry               INTEGER DEFAULT 5,
    autopause           VARCHAR(3) DEFAULT 'no'
);


-- ---------------------------------------------------------------------------
-- Queue Members (Asterisk app_queue Realtime)
-- ---------------------------------------------------------------------------
-- Composite PK (queue_name, interface) per Asterisk convention.
-- NOT SERIAL uniqueid PK (Platform's non-standard approach).

CREATE TABLE queue_members (
    queue_name      VARCHAR(128) NOT NULL,
    interface       VARCHAR(128) NOT NULL,
    membername      VARCHAR(128),
    state_interface VARCHAR(128),
    penalty         INTEGER DEFAULT 0,
    paused          INTEGER DEFAULT 0,
    uniqueid        VARCHAR(40) NOT NULL DEFAULT gen_random_uuid()::VARCHAR(40),
    reason_paused   VARCHAR(80),
    PRIMARY KEY (queue_name, interface)
);

CREATE INDEX idx_queue_members_queue ON queue_members (queue_name);
CREATE INDEX idx_queue_members_interface ON queue_members (interface);


-- ---------------------------------------------------------------------------
-- Voicemail (future use)
-- ---------------------------------------------------------------------------

CREATE TABLE voicemail (
    id          SERIAL PRIMARY KEY,
    context     VARCHAR(40) NOT NULL DEFAULT 'default',
    mailbox     VARCHAR(40) NOT NULL,
    password    VARCHAR(40) DEFAULT '1234',
    fullname    VARCHAR(80),
    email       VARCHAR(80),
    pager       VARCHAR(80),
    tz          VARCHAR(10) DEFAULT 'central',
    attach      VARCHAR(3) DEFAULT 'yes',
    saycid      VARCHAR(3) DEFAULT 'yes',
    dialout     VARCHAR(40),
    callback    VARCHAR(40),
    review      VARCHAR(3) DEFAULT 'no',
    operator    VARCHAR(3) DEFAULT 'no',
    envelope    VARCHAR(3) DEFAULT 'no',
    sayduration VARCHAR(3) DEFAULT 'no',
    saydurationm INTEGER DEFAULT 1,
    maxmsg      INTEGER DEFAULT 100,
    uniqueid    VARCHAR(40),
    UNIQUE (context, mailbox)
);

CREATE INDEX idx_voicemail_mailbox ON voicemail (context, mailbox);
