-- One-time database initialization (ADR-0004, ADR-0006 first deployment).
-- Run as the database master user against the `orders` database.
--
-- Two roles with different rights:
--   orders_migrator  owns the orders schema and runs migrations (DDL).
--   orders_app       reads and writes rows. No DDL, no DELETE.
-- In AWS both log in with RDS IAM tokens: run rds-iam.sql afterwards.
-- Locally, local-passwords.sh sets throwaway passwords instead.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'orders_migrator') THEN
        CREATE ROLE orders_migrator LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'orders_app') THEN
        CREATE ROLE orders_app LOGIN;
    END IF;
END
$$;

GRANT CONNECT ON DATABASE orders TO orders_migrator, orders_app;

-- The migrator owns the schema, so EF's CREATE SCHEMA IF NOT EXISTS is a no-op and
-- nothing needs rights on the public schema.
CREATE SCHEMA IF NOT EXISTS orders AUTHORIZATION orders_migrator;
REVOKE ALL ON SCHEMA public FROM orders_app;
GRANT USAGE ON SCHEMA orders TO orders_app;

-- Every table the migrator creates, now or in later releases, is readable and
-- writable by the application but never deletable or alterable by it.
ALTER DEFAULT PRIVILEGES FOR ROLE orders_migrator IN SCHEMA orders
    GRANT SELECT, INSERT, UPDATE ON TABLES TO orders_app;
ALTER DEFAULT PRIVILEGES FOR ROLE orders_migrator IN SCHEMA orders
    GRANT USAGE, SELECT ON SEQUENCES TO orders_app;
