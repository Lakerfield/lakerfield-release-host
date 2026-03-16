# Lakerfield.ReleaseHost

A small .NET 10 file hosting service for application releases and static download pages. No database or cloud storage required.

- Upload files from CI/CD pipelines protected by a bearer token
- Serve release artifacts such as Velopack packages
- Host a root `index.html` with installation instructions or download links
- Optional SHA-256 checksum verification and atomic file writes

## Quick start

```bash
docker run -d \
  --name lakerfield-releasehost \
  -p 80:80 \
  -e Upload__Token="change-me" \
  -v ./data:/data/files \
  ghcr.io/lakerfield/release-host:latest
```

- `http://localhost/` — root page
- `http://localhost/upload/...` — upload endpoint
- `http://localhost/...` — download endpoint

## Docker Compose

```yaml
services:
  releasehost:
    image: ghcr.io/lakerfield/release-host:latest
    container_name: lakerfield-releasehost
    restart: unless-stopped
    ports:
      - "80:80"
    environment:
      Upload__Token: "change-me"
    volumes:
      - ./data:/data/files
```

## Upload example

```bash
FILE="MyApp-1.2.3-full.nupkg"
SHA=$(sha256sum "$FILE" | awk '{print $1}')

curl --fail-with-body -X PUT \
  -H "Authorization: Bearer $UPLOAD_TOKEN" \
  -H "Content-Type: application/octet-stream" \
  -H "X-Checksum-Sha256: $SHA" \
  --data-binary "@$FILE" \
  "https://releases.example.com/upload/stable/$FILE"
```

## Configuration

| Setting | Environment variable | Default | Description |
|---|---|---:|---|
| `Upload:Token` | `Upload__Token` | none | Bearer token required for uploads |
| `Upload:VerifyChecksum` | `Upload__VerifyChecksum` | `true` | Verifies `X-Checksum-Sha256` when present |
| `Upload:RequireChecksum` | `Upload__RequireChecksum` | `false` | Rejects uploads when checksum header is missing |
| `Storage:Root` | `Storage__Root` | `/data/files` | Root folder used for hosted files |

## Routing

```text
/                   -> /data/files/index.html
/stable/...         -> /data/files/stable/...
/dev/...            -> /data/files/dev/...
/upload/index.html  -> upload to /data/files/index.html
/upload/stable/...  -> upload to /data/files/stable/...
/upload/dev/...     -> upload to /data/files/dev/...
```

## Storage layout

```text
/data/files/
  index.html
  stable/
    releases.stable.json
    MyApp-1.2.3-full.nupkg
  dev/
    releases.dev.json
    MyApp-1.3.0-dev.1-full.nupkg
```

## Velopack

One ReleaseHost instance per application, channels separated by path:

```text
https://releases.example.com/stable/releases.stable.json
https://releases.example.com/dev/releases.dev.json
```

## Security

- Use HTTPS in front of the service
- Store files on a mounted volume
- Inject the upload token through secrets management
- Prefer checksum verification in CI
