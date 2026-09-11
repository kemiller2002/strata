-- Reconciliation against the legacy system.
SELECT i.legacy_code, sum(i.amount) AS total
FROM billing.invoice i
WHERE i.legacy_code IS NOT NULL
GROUP BY i.legacy_code;
