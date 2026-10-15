#!/usr/bin/env bash
# Self-test for migration-safety.sh: each destructive form must fail, each additive form must pass.
set -euo pipefail

script="$(cd "$(dirname "$0")/.." && pwd)/migration-safety.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
failures=0

expect() {
  local want="$1" sql="$2"
  printf '%s\n' "$sql" > "$tmp/case.sql"
  if "$script" --sql-file "$tmp/case.sql" >/dev/null 2>&1; then got=pass; else got=fail; fi
  if [[ "$got" != "$want" ]]; then
    echo "FAIL: expected $want, got $got: $sql"
    failures=$((failures + 1))
  fi
}

expect pass 'ALTER TABLE orders.orders ADD cancellation_reason character varying(500);'
expect pass 'ALTER TABLE orders.orders ADD priority integer NOT NULL DEFAULT 0;'
expect pass 'CREATE TABLE orders.shipments (id uuid NOT NULL, CONSTRAINT pk_shipments PRIMARY KEY (id));'
expect pass 'CREATE INDEX ix_orders_status ON orders.orders (status);'
expect fail 'ALTER TABLE orders.orders DROP COLUMN customer_reference;'
expect fail 'DROP TABLE orders.idempotency_keys;'
expect fail 'ALTER TABLE orders.orders RENAME COLUMN sku TO product_code;'
expect fail 'ALTER TABLE orders.orders RENAME TO purchase_orders;'
expect fail 'ALTER TABLE orders.orders ALTER COLUMN quantity TYPE bigint;'
expect fail 'ALTER TABLE orders.orders ALTER COLUMN customer_reference SET NOT NULL;'
expect fail 'ALTER TABLE orders.orders ADD priority integer NOT NULL;'
expect fail 'DELETE FROM orders.orders;'

printf '%s\n' 'ALTER TABLE orders.orders DROP COLUMN customer_reference;' > "$tmp/approved.sql"
if ! CONTRACT_MIGRATION_APPROVED=true "$script" --sql-file "$tmp/approved.sql" >/dev/null 2>&1; then
  echo "FAIL: approved contract migration should pass"
  failures=$((failures + 1))
fi

if [[ $failures -gt 0 ]]; then
  echo "$failures migration-safety case(s) failed"
  exit 1
fi
echo "migration-safety self-test: all cases passed"
