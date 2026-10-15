#!/usr/bin/env bash
# T44: FinanceProxy:ReadOnlyQueries (appsettings.json) must list the same paths as finance's [ReadOnlyQuery] endpoints,
# which finance keeps in docs/read-only-queries.txt (checked by a finance test). A difference fails with an annotation.
# Usage: scripts/check-finance-read-only-queries.sh [finance list URL or file]
set -euo pipefail
source="${1:-https://raw.githubusercontent.com/guibilar/PersonalFinanceInsightsBackend/master/docs/read-only-queries.txt}"
if [[ "$source" == http* ]]; then
  finance=$(curl -fsSL --retry 3 "$source")
else
  finance=$(cat "$source")
fi
finance=$(printf '%s\n' "$finance" | sed '/^\s*$/d' | sort)
host=$(jq -r '.FinanceProxy.ReadOnlyQueries[]' src/Hestia.Api/appsettings.json | sort)
if [ "$finance" = "$host" ]; then
  echo "FinanceProxy:ReadOnlyQueries matches finance ($(printf '%s\n' "$host" | wc -l) paths)."
  exit 0
fi
missing=$(comm -23 <(printf '%s\n' "$finance") <(printf '%s\n' "$host") | paste -sd, -)
extra=$(comm -13 <(printf '%s\n' "$finance") <(printf '%s\n' "$host") | paste -sd, -)
echo "::error title=FinanceProxy:ReadOnlyQueries differs from finance::Add to src/Hestia.Api/appsettings.json: ${missing:-none}. Remove: ${extra:-none}. Finance list: $source"
exit 1
