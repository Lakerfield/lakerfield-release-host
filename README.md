# Lakerfield.ReleaseHost

Lakerfield.ReleaseHost is a small .NET 10 file hosting service for application releases and static download pages.

It is designed for simple deployment scenarios where you want to:

- upload files over HTTP from CI/CD pipelines
- serve release artifacts such as Velopack packages
- host a root `index.html` page with installation instructions or download links
- protect uploads with a single bearer token
- optionally verify uploads with SHA-256 checksums
- write files atomically to avoid partially written artifacts being served

The service is intentionally minimal. It does not require a database, cloud storage, or an external authentication provider.

## Features

- .NET 10
- Docker-friendly
- Single upload endpoint
- Bearer token authentication for uploads
- Optional SHA-256 checksum validation
- Atomic file writes using temp file + replace
- Static file hosting from a mounted volume
- Root `index.html` support
- Channel-based paths for release feeds such as `stable` and `dev`

## Routing

The service uses the following URL structure:

```text
/                   -> /data/files/index.html
/stable/...         -> /data/files/stable/...
/dev/...            -> /data/files/dev/...
/upload/index.html  -> upload to /data/files/index.html
/upload/stable/...  -> upload to /data/files/stable/...
/upload/dev/...     -> upload to /data/files/dev/...
```

## Example storage layout

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

## Configuration

Configuration can be provided through `appsettings.json` or environment variables.

### Settings

| Setting | Environment variable | Default | Description |
|---|---|---:|---|
| `Upload:Token` | `Upload__Token` | none | Bearer token required for uploads |
| `Upload:VerifyChecksum` | `Upload__VerifyChecksum` | `true` | Verifies `X-Checksum-Sha256` when present |
| `Upload:RequireChecksum` | `Upload__RequireChecksum` | `false` | Rejects uploads when checksum header is missing |
| `Storage:Root` | `Storage__Root` | `/data/files` | Root folder used for hosted files |

## Quick start

This is the smallest useful example.

```bash
docker run -d --name lakerfield-releasehost -p 80:80 -e Upload__Token="change-me" -v ./data:/data/files ghcr.io/lakerfield/release-host:latest
```

After that:

- open `http://localhost/` for the root page
- upload files to `http://localhost/upload/...`
- download files from `http://localhost/...`

## Simple example

### Upload `index.html`

```bash
FILE="index.html"
SHA=$(sha256sum "$FILE" | awk '{print $1}')

curl -X PUT   -H "Authorization: Bearer change-me"   -H "Content-Type: text/html; charset=utf-8"   -H "X-Checksum-Sha256: $SHA"   --data-binary "@$FILE"   "http://localhost/upload/index.html"
```

### Upload a stable release artifact

```bash
FILE="MyApp-1.2.3-full.nupkg"
SHA=$(sha256sum "$FILE" | awk '{print $1}')

curl -X PUT   -H "Authorization: Bearer change-me"   -H "Content-Type: application/octet-stream"   -H "X-Checksum-Sha256: $SHA"   --data-binary "@$FILE"   "http://localhost/upload/stable/$FILE"
```

### Download the uploaded file

```bash
curl -O "http://localhost/stable/MyApp-1.2.3-full.nupkg"
```

## Extended example

### Docker Compose

```yaml
services:
  releasehost:
    image: ghcr.io/lakerfield/lakerfield.releasehost:latest
    container_name: lakerfield-releasehost
    restart: unless-stopped
    ports:
      - "80:80"
    environment:
      Upload__Token: "change-me"
      Upload__VerifyChecksum: "true"
      Upload__RequireChecksum: "false"
      Storage__Root: "/data/files"
    volumes:
      - ./data:/data/files
```

### Example CI upload step using curl

```bash
FILE="MyApp-1.2.3-full.nupkg"
SHA=$(sha256sum "$FILE" | awk '{print $1}')
BASE_URL="https://releases.example.com"
TOKEN="$UPLOAD_TOKEN"

curl --fail-with-body -X PUT   -H "Authorization: Bearer $TOKEN"   -H "Content-Type: application/octet-stream"   -H "X-Checksum-Sha256: $SHA"   --data-binary "@$FILE"   "$BASE_URL/upload/stable/$FILE"
```

### Example `index.html`

This can be used as a simple landing page for end users.

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>MyApp Downloads</title>
</head>
<body>
  <h1>MyApp</h1>
  <p>Download the latest stable version:</p>
  <ul>
    <li><a href="/stable/MyApp-1.2.3-full.nupkg">Stable package</a></li>
    <li><a href="/stable/releases.stable.json">Stable release feed</a></li>
  </ul>
  <p>Test builds are available in the <a href="/dev/">dev</a> channel if you expose them.</p>
</body>
</html>
```

## Velopack notes

A practical layout for Velopack is one ReleaseHost instance per application.

Example:

```text
https://releases.example.com/
https://releases.example.com/stable/releases.stable.json
https://releases.example.com/dev/releases.dev.json
```

This keeps channels separated by path while still allowing the root page to contain installation instructions, links, or troubleshooting notes.

## Security notes

- Upload authentication is based on a single bearer token.
- Use HTTPS in front of the service.
- Store files on a mounted volume.
- Prefer checksum verification in CI.
- For stricter environments, inject the upload token through secrets management instead of plain environment variables.

## Docker image

The container listens on port `80` inside the container.

Example:

```bash
docker run -d   --name releasehost   -p 80:80   -e Upload__Token="change-me"   -v ./data:/data/files   ghcr.io/lakerfield/lakerfield.releasehost:latest
```
