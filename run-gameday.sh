#!/usr/bin/env bash
# Game day: a Release build run standalone (SQLite, no Aspire or Docker), with the Mac kept awake.
# Usage: ./run-gameday.sh            (APP_DIR and PORT can be overridden from the environment)
set -euo pipefail

REPO="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$REPO/src/ScoutingAutomation.Web"
APP_DIR="${APP_DIR:-$HOME/du-app}"
PORT="${PORT:-5231}"

dotnet publish "$PROJECT" -c Release -o "$APP_DIR" --nologo

# Runtime settings are never published: take them over once from the project folder,
# so the reviewer names and rules stay the same. Accounts live in the database.
for f in runtime-settings.json; do
  if [[ ! -e "$APP_DIR/$f" && -e "$PROJECT/$f" ]]; then
    cp "$PROJECT/$f" "$APP_DIR/"
  fi
done

# User secrets (the Slack tokens) are only read in Development: pass them on as environment variables
while IFS= read -r line; do
  key="${line%% = *}"
  export "${key//:/__}=${line#* = }"
done < <(dotnet user-secrets list --project "$PROJECT" | grep ' = ' || true)

if [[ -z "${Scouting__Slack__BotToken:-}" || -z "${Scouting__Slack__AppToken:-}" ]]; then
  echo "No Slack BotToken or AppToken in the user secrets: the app would not connect to Slack." >&2
  exit 1
fi

export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS="http://0.0.0.0:$PORT"
# The points file is not published; reading it from the repository keeps edits working after a restart
export Scouting__AssignmentPointsFile="$PROJECT/opdrachtenpunten.csv"

# The SQLite database (scouting_dev.db) is relative to the working folder, so it lives in APP_DIR
cd "$APP_DIR"

ip="$(ipconfig getifaddr en0 2>/dev/null || true)"
echo "DU Review on http://localhost:$PORT${ip:+ and http://$ip:$PORT}; database in $APP_DIR. Ctrl+C stops."

# -d display, -i idle, -m disk, -s system (on power): no sleep while the app runs
exec caffeinate -dims ./ScoutingAutomation.Web
