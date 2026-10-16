#!/bin/sh
# Dumps the Hestia database (all module schemas) into /backups, in pg_dump's compressed custom format
# (for pg_restore). Copied from the finance backend (T24), for the Hestia database on PostgreSQL 17.
# T11: after each dump, it also copies the document files (the volume at /documents) into a folder next to the
# dump: hestia-<time>.dump and hestia-<time>.documents/. A file that the previous copy has already is a hard link to it
# (the files never change: their name is the SHA-256 of the content), so each copy takes space only for new files.
#   backup.sh                 dump now, then every day at $BACKUP_AT (the backup container runs this)
#   backup.sh once            dump now and exit (non-zero on failure)
#   backup.sh before-restore  dump now as before-restore-<time>.dump, which is never deleted (restore.sh)
#   backup.sh verify          restore the newest dump into a scratch database and check it (T99)
# T99: after each scheduled dump, the script also restores it into a scratch database (the restore check), unless
# BACKUP_VERIFY=false. It writes the result of both into $BACKUP_STATUS_DIR/hestia.json, which Hestia.Api reads for
# the "backups" readiness check and the System alerts on Today. That file has no secrets: everyone can read it.
# Connection from the standard PGHOST / PGUSER / PGPASSWORD / PGDATABASE variables. Dumps older than
# $BACKUP_KEEP_DAYS days are deleted, but only after a successful dump.
set -u
# The dumps hold the whole database (later: password hash, cookie keys, sessions): owner-only files.
umask 077

dir=/backups
documents=/documents
status_dir=${BACKUP_STATUS_DIR:-/status}
verify_enabled=${BACKUP_VERIFY:-true}
keep_days=${BACKUP_KEEP_DAYS:-14}
at=${BACKUP_AT:-03:00}

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }

# The container starts as root, so it can write to a new Docker volume (Docker makes it as root).
# Give the folder to the backup user, then run as that user. The default 999 is the image's
# "postgres" user. For a host folder (BACKUP_DIR=./backups), set BACKUP_UID/GID to its owner.
if [ "$(id -u)" = 0 ]; then
  uid=${BACKUP_UID:-999}
  gid=${BACKUP_GID:-999}
  mkdir -p "$dir"
  chown "$uid:$gid" "$dir"
  chmod 700 "$dir"
  # Dumps from an earlier owner (for example after a change of BACKUP_UID).
  find "$dir" -maxdepth 1 -name '*.dump' ! -user "$uid" -exec chown "$uid:$gid" {} +
  # A new documents volume that Docker made for this container belongs to root: give it to the host's user.
  if [ -d "$documents" ] && [ "$(stat -c %u "$documents")" = 0 ] && [ -n "${DOCUMENTS_OWNER:-}" ]; then
    chown "$DOCUMENTS_OWNER" "$documents"
  fi
  # T99: the status folder: readable by Hestia.Api (another user), writable by the backup user only.
  if [ -n "$status_dir" ]; then
    mkdir -p "$status_dir"
    chown "$uid:$gid" "$status_dir"
    chmod 755 "$status_dir"
  fi
  exec gosu "$uid:$gid" sh "$0" "$@"
fi

check_dir() {
  [ -d "$dir" ] && [ -w "$dir" ] && return 0
  log "BACKUP FAILED: $dir is not writable by uid $(id -u)." >&2
  return 1
}

# T99: the state of the newest backup and of the newest restore check, for Hestia.Api. Each value is kept until the
# next run of its kind changes it, in the shell variables below and in the file.
backup_at=""; backup_ok=""; backup_file=""; backup_size=""; backup_error=""
verify_at=""; verify_ok=""; verify_message=""
read_status() {
  file="$status_dir/hestia.json"
  [ -f "$file" ] || return 0
  # One value per line (write_status writes them so).
  value() { sed -n -e "s/^  \"$1\": \"\(.*\)\",\{0,1\}$/\1/p" -e "s/^  \"$1\": \([^\",]*\),\{0,1\}$/\1/p" "$file" | head -n 1; }
  backup_at=$(value lastBackupAt); backup_ok=$(value lastBackupOk); backup_file=$(value lastBackupFile)
  backup_size=$(value lastBackupBytes); backup_error=$(value lastBackupError)
  verify_at=$(value lastVerifyAt); verify_ok=$(value lastVerifyOk); verify_message=$(value lastVerifyMessage)
}
# A JSON string or null. Messages never hold quotes or backslashes (clean removes them).
json() { if [ -z "$1" ] || [ "$1" = null ]; then echo null; else echo "\"$1\""; fi; }
json_raw() { if [ -z "$1" ] || [ "$1" = null ]; then echo null; else echo "$1"; fi; }
clean() { printf '%s' "$*" | tr -d '"\\' | tr '\n\r\t' '   ' | cut -c1-300; }
write_status() {
  [ -n "$status_dir" ] && [ -d "$status_dir" ] && [ -w "$status_dir" ] || return 0
  # Written under a temporary name first, so a reader never sees half a file.
  { echo "{"
    echo "  \"name\": \"hestia\","
    echo "  \"lastBackupAt\": $(json "$backup_at"),"
    echo "  \"lastBackupOk\": $(json_raw "$backup_ok"),"
    echo "  \"lastBackupFile\": $(json "$backup_file"),"
    echo "  \"lastBackupBytes\": $(json_raw "$backup_size"),"
    echo "  \"lastBackupError\": $(json "$backup_error"),"
    echo "  \"lastVerifyAt\": $(json "$verify_at"),"
    echo "  \"lastVerifyOk\": $(json_raw "$verify_ok"),"
    echo "  \"lastVerifyMessage\": $(json "$verify_message")"
    echo "}"
  } > "$status_dir/.hestia.json.partial" && chmod 644 "$status_dir/.hestia.json.partial" &&
    mv "$status_dir/.hestia.json.partial" "$status_dir/hestia.json"
}
now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# $1: the file name prefix ("hestia" for the scheduled dumps).
backup() {
  name="$1-$(date +%Y-%m-%d_%H%M%S).dump"
  # Written under a temporary name first: a dump that fails halfway never looks like a backup.
  if error=$(pg_dump --format=custom --file="$dir/.$name.partial" 2>&1) && mv "$dir/.$name.partial" "$dir/$name"; then
    log "wrote $name ($(du -h "$dir/$name" | cut -f1))"
  else
    rm -f "$dir/.$name.partial"
    log "BACKUP FAILED: $error" >&2
    [ "$1" = hestia ] && { read_status; backup_at=$(now_iso); backup_ok=false; backup_error=$(clean "pg_dump failed: $error"); write_status; }
    return 1
  fi
  if ! copy_documents "${name%.dump}.documents"; then
    [ "$1" = hestia ] && { read_status; backup_at=$(now_iso); backup_ok=false; backup_file=$name
      backup_error="The dump is there, the document files are not."; write_status; }
    return 1
  fi
  if [ "$1" = hestia ]; then
    read_status; backup_at=$(now_iso); backup_ok=true; backup_file=$name; backup_error=""
    backup_size=$(stat -c %s "$dir/$name"); write_status
  fi
}

# T99: the restore check. Restores the newest scheduled dump into a scratch database (never the real one), checks that
# it has the module schemas with their tables, and that every document file of the dump is in its .documents folder.
# Then drops the scratch database. A backup that nobody restored is only a hope.
verify() {
  newest=$(ls -1t "$dir"/hestia-*.dump 2>/dev/null | head -n 1)
  read_status
  verify_at=$(now_iso)
  if [ -z "$newest" ]; then
    verify_ok=false; verify_message="No dump to check."; write_status
    log "RESTORE CHECK FAILED: no dump" >&2
    return 1
  fi
  scratch="${PGDATABASE}_restore_check"
  dropdb --if-exists "$scratch" 2>/dev/null
  message=""
  if ! error=$(createdb "$scratch" 2>&1); then
    message="Could not make the scratch database: $error"
  elif ! error=$(pg_restore --no-owner --exit-on-error --dbname="$scratch" "$newest" 2>&1); then
    message="pg_restore failed: $error"
  else
    tables=$(psql --no-psqlrc --tuples-only --no-align --dbname="$scratch" -c \
      "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'public')")
    schemas=$(psql --no-psqlrc --tuples-only --no-align --dbname="$scratch" -c \
      "SELECT count(DISTINCT table_schema) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'public')")
    missing=0
    files=0
    if [ "$(psql --no-psqlrc --tuples-only --no-align --dbname="$scratch" -c \
         "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'documents' AND table_name = 'Files'")" = 1 ]; then
      copy="${newest%.dump}.documents"
      for key in $(psql --no-psqlrc --tuples-only --no-align --dbname="$scratch" -c 'SELECT DISTINCT "StorageKey" FROM documents."Files"'); do
        files=$((files + 1))
        [ -f "$copy/$key" ] || missing=$((missing + 1))
      done
    fi
    # A new install dumps before the host made its tables: an empty dump of an empty database is right.
    live=$(psql --no-psqlrc --tuples-only --no-align -c \
      "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'public')")
    if [ "${tables:-0}" -eq 0 ] && [ "${live:-0}" -gt 0 ]; then
      message="The dump restored, but it has no tables (the database has $live)."
    elif [ "$missing" -gt 0 ]; then
      message="$missing of $files document files are not in $(basename "$copy")."
    else
      verify_ok=true
      verify_message=$(clean "$(basename "$newest") restored: $schemas schemas, $tables tables, $files document files.")
      [ "${tables:-0}" -eq 0 ] && verify_message=$(clean "$(basename "$newest") restored: the database had no tables yet.")
    fi
  fi
  dropdb --if-exists "$scratch" 2>/dev/null
  if [ -n "$message" ]; then
    verify_ok=false; verify_message=$(clean "$(basename "$newest"): $message"); write_status
    log "RESTORE CHECK FAILED: $verify_message" >&2
    return 1
  fi
  write_status
  log "restore check: $verify_message"
}

# The restore check after a scheduled dump, unless it is off. Its failure does not fail the backup.
verify_after_backup() {
  [ "$verify_enabled" = true ] || return 0
  verify || true
}

# T11: copies the document files into $dir/$1 (the dump name with .documents). After the dump: an upload adds its file
# before its row, and a delete removes the file after the row, so the files of every row in the dump are there.
copy_documents() {
  [ -d "$documents" ] || return 0
  target="$dir/$1"
  partial="$dir/.$1.partial"
  # The newest earlier copy: its files become hard links, so they take no new space.
  previous=$(ls -1td "$dir"/*.documents 2>/dev/null | head -n 1)
  rm -rf "$partial"
  mkdir -p "$partial"
  # "tmp" holds the uploads in progress, which are not documents yet.
  if (cd "$documents" && find . -path ./tmp -prune -o -type f -print) | while read -r file; do
       mkdir -p "$partial/$(dirname "$file")" &&
         { { [ -n "$previous" ] && [ -f "$previous/$file" ] && ln "$previous/$file" "$partial/$file" 2>/dev/null; } ||
           cp "$documents/$file" "$partial/$file"; } || exit 1
     done && mv "$partial" "$target"; then
    log "wrote $1 ($(find "$target" -type f | wc -l) files)"
  else
    rm -rf "$partial"
    log "DOCUMENTS BACKUP FAILED: the dump is there, the document files are not" >&2
    return 1
  fi
}

remove_old() {
  find "$dir" -maxdepth 1 -name 'hestia-*.dump' -mmin +$((keep_days * 1440)) -print -delete |
    while read -r old; do log "removed $(basename "$old") (older than $keep_days days)"; done
  find "$dir" -maxdepth 1 -type d -name 'hestia-*.documents' -mmin +$((keep_days * 1440)) -print |
    while read -r old; do rm -rf "$old" && log "removed $(basename "$old") (older than $keep_days days)"; done
}

seconds_until_next_run() {
  now=$(date +%s)
  next=$(date -d "today $at" +%s)
  [ "$next" -gt "$now" ] || next=$(date -d "tomorrow $at" +%s)
  echo $((next - now))
}

case "${1:-schedule}" in
  once)
    check_dir && backup hestia && remove_old
    ;;
  verify)
    check_dir && verify
    ;;
  before-restore)
    check_dir && backup before-restore
    ;;
  schedule)
    # As the container's main process, stop at once on `docker compose stop`/`down` (a foreground sleep
    # would hold the signal until Docker's 10-second timeout kills the container).
    sleeper=""
    trap 'log "stopping"; [ -n "$sleeper" ] && kill "$sleeper" 2>/dev/null; exit 0' TERM INT
    # Exits, so the container restarts and `docker compose up --wait` fails with the message above.
    check_dir || exit 1
    backup hestia && remove_old && verify_after_backup
    while true; do
      wait=$(seconds_until_next_run)
      log "next backup at $at (in $((wait / 60)) min)"
      sleep "$wait" &
      sleeper=$!
      wait "$sleeper"
      # A failure is logged and retried the next day; the health check flags a missing recent dump.
      { backup hestia && remove_old && verify_after_backup; } || true
    done
    ;;
  *)
    echo "usage: $0 [once|before-restore|verify|schedule]" >&2
    exit 2
    ;;
esac
