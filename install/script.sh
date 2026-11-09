#!/usr/bin/env bash
set -Eeuo pipefail

# Ga naar de directory waarin dit script staat
cd "$(dirname "$(readlink -f "$0")")"

# Configuratie
NAME="release-host"
IMAGE="ghcr.io/lakerfield/release-host:latest"
ENV_FILE="$PWD/.env"
DATA_DIR="$PWD/data"

# Controleer of het configuratiebestand bestaat
if [[ ! -f "$ENV_FILE" ]]; then
  echo "Ontbreekt: $ENV_FILE (kopieer .env.example naar .env)" >&2
  exit 1
fi

# Waarschuw als de credentials onvoldoende beschermd zijn
if [[ "$(stat -c '%a' "$ENV_FILE")" != "600" ]]; then
  echo "Waarschuwing: gebruik chmod 600 .env" >&2
fi

# Maak de directory voor persistente opslag aan
mkdir -p "$DATA_DIR"

# Download de nieuwste containerimage
podman pull "$IMAGE"

# Stop en verwijder de bestaande container indien aanwezig
if podman container exists "$NAME"; then
  podman rm -f "$NAME"
fi

# Start de container met configuratie, poort en opslag
podman run -d \
  --name "$NAME" \
  --restart=unless-stopped \
  --publish 127.0.0.1:2001:80 \
  --env-file "$ENV_FILE" \
  --volume "$DATA_DIR:/data/files" \
  "$IMAGE"

# Toon de containerstatus
podman ps --filter "name=$NAME"

# Toon het lokale endpoint
echo "ReleaseHost: http://127.0.0.1:2001/"
