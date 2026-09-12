CREATE POLICY p_restrictive ON t AS RESTRICTIVE FOR ALL TO strata_awkward_role
    USING (tenant = 'acme') WITH CHECK (tenant = 'acme');
