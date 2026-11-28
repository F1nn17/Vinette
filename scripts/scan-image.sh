#!/bin/sh
# Scans a container image for known vulnerabilities with Trivy and, on request, writes its SBOM (VIN-123).
#
#   scripts/scan-image.sh IMAGE            list HIGH and CRITICAL findings; fail when there is a CRITICAL one that has a fix
#   scripts/scan-image.sh IMAGE SBOM.json  the same, then write the list of the image's components (SPDX JSON) to SBOM.json
#
# Trivy runs from its own image (pinned below), so nothing but Docker is needed. For an image in a registry that wants a login,
# set TRIVY_USERNAME and TRIVY_PASSWORD. Findings that were looked at and accepted go to .trivyignore, each with the reason.
set -eu

# Update the tag and the digest together: docker buildx imagetools inspect aquasec/trivy:TAG
TRIVY_IMAGE="aquasec/trivy:0.76.2@sha256:8f6bb947dbc528902e11f2f5be52f7b519f6a39d5d148f41166f2040499768fb"

image="${1:?usage: scan-image.sh IMAGE [SBOM.json]}"
sbom="${2:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"

trivy() {
  # MSYS_NO_PATHCONV keeps Git Bash on Windows from rewriting the mount paths.
  MSYS_NO_PATHCONV=1 docker run --rm \
    -e TRIVY_USERNAME -e TRIVY_PASSWORD -e TRIVY_DISABLE_TELEMETRY=true \
    -v /var/run/docker.sock:/var/run/docker.sock \
    -v "$root/.trivyignore:/.trivyignore:ro" \
    "$TRIVY_IMAGE" "$@"
}

echo "== HIGH and CRITICAL findings (those without a fix yet are listed too) =="
trivy image --ignorefile /.trivyignore --severity HIGH,CRITICAL --quiet "$image"

echo "== The gate: a CRITICAL finding that has a fix fails =="
trivy image --ignorefile /.trivyignore --severity CRITICAL --ignore-unfixed --exit-code 1 --quiet "$image"
echo "No CRITICAL finding with a fix."

if [ -n "$sbom" ]; then
  trivy image --format spdx-json --quiet "$image" > "$sbom"
  echo "SBOM written to $sbom ($(wc -c < "$sbom") bytes)."
fi
