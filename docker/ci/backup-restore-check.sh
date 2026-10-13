#!/bin/sh
# CI check for the backups (T24). Run it from the directory with docker-compose.yml, with the stack up.
#   1. Adds a Sample item and a document with a file (T11), and makes a backup with backup.sh.
#   2. Restores the dump into a new, empty database, and checks the "sample" schema and the item, the document row,
#      and the copy of its file next to the dump.
#   3. Adds a second item and a second document, runs docker/restore.sh (dump from the volume, then from a file on
#      this machine), and checks that the database and the document files are back at the dump: the first item and
#      the first document only, with its file.
set -eu

api=${1:-http://localhost:5097}
# T03: the Sample endpoints need a signed-in user (HESTIA_USERNAME and HESTIA_PASSWORD, as for the smoke test).
cookies=$(mktemp)
trap 'rm -f "$cookies"' EXIT
sign_in() {
  curl -sf -c "$cookies" -H 'Content-Type: application/json' \
    -d "{\"username\":\"${HESTIA_USERNAME:?}\",\"password\":\"${HESTIA_PASSWORD:?}\"}" "$api/api/Auth/login" > /dev/null
}

# psql in the backup container (the connection comes from its environment). $1: database.
sql() { docker compose exec -T backup psql --no-psqlrc --tuples-only --no-align --dbname "$1" -c "$2"; }
items_named() { sql "$1" "SELECT count(*) FROM sample.\"Items\" WHERE \"Name\" = '$2'"; }
add_item() {
  curl -sf -b "$cookies" -X POST -H 'Content-Type: application/json' -d "{\"name\":\"$1\"}" "$api/api/sample/items" > /dev/null
}
# T11: a document with one file (a sample from the tests). Prints the SHA-256 of the file.
samples=tests/Hestia.Modules.Documents.Tests/Samples
add_document() { # title, sample file, content type
  curl -sf -b "$cookies" -F "title=$1" -F "files=@$samples/$2;type=$3" "$api/api/documents" | jq -r '.files[0].sha256'
}
documents_named() { sql "$1" "SELECT count(*) FROM documents.\"Documents\" WHERE \"Title\" = '$2'"; }
file_in_volume() { # sha256; prints yes or no
  if docker compose exec -T backup test -f "/documents/$(echo "$1" | cut -c1-2)/$1"; then echo yes; else echo no; fi
}
# Downloads the file of the document with this title, and prints its SHA-256.
download_sha() {
  id=$(curl -sf -b "$cookies" "$api/api/documents?pageSize=100" | jq -r --arg title "$1" '.items[] | select(.title == $title) | .id')
  file=$(curl -sf -b "$cookies" "$api/api/documents/$id" | jq -r '.files[0].id')
  curl -sf -b "$cookies" "$api/api/documents/$id/files/$file" | sha256sum | cut -d' ' -f1
}
wait_ready() {
  for i in $(seq 1 60); do curl -sf "$api/health/ready" > /dev/null && sign_in && return 0; sleep 1; done
  echo "The host is not ready." >&2
  return 1
}
fail() { echo "::error::$*" >&2; exit 1; }

sign_in
echo "1. Backup"
add_item before-backup
first_sha=$(add_document before-backup receipt.png image/png)
[ "$(file_in_volume "$first_sha")" = yes ] || fail "The uploaded file is not on the documents volume."
docker compose exec -T backup backup.sh once
dump=$(docker compose exec -T backup sh -c 'ls -t /backups/hestia-*.dump | head -n 1')
echo "Newest dump: $dump"
# Owner-only, and owned by the backup user (999, the image's "postgres" user), not by root.
[ "$(docker compose exec -T backup stat -c '%a %u' "$dump")" = "600 999" ] ||
  fail "The dump is not mode 600 and owned by uid 999."
snapshot="${dump%.dump}.documents"
docker compose exec -T backup test -f "$snapshot/$(echo "$first_sha" | cut -c1-2)/$first_sha" ||
  fail "The backup has no copy of the document file ($snapshot)."

echo "2. Restore into an empty database"
docker compose exec -T backup createdb restore_check
docker compose exec -T backup pg_restore --no-owner --exit-on-error --dbname restore_check "$dump"
[ "$(sql restore_check "SELECT count(*) FROM pg_namespace WHERE nspname = 'sample'")" = 1 ] ||
  fail "The restored database has no sample schema."
[ "$(sql restore_check 'SELECT count(*) > 0 FROM sample."__EFMigrationsHistory"')" = t ] ||
  fail "The restored sample schema has no migration history."
[ "$(items_named restore_check before-backup)" = 1 ] ||
  fail "The restored database does not have the Sample item."
[ "$(documents_named restore_check before-backup)" = 1 ] ||
  fail "The restored database does not have the document."
docker compose exec -T backup dropdb restore_check

echo "3. docker/restore.sh with the dump in the volume"
add_item after-backup
second_sha=$(add_document after-backup scanned-warranty.pdf application/pdf)
docker/restore.sh "$(basename "$dump")"
wait_ready
[ "$(items_named Hestia before-backup)" = 1 ] || fail "restore.sh lost the item from the dump."
[ "$(items_named Hestia after-backup)" = 0 ] || fail "restore.sh kept an item from after the dump."
[ "$(documents_named Hestia before-backup)" = 1 ] || fail "restore.sh lost the document from the dump."
[ "$(documents_named Hestia after-backup)" = 0 ] || fail "restore.sh kept a document from after the dump."
[ "$(file_in_volume "$second_sha")" = no ] || fail "restore.sh kept the file of a document from after the dump."
[ "$(download_sha before-backup)" = "$first_sha" ] || fail "The restored document does not download its file."
[ "$(docker compose exec -T backup stat -c '%u' "/documents/$(echo "$first_sha" | cut -c1-2)/$first_sha")" = 1654 ] ||
  fail "The restored file does not belong to the host's user (1654)."
docker compose exec -T backup sh -c 'ls /backups/before-restore-*.dump' > /dev/null ||
  fail "restore.sh did not save the database first."
curl -sf -b "$cookies" "$api/api/sample/items" | jq -e 'any(.. | objects; .name? == "before-backup")' > /dev/null ||
  fail "The host does not show the restored item."

echo "4. docker/restore.sh with a file on this machine"
add_item after-first-restore
add_document after-first-restore scanned-warranty.pdf application/pdf > /dev/null
copy=$(mktemp -d)/copy.dump
docker compose cp "backup:$dump" "$copy"
docker compose cp "backup:$snapshot" "${copy%.dump}.documents"
# The volume loses the first file too: the copy on this machine must bring it back.
docker compose exec -T -u root backup rm -f "/documents/$(echo "$first_sha" | cut -c1-2)/$first_sha"
docker/restore.sh "$copy"
wait_ready
[ "$(items_named Hestia after-first-restore)" = 0 ] || fail "restore.sh did not restore the copied file."
[ "$(items_named Hestia before-backup)" = 1 ] || fail "The copied file did not have the item."
[ "$(documents_named Hestia after-first-restore)" = 0 ] || fail "restore.sh kept a document from after the dump."
[ "$(file_in_volume "$second_sha")" = no ] || fail "restore.sh kept the file of a document from after the dump."
[ "$(download_sha before-backup)" = "$first_sha" ] || fail "The copied document files did not bring the file back."
rm -rf "$(dirname "$copy")"

echo "Backup and restore work."
