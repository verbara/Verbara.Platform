CREATE TABLE IF NOT EXISTS articles (
    article_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    tags TEXT[] NOT NULL DEFAULT '{}',
    language TEXT,
    is_published BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, article_id)
);
CREATE INDEX IF NOT EXISTS idx_articles_published ON articles (tenant_id) WHERE is_published;
