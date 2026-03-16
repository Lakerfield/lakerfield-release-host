using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var settings = new FileServerSettings
{
  UploadToken = builder.Configuration["Upload:Token"] ?? throw new InvalidOperationException("Upload:Token missing"),
  StorageRoot = builder.Configuration["Storage:Root"] ?? "/data/files",
  RequireChecksum = bool.TryParse(builder.Configuration["Upload:RequireChecksum"], out var requireChecksum) && requireChecksum,
  VerifyChecksum = !bool.TryParse(builder.Configuration["Upload:VerifyChecksum"], out var verifyChecksum) || verifyChecksum
};

var app = builder.Build();

Directory.CreateDirectory(settings.StorageRoot);

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
  checksumRequired = settings.RequireChecksum
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
