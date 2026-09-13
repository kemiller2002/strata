-- A domain whose base type carries a MODIFIER and whose predicates are written
-- in the spellings a server rewrites. `numeric(12,2)` comes back from
-- format_type with its modifier intact; `0` in a comparison against a numeric
-- comes back as `(0)::numeric`; and a chain of ANDs is re-parenthesised. Each
-- of those differs from what is written here, which is the point — the declared
-- side is compared as the SERVER renders it or not at all.
CREATE DOMAIN money_amount AS numeric(12,2)
    CHECK (VALUE >= 0 AND VALUE < 1000000 AND VALUE = round(VALUE, 2));
