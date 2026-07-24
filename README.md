# Lakerfield.ReleaseHost

A small .NET 10 file hosting service for multi-scope application releases and static download pages.
No database or cloud storage required.

- **S3-compatible API** (primary) — Velopack and other S3 clients upload directly using per-scope credentials
- **Bearer-token upload** for root static assets (index page, CSS, images)
- Serve release artifacts such as Velopack packages on a per-scope/bucket basis
- Host a root `index.html` with installation instructions or download links
- Optional SHA-256 checksum verification and atomic file writes

## Quick start

1. Mount a volume, create `config.json` inside it with your scopes and keys.
2. Run the container — no extra environment variables required for S3 uploads.

```bash
docker run -d \
  --name lakerfield-releasehost \
  -p 80:80 \
  -v ./data:/data/files \
  ghcr.io/lakerfield/release-host:latest
```

- `http://localhost/` — root page (`index.html` from storage root, or 404)
- `http://localhost/{bucket}/{*key}` — S3-compatible upload / download endpoint
- `http://localhost/upload/{*relativePath}` — bearer-token upload for root static assets
- `http://localhost/health` — health check (reports configured scopes)

## Storage config (`config.json`)

Create `config.json` at the root of the mounted storage volume (e.g. `/data/files/config.json`).
The file is loaded at startup and controls all upload credentials.

```json
{
  "rootUploadKey": "root-secret",
  "scopes": {
    "ui": {
      "s3AccessKey": "ui-access-key",
      "s3SecretKey": "ui-secret-key"
    },
    "service": {
      "s3AccessKey": "service-access-key",
      "s3SecretKey": "service-secret-key"
    }
  }
}
```

| Field | Description |
|---|---|
| `rootUploadKey` | ****** for root static asset uploads via `/upload/...` |
| `scopes.<name>.s3AccessKey` | S3 access key ID for this bucket/scope |
| `scopes.<name>.s3SecretKey` | S3 secret access key for this bucket/scope |

> **Tip:** Each product or application component becomes its own scope (bucket). Add as many scopes as needed.

## Docker Compose

```yaml
services:
  releasehost:
    image: ghcr.io/lakerfield/release-host:latest
    container_name: lakerfield-releasehost
    restart: unless-stopped
    ports:
      - "80:80"
    volumes:
      - ./data:/data/files
```

Then place your `config.json` in `./data/config.json` before starting the container.

## S3-compatible upload (primary — for Velopack and CI)

The service accepts standard S3 PUT/GET/HEAD requests authenticated with **AWS Signature Version 4**.
The bucket name is the release scope (e.g. `ui`, `service`).
Credentials are loaded from `config.json` in the storage root — one access/secret key pair per scope.

### Behaviour

| Operation | Path | Auth | Description |
|---|---|---|---|
| `PUT /{bucket}/{*key}` | e.g. `/ui/releases.stable.json` | SigV4 with scope credentials | Upload object |
| `HEAD /{bucket}/{*key}` | e.g. `/ui/MyApp-1.2.3-full.nupkg` | SigV4 with scope credentials | Check existence / metadata |
| `GET /{bucket}/{*key}` | e.g. `/ui/MyApp-1.2.3-full.nupkg` | SigV4 with scope credentials | Download object |

- Unknown bucket/scope → S3 `NoSuchBucket` (404).
- `GET` requests without S3 auth fall through to the public static file server.
- When `x-amz-content-sha256` contains a valid 64-character hex SHA-256 hash **and**
  `Upload:VerifyChecksum` is `true`, the server verifies upload integrity and returns
  `400 InvalidDigest` on mismatch.
- Chunked/streaming uploads (`STREAMING-AWS4-HMAC-SHA256-PAYLOAD`, `UNSIGNED-PAYLOAD`) are
  accepted; payload hash verification is skipped for those.

### AWS CLI example

```bash
aws s3 cp MyApp-1.2.3-full.nupkg s3://ui/MyApp-1.2.3-full.nupkg \
  --endpoint-url http://localhost \
  --aws-access-key-id ui-access-key \
  --aws-secret-access-key ui-secret-key \
  --region us-east-1
```

### boto3 example

```python
import boto3

s3 = boto3.client(
    "s3",
    endpoint_url="http://localhost",
    aws_access_key_id="ui-access-key",
    aws_secret_access_key="ui-secret-key",
    region_name="us-east-1",
)
s3.upload_file("MyApp-1.2.3-full.nupkg", "ui", "MyApp-1.2.3-full.nupkg")
```

## Root static asset upload (bearer token)

Use bearer-token upload for root-level static files: the landing page, CSS, images, etc.
These are served directly from the storage root.

> **Note:** Uploading into configured scope directories (e.g. `/upload/ui/...`) is blocked on
> this endpoint — use the S3 API for release artifacts.

```bash
# Upload index.html
curl --fail-with-body -X PUT \
  -H "Authorization: ******" \
  -H "Content-Type: text/html" \
  --data-binary "@index.html" \
  "https://releases.example.com/upload/index.html"

# Upload a CSS file
curl --fail-with-body -X PUT \
  -H "Authorization: ******" \
  -H "Content-Type: text/css" \
  --data-binary "@styles/site.css" \
  "https://releases.example.com/upload/styles/site.css"

# Upload an image
curl --fail-with-body -X PUT \
  -H "Authorization: ******" \
  -H "Content-Type: image/png" \
  --data-binary "@assets/logo.png" \
  "https://releases.example.com/upload/assets/logo.png"
```

## Configuration

| Setting | Environment variable | Default | Description |
|---|---|---|---|
| `Upload:VerifyChecksum` | `Upload__VerifyChecksum` | `false` | Verifies `X-Checksum-Sha256` / `x-amz-content-sha256` when present |
| `Upload:RequireChecksum` | `Upload__RequireChecksum` | `false` | Rejects uploads when checksum header is missing |
| `Storage:Root` | `Storage__Root` | `/data/files` | Root folder used for hosted files and `config.json` |

S3 credentials and the root upload key are configured exclusively in `${Storage:Root}/config.json`.
There are no global S3 environment variables.

## Routing

```text
/                         -> /data/files/index.html  (or 404)
/ui/...                   -> /data/files/ui/...       (public static files)
/service/...              -> /data/files/service/...  (public static files)
/upload/index.html        -> upload to /data/files/index.html        (bearer auth)
/upload/styles/site.css   -> upload to /data/files/styles/site.css   (bearer auth)
/upload/assets/logo.png   -> upload to /data/files/assets/logo.png   (bearer auth)
PUT /ui/{*key}            -> upload to /data/files/ui/{key}          (S3 SigV4 auth)
PUT /service/{*key}       -> upload to /data/files/service/{key}     (S3 SigV4 auth)
```

## Storage layout

```text
/data/files/
  config.json           <- credentials / scope config (not served publicly)
  index.html            <- root landing page
  styles/
    site.css
  assets/
    logo.png
  ui/
    releases.stable.json
    MyApp-1.2.3-full.nupkg
  service/
    releases.stable.json
    MyApp-1.2.3-full.nupkg
```

## Velopack

Multiple product components on one release host, each using its own S3 bucket:

```text
https://releases.example.com/ui/releases.stable.json
https://releases.example.com/service/releases.stable.json
```

Configure Velopack (or your CI pipeline) to use the S3 endpoint per component:

```bash
# UI component
vpk upload s3 \
  --bucket ui \
  --endpoint http://releases.example.com \
  --keyId ui-access-key \
  --secret ui-secret-key \
  --region us-east-1

# Service component
vpk upload s3 \
  --bucket service \
  --endpoint http://releases.example.com \
  --keyId service-access-key \
  --secret service-secret-key \
  --region us-east-1
```

## Security

- Use HTTPS in front of the service
- Store the mounted volume securely — `config.json` contains all upload credentials
- Inject secrets via secrets management (e.g. Kubernetes Secret mounted as a file)
- Unknown S3 buckets return `NoSuchBucket` (404) — only configured scopes are accessible
- Bearer-token uploads are confined to root/static assets; scope directories are protected
