#!/bin/sh
# Restores a dump written by backup.sh over the current Hestia database. Run it from the directory
# with docker-compose.yml, with the stack up. The dump is a file name in the backup volume, or a
# file on this machine:
#   docker/restore.sh hestia-2026-10-11_030000.dump
#   docker/restore.sh ~/copies/hestia-2026-10-11_030000.dump
# Everything stored since that dump is replaced, so the current database is saved first, as
# before-restore-<time>.dump in the backup volume (never deleted automatically). The host
# (hestia-api) is stopped while the restore runs.
# T11: the document files come back with the rows. They are in the folder next to the dump, with the same name and
# .documents (hestia-2026-10-11_030000.documents), in the volume or on this machine. Without that folder, the files on
# the documents volume stay as they are.
set -eu

dump=${1:?usage: docker/restore.sh <dump file name in the backup volume, or path to a .dump file>}

if [ -f "$dump" ]; then
  # A file on this machine: pg_restore reads it from stdin.
  in_volume=""
  local_documents="${dump%.dump}.documents"
  [ -d "$local_documents" ] || local_documents=""
else
  in_volume="/backups/$(basename "$dump")"
  docker compose exec -T backup test -f "$in_volume" ||
    { echo "No such file here or in the backup volume: $dump" >&2; exit 1; }
  local_documents=""
fi
documents="${in_volume%.dump}.documents"
if [ -n "$local_documents" ]; then
  documents=/tmp/restore-documents
elif [ -z "$in_volume" ] || ! docker compose exec -T backup test -d "$documents"; then
  documents=""
  echo "No document files next to the dump (a .documents folder): the files on the documents volume stay as they are."
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

# T11: the document files of the dump. A file that the dump does not have was uploaded after it: its row is gone now,
# so it goes too. The files go back to the owner of the host's files (DOCUMENTS_OWNER).
files_status=0
if [ "$status" -eq 0 ] && [ -n "$documents" ]; then
  if [ -n "$local_documents" ]; then
    docker compose exec -T -u root backup rm -rf "$documents"
    docker compose cp "$local_documents" "backup:$documents"
  fi
  docker compose exec -T -u root -e SNAPSHOT="$documents" backup sh -c '
    set -eu
    cd /documents
    find . -path ./tmp -prune -o -type f -print | while read -r file; do
      [ -f "$SNAPSHOT/$file" ] || rm -f "$file"
    done
    cd "$SNAPSHOT"
    find . -type f -print | while read -r file; do
      [ -f "/documents/$file" ] && continue
      mkdir -p "/documents/$(dirname "$file")"
      cp "$file" "/documents/$file.restoring"
      mv "/documents/$file.restoring" "/documents/$file"
    done
    chown -R "$DOCUMENTS_OWNER" /documents
    find /documents -type d -exec chmod 755 {} +
    find /documents -type f -exec chmod 644 {} +
  ' || files_status=$?
  if [ -n "$local_documents" ]; then
    docker compose exec -T -u root backup rm -rf "$documents"
  fi
fi

docker compose start hestia-api
if [ "$status" -ne 0 ]; then
  echo "Restore failed; the database was left as it was." >&2
elif [ "$files_status" -ne 0 ]; then
  echo "The database is restored, but the document files are not. Run the restore again, or restore the" >&2
  echo "before-restore dump to go back." >&2
  status=$files_status
else
  echo "Restored $dump"
fi
exit "$status"
