-- Run with psql -v jobs_schema=bluetusk_jobs -v workflows_schema=bluetusk_workflows -f eng/jobs-workflows-vacuum.sql
-- The maintenance role must own these tables or hold the PostgreSQL-version-appropriate maintenance privilege.
-- Deliberately no BEGIN: VACUUM cannot run inside a transaction block.
\set ON_ERROR_STOP on
\timing on
SET lock_timeout = '2s';
SET statement_timeout = '30s';
SELECT count(*) > 0 AND bool_and(to_regclass(format('%I.%I', requested.schema_name, requested.table_name)) IS NOT NULL)
    AS maintenance_targets_exist
FROM (VALUES (:'jobs_schema', 'jobs'), (:'workflows_schema', 'instances')) requested(schema_name, table_name)
WHERE requested.schema_name <> ''
\gset
\if :maintenance_targets_exist
\else
    DO $maintenance$ BEGIN
        RAISE EXCEPTION 'Requested runtime maintenance tables do not exist; no vacuum was issued.';
    END $maintenance$;
\endif
SELECT format('VACUUM (ANALYZE, TRUNCATE FALSE, INDEX_CLEANUP ON) %I.%I;', n.nspname, c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind = 'r' AND (
    (n.nspname = :'jobs_schema' AND c.relname IN ('settings', 'jobs', 'job_attempts', 'schedules')) OR
    (n.nspname = :'workflows_schema' AND c.relname IN ('settings', 'definitions', 'instances', 'nodes', 'history', 'signals')))
ORDER BY n.nspname, c.relname
\gexec
