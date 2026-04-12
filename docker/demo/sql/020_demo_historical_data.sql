-- ============================================================
-- Demo Historical Data
-- Run AFTER Platform API has started (creates completed_sessions, interval_snapshots)
-- Generates 50 CDRs + 48 interval snapshots covering the last 24 hours
-- ============================================================

-- 50 CDRs distributed over the last 24 hours
-- Distribution: 30 answered, 10 abandoned, 5 busy, 5 no-answer
-- Queues: 60% support, 40% sales
INSERT INTO completed_sessions (
    session_id, tenant_id, server_id, direction,
    caller_id_num, caller_id_name,
    agent_id, queue_name,
    started_at, connected_at, completed_at,
    duration_ms, talk_time_ms, wait_time_ms, hold_time_ms,
    final_state, event_count
)
SELECT
    'demo-' || lpad(i::text, 3, '0'),
    'demo',
    'demo',
    0, -- inbound
    '555' || lpad((1000 + (i * 7) % 100)::text, 4, '0'),
    'Caller ' || i,
    -- Agent: NULL for non-answered, otherwise rotate agents
    CASE
        WHEN i % 10 >= 6 THEN NULL -- abandoned/busy/no-answer
        WHEN i % 5 < 3 THEN (3001 + (i % 3))::text -- support agents
        ELSE (2001 + (i % 3))::text -- sales agents
    END,
    -- Queue
    CASE WHEN i % 5 < 3 THEN 'support' ELSE 'sales' END,
    -- started_at: spread across 24h, peak at 10-12h and 15-17h
    NOW() - INTERVAL '24 hours' + (
        CASE
            WHEN i <= 5  THEN (i * 108)         -- 00:00-01:30 (sparse)
            WHEN i <= 15 THEN (540 + (i-5) * 72) -- 09:00-12:00 (morning peak)
            WHEN i <= 25 THEN (720 + (i-15) * 54) -- 12:00-15:00 (midday)
            WHEN i <= 40 THEN (900 + (i-25) * 48) -- 15:00-18:00 (afternoon peak)
            ELSE (1080 + (i-40) * 72)             -- 18:00-21:00 (evening)
        END
    ) * INTERVAL '1 minute',
    -- connected_at: NULL for non-answered, +wait_time for answered
    CASE
        WHEN i % 10 < 6 THEN
            NOW() - INTERVAL '24 hours' + (
                CASE
                    WHEN i <= 5  THEN (i * 108)
                    WHEN i <= 15 THEN (540 + (i-5) * 72)
                    WHEN i <= 25 THEN (720 + (i-15) * 54)
                    WHEN i <= 40 THEN (900 + (i-25) * 48)
                    ELSE (1080 + (i-40) * 72)
                END
            ) * INTERVAL '1 minute' + ((5 + i % 20) * INTERVAL '1 second')
        ELSE NULL
    END,
    -- completed_at
    NOW() - INTERVAL '24 hours' + (
        CASE
            WHEN i <= 5  THEN (i * 108)
            WHEN i <= 15 THEN (540 + (i-5) * 72)
            WHEN i <= 25 THEN (720 + (i-15) * 54)
            WHEN i <= 40 THEN (900 + (i-25) * 48)
            ELSE (1080 + (i-40) * 72)
        END
    ) * INTERVAL '1 minute' + (
        CASE
            WHEN i % 10 < 6 THEN (30 + i * 5) -- answered: 30-280s total
            WHEN i % 10 < 8 THEN (5 + i % 15)  -- abandoned: 5-20s
            ELSE 0                                -- busy/no-answer: instant
        END
    ) * INTERVAL '1 second',
    -- duration_ms
    CASE
        WHEN i % 10 < 6 THEN (30 + i * 5) * 1000
        WHEN i % 10 < 8 THEN (5 + i % 15) * 1000
        ELSE 0
    END,
    -- talk_time_ms
    CASE WHEN i % 10 < 6 THEN (20 + i * 4) * 1000 ELSE 0 END,
    -- wait_time_ms
    CASE
        WHEN i % 10 < 6 THEN (5 + i % 20) * 1000
        WHEN i % 10 < 8 THEN (5 + i % 15) * 1000
        ELSE 0
    END,
    0, -- hold_time_ms
    -- final_state: 1=answered, 2=no-answer, 3=busy, 5=abandoned
    CASE
        WHEN i % 10 < 6 THEN 1  -- answered (60%)
        WHEN i % 10 < 8 THEN 5  -- abandoned (20%)
        WHEN i % 10 = 8 THEN 3  -- busy (10%)
        ELSE 2                    -- no-answer (10%)
    END,
    CASE WHEN i % 10 < 6 THEN 5 ELSE 3 END -- event_count
FROM generate_series(1, 50) AS i;

-- 48 interval snapshots (30-minute intervals over 24 hours)
-- Two queues: sales + support
INSERT INTO interval_snapshots (
    queue_name, server_id, interval_start, interval_seconds,
    calls_offered, calls_answered, calls_abandoned, short_abandons,
    sla_met_count, total_wait_ms, total_talk_ms, total_hold_ms,
    max_wait_ms, wait_0_10, wait_10_20, wait_20_30
)
SELECT
    queue,
    'demo',
    NOW() - INTERVAL '24 hours' + (i * 30) * INTERVAL '1 minute',
    1800,
    -- calls_offered: 0-4, more during business hours (intervals 18-36 = 9h-18h)
    CASE
        WHEN i BETWEEN 18 AND 36 THEN 1 + (i + queue_idx) % 4 -- 1-4 during business
        WHEN i BETWEEN 14 AND 17 THEN (i + queue_idx) % 2     -- 0-1 early morning
        ELSE 0                                                   -- night
    END,
    -- calls_answered: 75-95% of offered
    GREATEST(0, CASE
        WHEN i BETWEEN 18 AND 36 THEN (1 + (i + queue_idx) % 4) - ((i + queue_idx) % 5 = 0)::int
        WHEN i BETWEEN 14 AND 17 THEN ((i + queue_idx) % 2) - ((i + queue_idx) % 3 = 0)::int
        ELSE 0
    END),
    -- calls_abandoned
    CASE WHEN i BETWEEN 18 AND 36 AND (i + queue_idx) % 5 = 0 THEN 1 ELSE 0 END,
    0, -- short_abandons
    -- sla_met_count
    GREATEST(0, CASE
        WHEN i BETWEEN 18 AND 36 THEN (1 + (i + queue_idx) % 4) - ((i + queue_idx) % 4 = 0)::int
        WHEN i BETWEEN 14 AND 17 THEN (i + queue_idx) % 2
        ELSE 0
    END),
    -- total_wait_ms: 8-25s per call
    CASE WHEN i BETWEEN 14 AND 36 THEN (8000 + (i * 500) % 17000) * GREATEST(1, (i + queue_idx) % 4) ELSE 0 END,
    -- total_talk_ms: 60-180s per call
    CASE WHEN i BETWEEN 14 AND 36 THEN (60000 + (i * 3000) % 120000) * GREATEST(1, (i + queue_idx) % 4) ELSE 0 END,
    0, -- total_hold_ms
    -- max_wait_ms
    CASE WHEN i BETWEEN 18 AND 36 THEN 8000 + (i * 700) % 20000 ELSE 0 END,
    -- wait buckets
    CASE WHEN i BETWEEN 18 AND 36 THEN GREATEST(0, (i + queue_idx) % 3) ELSE 0 END,
    CASE WHEN i BETWEEN 18 AND 36 THEN (i + queue_idx) % 2 ELSE 0 END,
    0
FROM generate_series(0, 47) AS i,
    (VALUES ('sales', 0), ('support', 1)) AS q(queue, queue_idx);
