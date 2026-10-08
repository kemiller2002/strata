CREATE FUNCTION greet(name text) RETURNS text LANGUAGE sql AS $$ SELECT 'hi ' || name $$;
COMMENT ON FUNCTION greet(text) IS 'Says hello';
