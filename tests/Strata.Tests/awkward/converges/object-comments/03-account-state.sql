CREATE TYPE account_state AS ENUM ('active', 'closed');
COMMENT ON TYPE account_state IS 'Where an account is in its life';
