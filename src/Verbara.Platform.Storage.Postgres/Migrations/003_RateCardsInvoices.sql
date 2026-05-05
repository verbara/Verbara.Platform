-- 003_RateCardsInvoices.sql — Rate card pricing and invoice tables

CREATE TABLE IF NOT EXISTS rate_cards (
    rate_card_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    currency TEXT NOT NULL DEFAULT 'USD',
    effective_from TIMESTAMPTZ NOT NULL,
    effective_to TIMESTAMPTZ,
    rates JSONB NOT NULL,
    is_default BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_ratecard_tenant ON rate_cards (tenant_id, effective_from DESC);

CREATE TABLE IF NOT EXISTS invoices (
    invoice_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    period_start TIMESTAMPTZ NOT NULL,
    period_end TIMESTAMPTZ NOT NULL,
    currency TEXT NOT NULL,
    line_items JSONB NOT NULL,
    subtotal NUMERIC(18,2) NOT NULL,
    tax NUMERIC(18,2) NOT NULL DEFAULT 0,
    total NUMERIC(18,2) NOT NULL,
    status SMALLINT NOT NULL DEFAULT 0,
    generated_at TIMESTAMPTZ NOT NULL,
    issued_at TIMESTAMPTZ,
    paid_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_invoice_tenant ON invoices (tenant_id, period_start DESC);
