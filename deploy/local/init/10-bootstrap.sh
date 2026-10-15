#!/bin/sh
# Runs once inside the local PostgreSQL container on first start.
# Applies the same bootstrap as AWS, then sets throwaway local passwords.
set -eu

psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -c "CREATE DATABASE orders"
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d orders -f /bootstrap/bootstrap.sql
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d orders \
  -v migrator_pw="$ORDERS_MIGRATOR_PASSWORD" -v app_pw="$ORDERS_APP_PASSWORD" <<'SQL'
ALTER ROLE orders_migrator PASSWORD :'migrator_pw';
ALTER ROLE orders_app PASSWORD :'app_pw';
SQL
