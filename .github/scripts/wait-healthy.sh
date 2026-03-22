#!/usr/bin/env bash
# Waits until <url>/health/ready answers 200. Allows for a cold start from zero replicas.
# Usage: wait-healthy.sh <base-url>
set -euo pipefail

url="$1/health/ready"
for attempt in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$url" || true)
  echo "  attempt $attempt: $code"
  if [ "$code" = "200" ]; then
    exit 0
  fi
  sleep 10
done

echo "::error::$url did not become healthy within 5 minutes."
exit 1
