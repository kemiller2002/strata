-- Labels are DATA, not identifiers: single-quoted, and a quote inside one is
-- doubled. Mixed case survives, unlike an unquoted identifier, because a label
-- is never case-folded. A label may also be empty, and may contain a comma or a
-- space — none of which an identifier could carry unquoted.
CREATE TYPE awkward_labels AS ENUM ('', 'Mixed Case', 'it''s', 'a,b', 'trailing ');
