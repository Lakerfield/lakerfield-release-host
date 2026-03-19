# Lakerfield.ReleaseHost

A small .NET 10 file hosting service for application releases and static download pages. No database or cloud storage required.

- Upload files from CI/CD pipelines protected by a bearer token **or AWS Signature V4**
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
- `http://localhost/upload/...` — bearer-token upload endpoint
- `http://localhost/...` — S3-compatible upload / download endpoint
- `http://localhost/health` — health check

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

## Upload example (bearer token)

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

## S3-compatible upload

When `S3:AccessKey` and `S3:SecretKey` are configured the service accepts
standard S3 `PUT` and `HEAD` requests authenticated with **AWS Signature
Version 4**.  Files are stored under the same `Storage:Root` as bearer-token
uploads — the bucket name becomes the first directory level.

### Configuration

| Setting | Environment variable | Default | Description |
|---|---|---|---|
| `Upload:Token` | `Upload__Token` | none | Bearer token required for uploads |
| `Upload:VerifyChecksum` | `Upload__VerifyChecksum` | `true` | Verifies `X-Checksum-Sha256` / `x-amz-content-sha256` when present |
| `Upload:RequireChecksum` | `Upload__RequireChecksum` | `false` | Rejects uploads when checksum header is missing |
| `Storage:Root` | `Storage__Root` | `/data/files` | Root folder used for hosted files |
| `S3:AccessKey` | `S3__AccessKey` | _(disabled)_ | S3 access key ID — enables S3 API when set together with `S3:SecretKey` |
| `S3:SecretKey` | `S3__SecretKey` | _(disabled)_ | S3 secret access key — enables S3 API when set together with `S3:AccessKey` |

### Docker Compose with S3 API enabled

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
      S3__AccessKey: "my-access-key"
      S3__SecretKey: "my-secret-key"
    volumes:
      - ./data:/data/files
```

### AWS CLI example

```bash
aws s3 cp MyApp-1.2.3-full.nupkg s3://stable/MyApp-1.2.3-full.nupkg \
  --endpoint-url http://localhost \
  --aws-access-key-id my-access-key \
  --aws-secret-access-key my-secret-key \
  --region us-east-1
```

### boto3 example

```python
import boto3

s3 = boto3.client(
    "s3",
    endpoint_url="http://localhost",
    aws_access_key_id="my-access-key",
    aws_secret_access_key="my-secret-key",
    region_name="us-east-1",
)
s3.upload_file("MyApp-1.2.3-full.nupkg", "stable", "MyApp-1.2.3-full.nupkg")
```

### S3 API behaviour

| Operation | Path | Description |
|---|---|---|
| `PUT /{bucket}/{*key}` | e.g. `/stable/MyApp-1.2.3-full.nupkg` | Upload object |
| `HEAD /{bucket}/{*key}` | e.g. `/stable/MyApp-1.2.3-full.nupkg` | Check existence / metadata |
| `GET /{bucket}/{*key}` | e.g. `/stable/MyApp-1.2.3-full.nupkg` | Download (public, no auth required) |

- The bucket name maps directly to a directory under `Storage:Root`.
- When `x-amz-content-sha256` contains a valid 64-character hex SHA-256 hash
  **and** `Upload:VerifyChecksum` is `true`, the server verifies the upload
  integrity and returns `400 InvalidDigest` on mismatch.
- Chunked / streaming uploads (`STREAMING-AWS4-HMAC-SHA256-PAYLOAD`,
  `UNSIGNED-PAYLOAD`) are accepted; payload hash verification is skipped for
  those.
- `GET` downloads fall through to the existing static-file server and are
  publicly accessible without authentication.

## Configuration

| Setting | Environment variable | Default | Description |
|---|---|---|---|
| `Upload:Token` | `Upload__Token` | none | Bearer token required for uploads |
| `Upload:VerifyChecksum` | `Upload__VerifyChecksum` | `true` | Verifies `X-Checksum-Sha256` when present |
| `Upload:RequireChecksum` | `Upload__RequireChecksum` | `false` | Rejects uploads when checksum header is missing |
| `Storage:Root` | `Storage__Root` | `/data/files` | Root folder used for hosted files |
| `S3:AccessKey` | `S3__AccessKey` | _(disabled)_ | S3 access key ID — enables S3 API when set together with `S3:SecretKey` |
| `S3:SecretKey` | `S3__SecretKey` | _(disabled)_ | S3 secret access key — enables S3 API when set together with `S3:AccessKey` |

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
- Inject the upload token and S3 keys through secrets management
- Prefer checksum verification in CI
