using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
  options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB
});

var settings = new FileServerSettings
{
  UploadToken = builder.Configuration["Upload:Token"] ?? throw new InvalidOperationException("Upload:Token missing"),
  StorageRoot = builder.Configuration["Storage:Root"] ?? "/data/files",
  RequireChecksum = bool.TryParse(builder.Configuration["Upload:RequireChecksum"], out var requireChecksum) && requireChecksum,
  VerifyChecksum = !bool.TryParse(builder.Configuration["Upload:VerifyChecksum"], out var verifyChecksum) || verifyChecksum
};

S3Settings? s3Settings = null;
var s3AccessKey = builder.Configuration["S3:AccessKey"];
var s3SecretKey = builder.Configuration["S3:SecretKey"];
if (!string.IsNullOrEmpty(s3AccessKey) && !string.IsNullOrEmpty(s3SecretKey))
  s3Settings = new S3Settings { AccessKey = s3AccessKey, SecretKey = s3SecretKey };

var app = builder.Build();

Directory.CreateDirectory(settings.StorageRoot);

// S3-compatible API middleware — intercepts PUT/HEAD requests that carry AWS Signature V4 auth.
app.Use(async (context, next) =>
{
  var authHeader = context.Request.Headers.Authorization.ToString();
  var isS3Auth = authHeader.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.OrdinalIgnoreCase);

  if (isS3Auth &&
      (context.Request.Method == HttpMethods.Get ||
       context.Request.Method == HttpMethods.Put ||
       context.Request.Method == HttpMethods.Head))
  {
    if (s3Settings is null)
    {
      await WriteS3ErrorAsync(context, 403, "AccessDenied", "S3 API is not configured on this server");
      return;
    }

    await HandleS3RequestAsync(context, s3Settings, settings, context.RequestAborted);
    return;
  }

  await next();
});

app.Use(async (context, next) =>
{
  if (context.Request.Path.StartsWithSegments("/upload"))
  {
    var authHeader = context.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
      context.Response.StatusCode = StatusCodes.Status401Unauthorized;
      await context.Response.WriteAsync("Missing bearer token");
      return;
    }

    var providedToken = authHeader["Bearer ".Length..].Trim();
    if ((!SecureEquals(providedToken, settings.UploadToken)) || SecureEquals(providedToken, "super-secret-token"))
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      await context.Response.WriteAsync("Invalid bearer token");
      return;
    }
  }

  await next();
});

app.MapPut("/upload/{*relativePath}", async (
    HttpRequest request,
    string relativePath,
    CancellationToken cancellationToken) =>
{
  relativePath = SanitizeRelativeUploadPath(relativePath);

  if (string.IsNullOrWhiteSpace(relativePath))
    return Results.BadRequest("Invalid upload path");

  var targetPath = Path.Combine(settings.StorageRoot, relativePath);
  var fullStorageRoot = Path.GetFullPath(settings.StorageRoot);
  var fullTargetPath = Path.GetFullPath(targetPath);
  if (!fullTargetPath.StartsWith(fullStorageRoot, StringComparison.Ordinal))
    return Results.BadRequest("Invalid upload path");

  var fileName = Path.GetFileName(fullTargetPath);
  if (string.IsNullOrWhiteSpace(fileName))
    return Results.BadRequest("Invalid file name");

  var result = await SaveRequestBodyToFileAsync(request, fullTargetPath, settings, cancellationToken);
  return result.ToIResult(
      successFactory: upload => Results.Ok(new UploadResult
      {
        RelativePath = relativePath,
        FileName = fileName,
        Bytes = upload.BytesWritten,
        Url = "/" + relativePath.Replace('\\', '/'),
        ChecksumVerified = upload.ChecksumVerified,
        Sha256 = upload.Sha256
      }));
});

app.MapGet("/health", () => Results.Ok(new
{
  status = "ok",
  checksumVerification = settings.VerifyChecksum,
  checksumRequired = settings.RequireChecksum,
  s3Compatible = s3Settings is not null
}));

app.UseDefaultFiles(new DefaultFilesOptions
{
  FileProvider = new PhysicalFileProvider(settings.StorageRoot)
});

app.UseStaticFiles(new StaticFileOptions
{
  FileProvider = new PhysicalFileProvider(settings.StorageRoot),
  RequestPath = "",
  ServeUnknownFileTypes = true,
  DefaultContentType = "application/octet-stream",
  OnPrepareResponse = ctx =>
  {
    ctx.Context.Response.Headers.CacheControl = "public, max-age=300";
    ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
  }
});

app.Run();

static async Task<FileSaveResult> SaveRequestBodyToFileAsync(
    HttpRequest request,
    string targetPath,
    FileServerSettings settings,
    CancellationToken cancellationToken)
{
  var targetDir = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Target directory missing");

  Directory.CreateDirectory(targetDir);

  var fileName = Path.GetFileName(targetPath);
  var tempFileName = $".{fileName}.{Guid.NewGuid():N}.uploading";
  var tempPath = Path.Combine(targetDir, tempFileName);
  string? expectedChecksum = null;
  if (settings.VerifyChecksum || settings.RequireChecksum)
  {
    expectedChecksum = request.Headers["X-Checksum-Sha256"].FirstOrDefault();
    if (settings.RequireChecksum && string.IsNullOrWhiteSpace(expectedChecksum))
      return FileSaveResult.Fail(StatusCodes.Status400BadRequest, "Missing X-Checksum-Sha256 header");

    if (!string.IsNullOrWhiteSpace(expectedChecksum) && !IsValidSha256Hex(expectedChecksum))
      return FileSaveResult.Fail(StatusCodes.Status400BadRequest, "Invalid X-Checksum-Sha256 header");
  }

  string? actualChecksum = null;
  long bytesWritten = 0;
  try
  {
    await using var fileStream = new FileStream(
        tempPath,
        new FileStreamOptions
        {
          Mode = FileMode.CreateNew,
          Access = FileAccess.Write,
          Share = FileShare.None,
          BufferSize = 64 * 1024,
          Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

    if (settings.VerifyChecksum && !string.IsNullOrWhiteSpace(expectedChecksum))
    {
      using var sha256 = SHA256.Create();
      await using var cryptoStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write, leaveOpen: true);

      await request.Body.CopyToAsync(cryptoStream, cancellationToken);
      await cryptoStream.FlushAsync(cancellationToken);
      cryptoStream.FlushFinalBlock();

      actualChecksum = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
      bytesWritten = fileStream.Length;
    }
    else
    {
      await request.Body.CopyToAsync(fileStream, cancellationToken);
      await fileStream.FlushAsync(cancellationToken);
      bytesWritten = fileStream.Length;
    }

    fileStream.Flush(flushToDisk: true);

    if (!string.IsNullOrWhiteSpace(expectedChecksum) &&
        settings.VerifyChecksum &&
        !string.Equals(actualChecksum, expectedChecksum.ToLowerInvariant(), StringComparison.Ordinal))
    {
      TryDelete(tempPath);
      return FileSaveResult.Fail(
          StatusCodes.Status400BadRequest,
          "Checksum mismatch",
          expectedChecksum.ToLowerInvariant(),
          actualChecksum);
    }

    File.Move(tempPath, targetPath, overwrite: true);

    return FileSaveResult.Successfull(bytesWritten, actualChecksum, !string.IsNullOrWhiteSpace(expectedChecksum) && settings.VerifyChecksum);
  }
  catch (OperationCanceledException)
  {
    TryDelete(tempPath);
    return FileSaveResult.Fail(StatusCodes.Status499ClientClosedRequest, "Upload cancelled");
  }
  catch (Exception ex)
  {
    TryDelete(tempPath);
    return FileSaveResult.Fail(StatusCodes.Status500InternalServerError, ex.Message);
  }
}

static string SanitizeRelativeUploadPath(string value)
{
  if (string.IsNullOrWhiteSpace(value))
    return string.Empty;

  var segments = value
      .Replace('\\', '/')
      .Split('/', StringSplitOptions.RemoveEmptyEntries)
      .Select(SanitizePathSegment)
      .Where(x => !string.IsNullOrWhiteSpace(x))
      .ToArray();

  return string.Join('/', segments);
}

static string SanitizePathSegment(string value)
{
  if (string.IsNullOrWhiteSpace(value))
    return string.Empty;

  value = value.Trim().Replace('\\', '/');
  value = Path.GetFileName(value);

  foreach (var invalidChar in Path.GetInvalidFileNameChars())
    value = value.Replace(invalidChar, '_');

  return value;
}

static bool SecureEquals(string a, string b)
{
  var aBytes = Encoding.UTF8.GetBytes(a);
  var bBytes = Encoding.UTF8.GetBytes(b);

  return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
}

static bool IsValidSha256Hex(string? value)
{
  if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
    return false;

  foreach (var c in value)
  {
    var isHex =
        (c >= '0' && c <= '9') ||
        (c >= 'a' && c <= 'f') ||
        (c >= 'A' && c <= 'F');

    if (!isHex)
      return false;
  }

  return true;
}

static void TryDelete(string path)
{
  try
  {
    if (File.Exists(path))
      File.Delete(path);
  }
  catch
  {
    // intentionally ignored
  }
}

// ── S3-compatible API ─────────────────────────────────────────────────────────

static async Task HandleS3RequestAsync(
    HttpContext context,
    S3Settings s3Settings,
    FileServerSettings settings,
    CancellationToken cancellationToken)
{
  var authHeader = context.Request.Headers.Authorization.ToString();
  var (accessKeyId, date, region, service, signedHeaderNames, signature) =
      ParseAwsAuthHeader(authHeader);

  if (!SecureEquals(accessKeyId, s3Settings.AccessKey))
  {
    await WriteS3ErrorAsync(context, 403, "InvalidAccessKeyId",
        "The AWS Access Key Id provided does not exist in our records");
    return;
  }

  var amzDate = context.Request.Headers["x-amz-date"].FirstOrDefault();
  if (!TryParseAmzDate(amzDate, out var requestDate) ||
      Math.Abs((DateTime.UtcNow - requestDate).TotalMinutes) > 15)
  {
    await WriteS3ErrorAsync(context, 403, "RequestExpired",
        "The difference between the request time and the current time is too large");
    return;
  }

  var expectedSig = ComputeAwsV4Signature(
      context.Request, s3Settings.SecretKey, date, region, service, signedHeaderNames);

  if (!SecureEquals(signature, expectedSig))
  {
    await WriteS3ErrorAsync(context, 403, "SignatureDoesNotMatch",
        "The request signature we calculated does not match the signature you provided");
    return;
  }

  var path = context.Request.Path.Value ?? "/";

  if (context.Request.Method == HttpMethods.Get)
    await HandleS3GetObjectAsync(context, path, settings, cancellationToken);
  else if (context.Request.Method == HttpMethods.Put)
    await HandleS3PutObjectAsync(context, path, settings, cancellationToken);
  else
    await HandleS3HeadObjectAsync(context, path, settings);
}

static async Task HandleS3PutObjectAsync(
    HttpContext context,
    string path,
    FileServerSettings settings,
    CancellationToken cancellationToken)
{
  var relativePath = SanitizeRelativeUploadPath(path.TrimStart('/'));
  if (string.IsNullOrWhiteSpace(relativePath))
  {
    await WriteS3ErrorAsync(context, 400, "InvalidArgument", "Invalid object key");
    return;
  }

  var targetPath = Path.Combine(settings.StorageRoot, relativePath);
  var fullStorageRoot = Path.GetFullPath(settings.StorageRoot);
  var fullTargetPath = Path.GetFullPath(targetPath);
  if (!fullTargetPath.StartsWith(fullStorageRoot, StringComparison.Ordinal))
  {
    await WriteS3ErrorAsync(context, 400, "InvalidArgument", "Invalid object key");
    return;
  }

  var fileName = Path.GetFileName(fullTargetPath);
  if (string.IsNullOrWhiteSpace(fileName))
  {
    await WriteS3ErrorAsync(context, 400, "InvalidArgument", "Invalid object key: missing file name");
    return;
  }

  // Map x-amz-content-sha256 to X-Checksum-Sha256 when it carries a real hash.
  var xAmzSha256 = context.Request.Headers["x-amz-content-sha256"].FirstOrDefault();
  var hasPayloadHash = IsValidSha256Hex(xAmzSha256);
  if (hasPayloadHash)
    context.Request.Headers["X-Checksum-Sha256"] = xAmzSha256;

  // When the client uses streaming signatures the body uses AWS chunked encoding.
  // Wrap the request body so that only the actual payload bytes are written to disk.
  var isAwsChunked = xAmzSha256 is not null &&
      xAmzSha256.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase);
  if (isAwsChunked)
    context.Request.Body = new AwsChunkedDecodingStream(context.Request.Body);

  var s3ChecksumSettings = new FileServerSettings
  {
    UploadToken = settings.UploadToken,
    StorageRoot = settings.StorageRoot,
    VerifyChecksum = hasPayloadHash && settings.VerifyChecksum,
    RequireChecksum = false
  };

  var result = await SaveRequestBodyToFileAsync(
      context.Request, fullTargetPath, s3ChecksumSettings, cancellationToken);

  if (!result.Success)
  {
    if (result.StatusCode == StatusCodes.Status400BadRequest &&
        string.Equals(result.Message, "Checksum mismatch", StringComparison.Ordinal))
    {
      await WriteS3ErrorAsync(context, 400, "InvalidDigest",
          "The content SHA256 you specified did not match what the server received");
    }
    else if (result.StatusCode == StatusCodes.Status499ClientClosedRequest)
    {
      context.Response.StatusCode = 499;
    }
    else
    {
      await WriteS3ErrorAsync(context, result.StatusCode, "InternalError", result.Message);
    }
    return;
  }

  var etag = result.Sha256 is not null
      ? $"\"{result.Sha256}\""
      : $"\"{Guid.NewGuid():N}\"";

  context.Response.StatusCode = 200;
  context.Response.Headers.ETag = etag;
  context.Response.ContentLength = 0;
  await context.Response.CompleteAsync();
}

static async Task HandleS3GetObjectAsync(
    HttpContext context,
    string path,
    FileServerSettings settings,
    CancellationToken cancellationToken)
{
  var relativePath = SanitizeRelativeUploadPath(path.TrimStart('/'));
  if (string.IsNullOrWhiteSpace(relativePath))
  {
    await WriteS3ErrorAsync(context, 400, "InvalidArgument", "Invalid object key");
    return;
  }

  var targetPath = Path.Combine(settings.StorageRoot, relativePath);
  var fullStorageRoot = Path.GetFullPath(settings.StorageRoot);
  var fullTargetPath = Path.GetFullPath(targetPath);
  if (!fullTargetPath.StartsWith(fullStorageRoot, StringComparison.Ordinal))
  {
    await WriteS3ErrorAsync(context, 404, "NoSuchKey", "The specified key does not exist");
    return;
  }

  var fileInfo = new FileInfo(fullTargetPath);
  if (!fileInfo.Exists)
  {
    await WriteS3ErrorAsync(context, 404, "NoSuchKey", "The specified key does not exist");
    return;
  }

  var etagSource = $"{fileInfo.Length}-{fileInfo.LastWriteTimeUtc.Ticks}";
  var etagHash = Convert.ToHexString(SHA256.HashData(
      Encoding.UTF8.GetBytes(etagSource))).ToLowerInvariant()[..16];

  context.Response.StatusCode = 200;
  context.Response.Headers.ETag = $"\"{etagHash}\"";
  context.Response.ContentLength = fileInfo.Length;
  context.Response.ContentType = "application/octet-stream";
  context.Response.Headers["Last-Modified"] = fileInfo.LastWriteTimeUtc.ToString("R");

  try
  {
    await using var fs = new FileStream(fullTargetPath, FileMode.Open, FileAccess.Read, FileShare.Read,
        bufferSize: 64 * 1024, useAsync: true);
    await fs.CopyToAsync(context.Response.Body, cancellationToken);
  }
  catch (OperationCanceledException)
  {
    // Client disconnected – nothing to do.
  }
  catch (Exception)
  {
    // File may have been deleted or locked after the existence check; the response
    // headers are already sent so we can only abort the connection at this point.
    context.Abort();
  }
}

static async Task HandleS3HeadObjectAsync(
    HttpContext context,
    string path,
    FileServerSettings settings)
{
  var relativePath = SanitizeRelativeUploadPath(path.TrimStart('/'));
  if (string.IsNullOrWhiteSpace(relativePath))
  {
    context.Response.StatusCode = 400;
    return;
  }

  var targetPath = Path.Combine(settings.StorageRoot, relativePath);
  var fullStorageRoot = Path.GetFullPath(settings.StorageRoot);
  var fullTargetPath = Path.GetFullPath(targetPath);
  if (!fullTargetPath.StartsWith(fullStorageRoot, StringComparison.Ordinal))
  {
    context.Response.StatusCode = 404;
    return;
  }

  var fileInfo = new FileInfo(fullTargetPath);
  if (!fileInfo.Exists)
  {
    context.Response.StatusCode = 404;
    return;
  }

  var etagSource = $"{fileInfo.Length}-{fileInfo.LastWriteTimeUtc.Ticks}";
  var etagHash = Convert.ToHexString(SHA256.HashData(
      Encoding.UTF8.GetBytes(etagSource))).ToLowerInvariant()[..16];

  context.Response.StatusCode = 200;
  context.Response.Headers.ETag = $"\"{etagHash}\"";
  context.Response.ContentLength = fileInfo.Length;
  context.Response.ContentType = "application/octet-stream";
  context.Response.Headers["Last-Modified"] = fileInfo.LastWriteTimeUtc.ToString("R");
  await context.Response.CompleteAsync();
}

// ── AWS Signature V4 helpers ──────────────────────────────────────────────────

static (string AccessKeyId, string Date, string Region, string Service,
        string[] SignedHeaders, string Signature)
    ParseAwsAuthHeader(string authHeader)
{
  // AWS4-HMAC-SHA256 Credential=KEY/DATE/REGION/SERVICE/aws4_request,
  //                  SignedHeaders=header1;header2,
  //                  Signature=hexsig
  var body = authHeader["AWS4-HMAC-SHA256 ".Length..];
  var parts = body.Split(',', StringSplitOptions.TrimEntries);

  string accessKeyId = "", date = "", region = "", service = "",
         signedHeaders = "", signature = "";

  foreach (var part in parts)
  {
    if (part.StartsWith("Credential=", StringComparison.Ordinal))
    {
      var cred = part["Credential=".Length..].Split('/');
      if (cred.Length >= 4)
      {
        accessKeyId = cred[0];
        date = cred[1];
        region = cred[2];
        service = cred[3];
      }
    }
    else if (part.StartsWith("SignedHeaders=", StringComparison.Ordinal))
      signedHeaders = part["SignedHeaders=".Length..];
    else if (part.StartsWith("Signature=", StringComparison.Ordinal))
      signature = part["Signature=".Length..];
  }

  return (accessKeyId, date, region, service,
          signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries),
          signature);
}

static string ComputeAwsV4Signature(
    HttpRequest request,
    string secretKey,
    string date,
    string region,
    string service,
    string[] signedHeaderNames)
{
  var method = request.Method.ToUpperInvariant();
  var canonicalUri = CanonicalizeS3Uri(request.Path.Value ?? "/");
  var canonicalQuery = CanonicalizeAwsQueryString(request.Query);
  var sortedHeaders = signedHeaderNames
      .Select(h => h.ToLowerInvariant())
      .OrderBy(h => h, StringComparer.Ordinal)
      .ToArray();
  var canonicalHeaders = BuildAwsCanonicalHeaders(request, sortedHeaders);
  var signedHeadersStr = string.Join(";", sortedHeaders);
  var payloadHash = request.Headers["x-amz-content-sha256"].FirstOrDefault()
                    ?? "UNSIGNED-PAYLOAD";

  // Step 1 – canonical request
  // CanonicalHeaders already ends with '\n'; the spec requires one more '\n'
  // (blank line) before the SignedHeaders line.
  var canonicalRequest =
      $"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeadersStr}\n{payloadHash}";

  // Step 2 – string to sign
  var dateTime = request.Headers["x-amz-date"].FirstOrDefault() ?? "";
  var credScope = $"{date}/{region}/{service}/aws4_request";
  var crHash = Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));
  var stringToSign = $"AWS4-HMAC-SHA256\n{dateTime}\n{credScope}\n{crHash}";

  // Step 3 – signing key
  var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
  var kRegion = HmacSha256(kDate, region);
  var kService = HmacSha256(kRegion, service);
  var kSigning = HmacSha256(kService, "aws4_request");

  // Step 4 – signature
  return Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLowerInvariant();
}

static string CanonicalizeS3Uri(string path)
{
  if (string.IsNullOrEmpty(path)) return "/";
  // Encode each segment individually; preserve '/' separators.
  var segments = path.Split('/');
  return string.Join("/", segments.Select(s => AwsUriEncode(s)));
}

static string CanonicalizeAwsQueryString(IQueryCollection query)
{
  if (!query.Any()) return "";
  return string.Join("&",
      query
          .SelectMany(p => p.Value.Select(v =>
              (Key: AwsUriEncode(p.Key), Val: AwsUriEncode(v ?? ""))))
          .OrderBy(p => p.Key, StringComparer.Ordinal)
          .Select(p => $"{p.Key}={p.Val}"));
}

static string BuildAwsCanonicalHeaders(HttpRequest request, IEnumerable<string> sortedLowerNames)
{
  var sb = new StringBuilder();
  foreach (var name in sortedLowerNames)
  {
    var value = name == "host"
        ? request.Host.Value ?? ""
        : request.Headers[name].ToString();

    // Trim and collapse runs of whitespace to a single space.
    value = string.Join(" ",
        value.Trim().Split(new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries));

    sb.Append(name).Append(':').Append(value).Append('\n');
  }
  return sb.ToString();
}

static string AwsUriEncode(string value)
{
  // Percent-encode every byte that is not an RFC 3986 unreserved character.
  var sb = new StringBuilder();
  foreach (var b in Encoding.UTF8.GetBytes(value))
  {
    if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') ||
        b == '-' || b == '_' || b == '.' || b == '~')
      sb.Append((char)b);
    else
      sb.Append($"%{b:X2}");
  }
  return sb.ToString();
}

static bool TryParseAmzDate(string? amzDate, out DateTime result)
{
  result = default;
  if (string.IsNullOrEmpty(amzDate)) return false;
  return DateTime.TryParseExact(
      amzDate, "yyyyMMdd'T'HHmmss'Z'",
      CultureInfo.InvariantCulture,
      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
      out result);
}

static string Sha256Hex(byte[] data) =>
    Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

static byte[] HmacSha256(byte[] key, string data) =>
    HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

static Task WriteS3ErrorAsync(HttpContext context, int statusCode, string code, string message)
{
  context.Response.StatusCode = statusCode;
  context.Response.ContentType = "application/xml";
  return context.Response.WriteAsync(
      "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
      $"<Error><Code>{XmlEncode(code)}</Code>" +
      $"<Message>{XmlEncode(message)}</Message></Error>");
}

static string XmlEncode(string value) =>
    value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

public sealed class FileServerSettings
{
  public required string UploadToken { get; init; }
  public required string StorageRoot { get; init; }
  public bool VerifyChecksum { get; init; }
  public bool RequireChecksum { get; init; }
}

public sealed class UploadResult
{
  public required string RelativePath { get; init; }
  public required string FileName { get; init; }
  public required long Bytes { get; init; }
  public required string Url { get; init; }
  public required bool ChecksumVerified { get; init; }
  public string? Sha256 { get; init; }
}

public sealed class FileSaveResult
{
  public bool Success { get; private init; }
  public int StatusCode { get; private init; }
  public string Message { get; private init; } = string.Empty;
  public string? ExpectedChecksum { get; private init; }
  public string? ActualChecksum { get; private init; }
  public long BytesWritten { get; private init; }
  public string? Sha256 { get; private init; }
  public bool ChecksumVerified { get; private init; }

  public static FileSaveResult Successfull(long bytesWritten, string? sha256, bool checksumVerified) => new()
  {
    Success = true,
    BytesWritten = bytesWritten,
    Sha256 = sha256,
    ChecksumVerified = checksumVerified,
    StatusCode = StatusCodes.Status200OK
  };

  public static FileSaveResult Fail(int statusCode, string message, string? expectedChecksum = null, string? actualChecksum = null) => new()
  {
    Success = false,
    StatusCode = statusCode,
    Message = message,
    ExpectedChecksum = expectedChecksum,
    ActualChecksum = actualChecksum
  };

  public IResult ToIResult(Func<FileSaveResult, IResult> successFactory)
  {
    if (Success)
      return successFactory(this);

    if (StatusCode == StatusCodes.Status400BadRequest &&
        string.Equals(Message, "Checksum mismatch", StringComparison.Ordinal))
    {
      return Results.BadRequest(new
      {
        error = Message,
        expected = ExpectedChecksum,
        actual = ActualChecksum
      });
    }

    if (StatusCode == StatusCodes.Status400BadRequest)
      return Results.BadRequest(Message);

    if (StatusCode == StatusCodes.Status499ClientClosedRequest)
      return Results.StatusCode(StatusCode);

    return Results.Problem(
        title: "Upload failed",
        detail: Message,
        statusCode: StatusCode);
  }
}

public sealed class S3Settings
{
  public required string AccessKey { get; init; }
  public required string SecretKey { get; init; }
}

// Decodes the AWS chunked transfer encoding used when x-amz-content-sha256 is
// STREAMING-AWS4-HMAC-SHA256-PAYLOAD (or similar STREAMING-* values).
//
// Each chunk in the wire format looks like:
//   {hex-size}[;chunk-extension]\r\n
//   {data-bytes}\r\n
// The stream ends with a zero-length chunk:
//   0[;chunk-extension]\r\n
//   [optional trailers]
internal sealed class AwsChunkedDecodingStream : Stream
{
  private readonly Stream _inner;
  private readonly byte[] _readBuf = new byte[4096];
  private int _readBufPos;
  private int _readBufLen;
  private long _chunkBytesRemaining;
  private bool _needChunkHeader = true;
  private bool _finished;

  public AwsChunkedDecodingStream(Stream inner) => _inner = inner;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException();
  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override int Read(byte[] buffer, int offset, int count) =>
      ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    if (_finished || buffer.IsEmpty) return 0;

    while (true)
    {
      if (_needChunkHeader)
      {
        var line = await ReadLineAsync(cancellationToken);
        if (line is null) { _finished = true; return 0; }

        // Strip optional chunk extensions: "{hex-size};chunk-signature=..."
        var semiIdx = line.IndexOf(';');
        var hexPart = semiIdx >= 0 ? line.AsSpan(0, semiIdx) : line.AsSpan();

        if (!long.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize)
            || chunkSize < 0)
          throw new IOException($"Invalid chunk size in AWS chunked encoding: '{hexPart.ToString()}'");

        if (chunkSize == 0)
        {
          _finished = true;
          return 0;
        }

        _chunkBytesRemaining = chunkSize;
        _needChunkHeader = false;
      }

      if (_chunkBytesRemaining > 0)
      {
        var toRead = (int)Math.Min(buffer.Length, _chunkBytesRemaining);
        var read = await ReadFromBufferAsync(buffer[..toRead], cancellationToken);
        if (read == 0)
          throw new IOException("Unexpected end of stream inside an AWS chunked encoding chunk");

        _chunkBytesRemaining -= read;

        if (_chunkBytesRemaining == 0)
        {
          // Consume the trailing \r\n that follows each chunk's data.
          await ReadLineAsync(cancellationToken);
          _needChunkHeader = true;
        }

        return read;
      }
    }
  }

  // Reads up to dst.Length bytes from the internal read buffer, refilling from
  // the inner stream when needed. Returns 0 only on EOF.
  private async ValueTask<int> ReadFromBufferAsync(Memory<byte> dst, CancellationToken ct)
  {
    if (_readBufPos >= _readBufLen)
    {
      _readBufLen = await _inner.ReadAsync(_readBuf, ct);
      _readBufPos = 0;
      if (_readBufLen == 0) return 0;
    }

    var available = Math.Min(dst.Length, _readBufLen - _readBufPos);
    _readBuf.AsMemory(_readBufPos, available).CopyTo(dst);
    _readBufPos += available;
    return available;
  }

  // Reads one byte from the internal buffer, refilling from the inner stream
  // when needed. Returns -1 on EOF.
  private async ValueTask<int> ReadByteFromBufferAsync(CancellationToken ct)
  {
    if (_readBufPos >= _readBufLen)
    {
      _readBufLen = await _inner.ReadAsync(_readBuf, ct);
      _readBufPos = 0;
      if (_readBufLen == 0) return -1;
    }

    return _readBuf[_readBufPos++];
  }

  // Reads bytes from the internal buffer until '\n', returning the line without
  // the line ending. Returns null when the stream ends before any bytes are read.
  private async Task<string?> ReadLineAsync(CancellationToken ct)
  {
    var bytes = new List<byte>(64);

    while (true)
    {
      var b = await ReadByteFromBufferAsync(ct);
      if (b == -1)
        return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');

      if (b == '\n')
        return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');

      bytes.Add((byte)b);
    }
  }

  protected override void Dispose(bool disposing)
  {
    // Intentionally do not dispose the inner stream – it is owned by the request pipeline.
    base.Dispose(disposing);
  }
}
