-- =============================================================================
-- Verbara.Platform — one CSAT rating per email or SMS survey dispatch (035)
-- =============================================================================
-- csat-one-rating-per-dispatch (#697). An email or SMS survey is dispatched once
-- per (tenant, conversation, survey, channel), but every capture inserted a new
-- survey_responses row, so a second reply to the same survey email (or a
-- re-forwarded one) stored a second rating. From here on at most one row exists
-- per (tenant_id, conversation_id, survey_id, channel) for channel email or sms;
-- PostgresSurveyResponseStore inserts with ON CONFLICT DO NOTHING, so the first
-- capture is kept and a later or concurrent one stores nothing.
--
-- Upgrade over existing duplicates: of the rows sharing a key, the first
-- captured (COALESCE(captured_at, submitted_at), then response_id) is kept and
-- the others are deleted, the same rule the store applies to new captures. The
-- RAISE NOTICE below gives the number removed to a client that shows notices
-- (psql); the API's migration runner does not log notices. Audit records of the
-- removed ratings are kept.
-- Nothing references a survey_responses row by id (no foreign keys). Webchat,
-- voice and non-CSAT rows are untouched: the index is partial.
--
-- One DO block, so the steps are atomic even when a tool applies the file
-- outside a transaction (the runner wraps it in one anyway). The SHARE ROW
-- EXCLUSIVE lock keeps writers out between the delete and the index build, so
-- no duplicate can slip in after the DELETE; reads continue. Idempotent: a
-- second run finds no duplicate and the index already there.
-- =============================================================================

DO $$
DECLARE
    removed integer;
BEGIN
    LOCK TABLE survey_responses IN SHARE ROW EXCLUSIVE MODE;

    DELETE FROM survey_responses r
    USING survey_responses keep
    WHERE r.channel IN ('email', 'sms')
      AND keep.tenant_id = r.tenant_id
      AND keep.conversation_id = r.conversation_id
      AND keep.survey_id = r.survey_id
      AND keep.channel = r.channel
      AND (COALESCE(keep.captured_at, keep.submitted_at), keep.response_id)
          < (COALESCE(r.captured_at, r.submitted_at), r.response_id);
    GET DIAGNOSTICS removed = ROW_COUNT;
    RAISE NOTICE '035_SurveyResponseOnePerDispatch: removed % later email or SMS rating(s)', removed;

    CREATE UNIQUE INDEX IF NOT EXISTS ux_survey_resp_one_per_dispatch
        ON survey_responses (tenant_id, conversation_id, survey_id, channel)
        WHERE channel IN ('email', 'sms');
END
$$;
