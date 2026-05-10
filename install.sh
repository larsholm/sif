#!/usr/bin/env bash
# Install sif from a hosted source archive without requiring a manual clone.

set -euo pipefail

REPO_URL="${SIF_INSTALL_REPO:-https://github.com/sif/sif}"
REF="${SIF_INSTALL_REF:-main}"
ARCHIVE_URL="${REPO_URL%/}/archive/refs/heads/${REF}.tar.gz"
TMP_DIR="$(mktemp -d)"

cleanup() {
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

need() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "Missing required command: $1" >&2
        exit 1
    fi
}

need bash
need curl
need dotnet
need tar
need zip

echo "Downloading sif from ${REPO_URL} (${REF})..."
curl -fsSL "$ARCHIVE_URL" | tar -xz -C "$TMP_DIR" --strip-components=1

echo "Installing sif..."
bash "$TMP_DIR/sif.agent/build.sh" install
