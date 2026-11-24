-- =============================================================================
-- Verbara.Platform — routing skill catalog (032)
-- =============================================================================
-- routing-skills-stay-in-their-tenant (design D2). The DDL below is the text
-- measured on PostgreSQL 18 (card PLT-SKL-01); a change to it invalidates that
-- measurement.
--
-- routing_skills: the skill definitions of each tenant. One per
-- (tenant_id, lower(name)); an upsert replaces the stored name's case and fields.
--
-- agent_skill_proficiencies: each tenant's agent skill proficiencies. One per
-- (tenant_id, agent_id, lower(skill_name)); an agent's rows go with the agent
-- (ON DELETE CASCADE). No foreign key to routing_skills: an agent may hold a
-- skill the catalog does not define.
--
-- Additive, no backfill (D8): both tables start empty. Idempotent: IF NOT EXISTS
-- throughout. Locks: two new, empty tables; the foreign key takes a SHARE ROW
-- EXCLUSIVE lock on agents for the length of the migration transaction.
-- Rollback: an older binary never names either table, so both can stay unused.
-- To remove them,
--   DROP TABLE IF EXISTS agent_skill_proficiencies;
--   DROP TABLE IF EXISTS routing_skills;
-- =============================================================================

CREATE TABLE IF NOT EXISTS routing_skills (
    tenant_id   TEXT        NOT NULL,
    name        TEXT        NOT NULL,
    category    TEXT        NULL,
    description TEXT        NULL,
    created_at  TIMESTAMPTZ NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_routing_skills_tenant_name
    ON routing_skills (tenant_id, lower(name));

CREATE TABLE IF NOT EXISTS agent_skill_proficiencies (
    tenant_id   TEXT        NOT NULL,
    agent_id    TEXT        NOT NULL,
    skill_name  TEXT        NOT NULL,
    proficiency INTEGER     NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL,
    CONSTRAINT fk_agent_skill_proficiencies_agent FOREIGN KEY (tenant_id, agent_id)
        REFERENCES agents (tenant_id, agent_id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_skill_proficiencies_key
    ON agent_skill_proficiencies (tenant_id, agent_id, lower(skill_name));
