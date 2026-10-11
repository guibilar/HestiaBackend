#!/bin/sh
# Fails when a module's migrations do not match its model (a model change without a migration).
# Each module has one DbContext, named <Module>DbContext. Run after `dotnet build` and `dotnet tool restore`.
set -eu

status=0
for module_dir in src/Modules/*/; do
  module=$(basename "$module_dir")
  project="${module_dir}Hestia.Modules.${module}"
  echo "Module ${module}"
  if ! dotnet ef migrations has-pending-model-changes \
      --project "$project" --startup-project src/Hestia.Api \
      --context "${module}DbContext" --no-build; then
    status=1
  fi
done
exit $status
