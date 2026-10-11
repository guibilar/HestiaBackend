#!/bin/sh
# Restores a dump written by backup.sh over the current Hestia database. Run it from the directory
# with docker-compose.yml, with the stack up. The dump is a file name in the backup volume, or a
# file on this machine:
#   docker/restore.sh hestia-2026-10-11_030000.dump
#   docker/restore.sh ~/copies/hestia-2026-10-11_030000.dump
# Everything stored since that dump is replaced, so the current database is saved first, as
# before-restore-<time>.dump in the backup volume (never deleted automatically). The host
# (hestia-api) is stopped while the restore runs.
set -eu

dump=${1:?usage: docker/restore.sh <dump file name in the backup volume, or path to a .dump file>}

if [ -f "$dump" ]; then
  # A file on this machine: pg_restore reads it from stdin.
  in_volume=""
else
  in_volume="/backups/$(basename "$dump")"
  docker compose exec -T backup test -f "$in_volume" ||
    { echo "No such file here or in the backup volume: $dump" >&2; exit 1; }
fi

echo "Saving the current database first..."
docker compose exec -T backup backup.sh before-restore

docker compose stop hestia-api
# One transaction: if anything fails, the database is left as it was. Every module schema is dropped
# first (all schemas except the system ones), not with pg_restore --clean, which drops only what the
# dump has: a module or a migration that is newer than the dump would leave its tables behind. The
# connection (PGHOST, PGDATABASE, ...) comes from the backup container's environment.
reset_sql="DO \$\$ DECLARE s text; BEGIN
  FOR s IN SELECT nspname FROM pg_namespace
    WHERE nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema'
  LOOP EXECUTE format('DROP SCHEMA %I CASCADE', s); END LOOP;
END \$\$; CREATE SCHEMA public;"
status=0
if [ -n "$in_volume" ]; then
  docker compose exec -T -e RESET_SQL="$reset_sql" -e DUMP="$in_volume" backup sh -c '
    { printf "%s\n" "$RESET_SQL"; pg_restore --no-owner --file=- "$DUMP"; } |
      psql --quiet --no-psqlrc --set ON_ERROR_STOP=1 --single-transaction --output=/dev/null --dbname "postgresql:///"
  ' || status=$?
else
  docker compose exec -T -e RESET_SQL="$reset_sql" backup sh -c '
    { printf "%s\n" "$RESET_SQL"; pg_restore --no-owner --file=-; } |
      psql --quiet --no-psqlrc --set ON_ERROR_STOP=1 --single-transaction --output=/dev/null --dbname "postgresql:///"
  ' < "$dump" || status=$?
fi
docker compose start hestia-api
if [ "$status" -eq 0 ]; then
  echo "Restored $dump"
else
  echo "Restore failed; the database was left as it was." >&2
fi
exit "$status"
