-- Rename intent is carried in comments, so a parenthesis above a declaration is
-- not hypothetical.
-- strata:renamed_from (awk.open_bin_old)
-- open bins (by tenant)
CREATE VIEW open_bin AS
    SELECT id, label
    FROM bin
    WHERE tenant = 'acme';
