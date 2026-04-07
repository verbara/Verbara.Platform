-- 009_PartnerRevenue.sql — Partner revenue tracking for partner billing

CREATE TABLE IF NOT EXISTS partner_revenue (
    revenue_id         TEXT PRIMARY KEY,
    partner_tenant_id  TEXT NOT NULL,
    customer_tenant_id TEXT NOT NULL,
    invoice_id         TEXT NOT NULL,
    gross_amount       NUMERIC(18,4) NOT NULL,
    platform_cost      NUMERIC(18,4) NOT NULL,
    partner_margin     NUMERIC(18,4) NOT NULL,
    period_start       TIMESTAMPTZ NOT NULL,
    period_end         TIMESTAMPTZ NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_partner_revenue_partner_period
    ON partner_revenue (partner_tenant_id, period_start);

CREATE INDEX IF NOT EXISTS idx_partner_revenue_invoice
    ON partner_revenue (invoice_id);
