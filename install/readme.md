# ReleaseHost - Podman Deployment

Eenvoudige deployment van [Lakerfield ReleaseHost](https://github.com/lakerfield/lakerfield-release-host) met Podman op Ubuntu Server.

## Requirements

- Ubuntu Server 24.04 LTS of nieuwer
- Podman

```bash
sudo apt update
sudo apt install -y podman
```

## Configuration

Maak een `.env`-bestand op basis van het voorbeeld:

```bash
cp .env.example .env
nano .env
chmod 600 .env
```

Configureer de uploadtoken en S3-credentials:

```dotenv
Upload\_\_Token=YOUR\_UPLOAD\_TOKEN
S3\_\_AccessKey=YOUR\_ACCESS\_KEY
S3\_\_SecretKey=YOUR\_SECRET\_KEY
```

## Start / Update

```bash
chmod +x script.sh
./script.sh
```

Het script:

1. Downloadt de nieuwste containerimage van GHCR.
2. Stopt en verwijdert een eventueel bestaande container.
3. Start de container op `127.0.0.1:2001`.
4. Bewaart bestanden persistent in `./data`.

**Endpoint:** http://127.0.0.1:2001/

## Management

```bash
# Containerstatus
podman ps

# Logs bekijken
podman logs -f release-host

# Container stoppen
podman stop release-host

# Container starten
podman start release-host
```

## Notes

- De container is uitsluitend via localhost bereikbaar.
- `./data` blijft behouden bij updates.
- Elke uitvoering van `script.sh` vervangt de bestaande container en veroorzaakt mogelijk korte downtime.
- Voeg `.env` en `data/` toe aan `.gitignore`.
- Voor automatisch starten na een serverreboot wordt systemd Quadlet aanbevolen.
