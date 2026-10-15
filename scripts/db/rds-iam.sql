-- AWS only, after bootstrap.sql: both roles authenticate with IAM tokens issued to
-- their IRSA roles (rds-db:connect on dbuser:<resource-id>/<role>). No passwords exist.
GRANT rds_iam TO orders_migrator, orders_app;
