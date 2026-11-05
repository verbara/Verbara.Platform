-- =============================================================================
-- Verbara.Platform — WebChat addresses in the identity key forms (028)
-- =============================================================================
-- webchat-visitor-identity-two-levels (design D14; Platform/ADR-0048).
--
-- From this release a WebChat address that starts with webchat-visitor: or
-- webchat-external: is an identity key, which only POST /webchat/sessions writes,
-- and the contact routes refuse it. The contact routes accepted any address
-- before, so a contact can already hold one that somebody typed. Such an address
-- would read as an identity level with no session behind it, and a session could
-- find that contact by it. This file runs before the Api serves its first request,
-- and so before the new code has written any key: whatever it finds was typed.
--
-- Every such address gets the prefix legacy-, which makes it an ordinary address:
-- it reads Anonymous and no session looks it up. The test is the one the code
-- uses: ordinal and case-sensitive (starts_with, never ILIKE), on channel WebChat
-- only, in both on-disk forms of the channel, the name and the ordinal, as
-- PostgresContactStore.FindByAddressSql compares them. The address keeps its
-- position and its channel, and no other column changes: updated_at and updated_by
-- stay, and no audit row is written (the audit service does not exist yet; the
-- upgrade note gives a read-only SELECT that lists these contacts beforehand).
--
-- Locks (the runner wraps the file in one transaction): row locks on the contacts
-- it renames, and a sequential scan of contacts to find them.
--
-- Idempotent: a renamed address no longer starts with either prefix, so a second
-- run of this text changes nothing. Rollback: an older binary reads a legacy-
-- address as any other address. The rename is not undone.
-- =============================================================================

UPDATE contacts c
SET addresses = (
    SELECT jsonb_agg(
               CASE
                   WHEN e.item->>'channel' IN ('WebChat', '3')
                        AND (starts_with(e.item->>'address', 'webchat-visitor:')
                             OR starts_with(e.item->>'address', 'webchat-external:'))
                   THEN jsonb_set(e.item, '{address}', to_jsonb('legacy-' || (e.item->>'address')))
                   ELSE e.item
               END
               ORDER BY e.position)
    FROM jsonb_array_elements(c.addresses) WITH ORDINALITY AS e(item, position))
WHERE EXISTS (
    SELECT 1
    FROM jsonb_array_elements(CASE jsonb_typeof(c.addresses) WHEN 'array' THEN c.addresses ELSE '[]'::jsonb END) AS a(item)
    WHERE a.item->>'channel' IN ('WebChat', '3')
      AND (starts_with(a.item->>'address', 'webchat-visitor:')
           OR starts_with(a.item->>'address', 'webchat-external:')));
