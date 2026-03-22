#!/usr/bin/env bash
# Points the migrate job at an image, starts it and waits for the result.
# Usage: run-migration.sh <resource-group> <job-name> <image>
set -euo pipefail

resource_group=$1
job=$2
image=$3

echo "Updating $job to $image"
az containerapp job update -g "$resource_group" -n "$job" --image "$image" -o none --only-show-errors

execution=$(az containerapp job start -g "$resource_group" -n "$job" --query name -o tsv --only-show-errors)
echo "Started execution $execution"

for _ in $(seq 1 60); do
  status=$(az containerapp job execution show -g "$resource_group" -n "$job" \
    --job-execution-name "$execution" --query properties.status -o tsv --only-show-errors)
  echo "  status: $status"
  case "$status" in
    Succeeded)
      exit 0
      ;;
    Failed | Stopped | Degraded)
      echo "::error::Migration $execution ended with status $status. Check ContainerAppConsoleLogs_CL in Log Analytics."
      exit 1
      ;;
  esac
  sleep 10
done

echo "::error::Migration $execution did not finish within 10 minutes."
exit 1
