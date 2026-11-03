#!/usr/bin/env bash
# Arch hosts ship wide ncurses only; isolate Unity Clang's dependencies.
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
[[ "$(uname -m)" == x86_64 ]] || { echo 'This helper requires x86_64 Linux' >&2; exit 2; }
mkdir -p "$ROOT/.ci-cache/linux-compat"
docker run --rm --network bridge -v "$ROOT/.ci-cache/linux-compat:/out" debian:stable sh -c '
set -e
apt-get update -qq
cd /out
apt-get download libncurses6 libtinfo6
for package in ./*.deb; do dpkg-deb -x "$package" /out; done
'
[[ -f "$ROOT/.ci-cache/linux-compat/usr/lib/x86_64-linux-gnu/libncurses.so.6" ]]
echo 'Unity Clang compatibility libraries ready; system libraries unchanged.'
