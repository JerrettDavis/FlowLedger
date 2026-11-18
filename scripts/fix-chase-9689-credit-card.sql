-- One-off, idempotent data fix: the Chase card "J. DAVIS (9689)" was imported from SimpleFIN as a CHECKING account
-- (its name carries no card keyword) with its owed amount stored as a negative asset balance.
--
-- Effect (only on rows that still look wrong, so re-running is a no-op):
--   accounts:                  account_type Checking -> CreditCard, balance -45.60 -> 45.60 (liabilities store the amount OWED, positive)
--                              type_locked_by_user = true when the column exists (after the AddAccountTypeLock migration)
--   account_balance_snapshots: negative snapshot(s) of that account get the same sign flip
-- Nothing is deleted. Transactions are NOT touched (use PUT /api/accounts/{id}/type once the new build is deployed if you
-- also want auto "Transfer" categories re-derived; the existing ones for this card's payments are already correct).
--
-- Run (psql), after taking a backup, e.g.:
--   pg_dump -Fc -d flowledger_prod > before.dump
--   psql -d flowledger_prod -v ON_ERROR_STOP=1 -f scripts/fix-chase-9689-credit-card.sql

\set ON_ERROR_STOP on
BEGIN;

CREATE TEMP TABLE _fix_target AS
SELECT id FROM accounts
WHERE id = '15ff5dbd-6ad4-4bd8-8e0d-baefed3b6f1b'
  AND name LIKE 'Chase Bank - JDH Pro - J. DAVIS (9689)'
  AND account_type = 'Checking'
  AND balance_amount < 0;

SELECT 'rows to fix' AS what, count(*) FROM _fix_target;

UPDATE account_balance_snapshots s
SET balance = abs(s.balance)
FROM _fix_target t
WHERE s.account_id = t.id AND s.balance < 0;

UPDATE accounts a
SET account_type = 'CreditCard',
    balance_amount = abs(a.balance_amount)
FROM _fix_target t
WHERE a.id = t.id;

-- Pin the type when the column exists (tolerates running before the migration is applied).
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.columns
             WHERE table_name = 'accounts' AND column_name = 'type_locked_by_user') THEN
    UPDATE accounts SET type_locked_by_user = true
    WHERE id = '15ff5dbd-6ad4-4bd8-8e0d-baefed3b6f1b' AND account_type = 'CreditCard';
  END IF;
END $$;

SELECT id, name, account_type, balance_amount FROM accounts WHERE id IN (SELECT id FROM _fix_target);

COMMIT;
