# Source only from a trusted local checkout; never print configuration values.
# Shell operational scripts accept shell syntax. The portable Unity launcher
# deliberately accepts literal KEY=value syntax only (see .env.example).
BLITZ_ENV_PATH="${BLITZ_ENV_FILE:-$PROJ/.env}"
if [ ! -f "$BLITZ_ENV_PATH" ] && [ -z "${BLITZ_ENV_FILE:-}" ]; then
  BLITZ_ENV_PATH="$PROJ/.env.release.local"
fi
if [ -f "$BLITZ_ENV_PATH" ]; then
  set -a
  . "$BLITZ_ENV_PATH"
  set +a
fi
unset BLITZ_ENV_PATH
