#!/bin/sh
# Dumps the Hestia database (all module schemas) into /backups, in pg_dump's compressed custom format
# (for pg_restore). Copied from the finance backend (T24), for the Hestia database on PostgreSQL 17.
# T11: after each dump, it also copies the document files (the volume at /documents) into a folder next to the
# dump: hestia-<time>.dump and hestia-<time>.documents/. A file that the previous copy has already is a hard link to it
# (the files never change: their name is the SHA-256 of the content), so each copy takes space only for new files.
#   backup.sh                 dump now, then every day at $BACKUP_AT (the backup container runs this)
#   backup.sh once            dump now and exit (non-zero on failure)
#   backup.sh before-restore  dump now as before-restore-<time>.dump, which is never deleted (restore.sh)
# Connection from the standard PGHOST / PGUSER / PGPASSWORD / PGDATABASE variables. Dumps older than
# $BACKUP_KEEP_DAYS days are deleted, but only after a successful dump.
set -u
# The dumps hold the whole database (later: password hash, cookie keys, sessions): owner-only files.
umask 077

dir=/backups
documents=/documents
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
  exec gosu "$uid:$gid" sh "$0" "$@"
fi

check_dir() {
  [ -d "$dir" ] && [ -w "$dir" ] && return 0
  log "BACKUP FAILED: $dir is not writable by uid $(id -u)." >&2
  return 1
}

# $1: the file name prefix ("hestia" for the scheduled dumps).
backup() {
  name="$1-$(date +%Y-%m-%d_%H%M%S).dump"
  # Written under a temporary name first: a dump that fails halfway never looks like a backup.
  if pg_dump --format=custom --file="$dir/.$name.partial" && mv "$dir/.$name.partial" "$dir/$name"; then
    log "wrote $name ($(du -h "$dir/$name" | cut -f1))"
  else
    rm -f "$dir/.$name.partial"
    log "BACKUP FAILED" >&2
    return 1
  fi
  copy_documents "${name%.dump}.documents"
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
    backup hestia && remove_old
    while true; do
      wait=$(seconds_until_next_run)
      log "next backup at $at (in $((wait / 60)) min)"
      sleep "$wait" &
      sleeper=$!
      wait "$sleeper"
      # A failure is logged and retried the next day; the health check flags a missing recent dump.
      { backup hestia && remove_old; } || true
    done
    ;;
  *)
    echo "usage: $0 [once|before-restore|schedule]" >&2
    exit 2
    ;;
esac
