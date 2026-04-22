#!/usr/bin/env dotnet run
// DealCloudBackup.cs
// .NET 10 file-based C# script — run with:  dotnet run DealCloudBackup.cs
//
// Flow:
//   1. POST to DealCloud OAuth token endpoint (client_credentials) -> access_token
//   2. GET the backup endpoint with Bearer token -> JSON listing backups
//      Pick the entry whose filename contains the target date (default: yesterday UTC)
//   3. GET the pre-signed download URL and stream the file to disk
//
// Designed to run as a Rundeck job step. All configuration comes from
// environment variables so Rundeck Key Storage values can be injected via
// the job's "Secure Options" -> env var mapping.
//
// Exit codes (useful for Rundeck failure conditions):
//   0  success
//   1  unhandled exception
//   2  missing required env var
//   3  token request failed
//   4  backup metadata request failed
//   5  no backup matched the target date (previous day by default)
//   6  download request failed
//   7  downloaded file is empty

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

static string RequireEnv(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(v))
    {
        Console.Error.WriteLine($"ERROR: Missing required environment variable: {name}");
        Environment.Exit(2);
    }
    return v!;
}

static string EnvOr(string name, string fallback)
    => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

static void Log(string level, string msg)
    => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] {msg}");

var jsonOpts = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true, // tolerate Name/name, Link/link, etc.
};

try
{
    // ---- Config from environment (Rundeck injects these from Key Storage) ----
    var siteUrl      = RequireEnv("DEALCLOUD_SITE_URL").TrimEnd('/'); // e.g. https://yourtenant.dealcloud.com
    var clientId     = RequireEnv("DEALCLOUD_CLIENT_ID");
    var clientSecret = RequireEnv("DEALCLOUD_CLIENT_SECRET");
    var tokenPath    = EnvOr("DEALCLOUD_TOKEN_PATH",  "/api/rest/v4/oauth/token");
    var backupPath   = EnvOr("DEALCLOUD_BACKUP_PATH", "/api/rest/v4/backup");
    var outputDir    = EnvOr("DEALCLOUD_OUTPUT_DIR",  "./backups");
    var scope        = Environment.GetEnvironmentVariable("DEALCLOUD_SCOPE"); // optional

    Directory.CreateDirectory(outputDir);

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("DealCloudBackup/1.0 (+rundeck)");

    // ---- Step 1: OAuth2 client_credentials -> access_token ----
    Log("INFO", $"Requesting OAuth token from {siteUrl}{tokenPath} ...");
    var form = new Dictionary<string, string>
    {
        ["grant_type"]    = "client_credentials",
        ["client_id"]     = clientId,
        ["client_secret"] = clientSecret,
    };
    if (!string.IsNullOrWhiteSpace(scope)) form["scope"] = scope!;

    using var tokenReq = new HttpRequestMessage(HttpMethod.Post, siteUrl + tokenPath)
    {
        Content = new FormUrlEncodedContent(form)
    };
    using var tokenResp = await http.SendAsync(tokenReq);
    var tokenBody = await tokenResp.Content.ReadAsStringAsync();
    if (!tokenResp.IsSuccessStatusCode)
    {
        Log("ERROR", $"Token request failed: {(int)tokenResp.StatusCode} {tokenResp.ReasonPhrase}");
        Log("ERROR", tokenBody);
        return 3;
    }

    var tokenPayload = JsonSerializer.Deserialize<TokenResponse>(tokenBody, jsonOpts);
    if (tokenPayload is null || string.IsNullOrWhiteSpace(tokenPayload.AccessToken))
    {
        Log("ERROR", "Token response did not contain access_token.");
        Log("ERROR", tokenBody);
        return 3;
    }
    var accessToken = tokenPayload.AccessToken!;
    Log("INFO", "Access token acquired.");

    // ---- Step 2: call backup endpoint and pick the entry for the target date ----
    Log("INFO", $"Calling backup endpoint {siteUrl}{backupPath} ...");
    using var backupReq = new HttpRequestMessage(HttpMethod.Get, siteUrl + backupPath);
    backupReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    backupReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var backupResp = await http.SendAsync(backupReq);
    var backupBody = await backupResp.Content.ReadAsStringAsync();
    if (!backupResp.IsSuccessStatusCode)
    {
        Log("ERROR", $"Backup request failed: {(int)backupResp.StatusCode} {backupResp.ReasonPhrase}");
        Log("ERROR", backupBody);
        return 4;
    }

    // Target date defaults to yesterday (UTC); override with DEALCLOUD_BACKUP_DATE=YYYYMMDD.
    var targetDate = EnvOr("DEALCLOUD_BACKUP_DATE",
                           DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd"));
    if (!Regex.IsMatch(targetDate, @"^\d{8}$"))
    {
        Log("ERROR", $"DEALCLOUD_BACKUP_DATE must be YYYYMMDD, got: {targetDate}");
        return 5;
    }
    Log("INFO", $"Looking for backup dated {targetDate} ...");

    BackupResponse? parsed;
    try
    {
        parsed = JsonSerializer.Deserialize<BackupResponse>(backupBody, jsonOpts);
    }
    catch (JsonException jx)
    {
        Log("ERROR", $"Backup response was not valid JSON: {jx.Message}");
        Log("ERROR", backupBody);
        return 4;
    }

    if (parsed?.Model is null || parsed.Model.Count == 0)
    {
        Log("ERROR", "Backup response had no 'model' array or it was empty.");
        Log("ERROR", backupBody);
        return 5;
    }

    // Match the 8-digit date inside filenames like Test_Site_DataExport_20250507_1.zip
    var dateInName = new Regex(@"_(\d{8})_", RegexOptions.Compiled);

    var match = parsed.Model.FirstOrDefault(e =>
        !string.IsNullOrEmpty(e.Name) &&
        dateInName.Match(e.Name) is { Success: true } m &&
        m.Groups[1].Value == targetDate);

    if (match is null ||
        string.IsNullOrWhiteSpace(match.Name) ||
        string.IsNullOrWhiteSpace(match.Link))
    {
        Log("ERROR", $"No backup file found for date {targetDate}.");
        Log("ERROR", "Available files:");
        foreach (var n in parsed.Model.Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)))
            Log("ERROR", "  - " + n);
        return 5;
    }

    if (!Uri.TryCreate(match.Link, UriKind.Absolute, out var downloadUri))
    {
        Log("ERROR", $"Matched entry '{match.Name}' but its link is not a valid absolute URL.");
        return 5;
    }

    var fileName    = match.Name!;
    var downloadUrl = match.Link!;
    Log("INFO", $"Matched backup: {fileName}");

    // ---- Step 3: download the backup file, preserving its original name ----
    var safeName   = Path.GetFileName(fileName); // guard against path traversal
    var outputPath = Path.Combine(outputDir, safeName);

    Log("INFO", $"Downloading backup -> {outputPath}");
    using var dlReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
    // Pre-signed blob URLs typically do NOT want the Bearer token. Only attach
    // it if the link points back to the DealCloud site host.
    if (downloadUri.Host.Equals(new Uri(siteUrl).Host, StringComparison.OrdinalIgnoreCase))
    {
        dlReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    using var dlResp = await http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead);
    if (!dlResp.IsSuccessStatusCode)
    {
        Log("ERROR", $"Download failed: {(int)dlResp.StatusCode} {dlResp.ReasonPhrase}");
        var errBody = await dlResp.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(errBody)) Log("ERROR", errBody);
        return 6;
    }

    await using (var inStream  = await dlResp.Content.ReadAsStreamAsync())
    await using (var outStream = File.Create(outputPath))
    {
        await inStream.CopyToAsync(outStream);
    }

    var fi = new FileInfo(outputPath);
    Log("INFO", $"Download complete. File: {outputPath}  Size: {fi.Length:N0} bytes");

    if (fi.Length == 0)
    {
        Log("ERROR", "Downloaded file is empty.");
        return 7;
    }

    // Emit the final path on its own line so Rundeck log filters can capture it
    Console.WriteLine($"BACKUP_FILE={outputPath}");
    return 0;
}
catch (Exception ex)
{
    Log("ERROR", $"Unhandled exception: {ex}");
    return 1;
}

// ---------- DTOs ----------
// Records live after top-level statements in a file-based C# program.

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")]   string? TokenType,
    [property: JsonPropertyName("expires_in")]   int?    ExpiresIn,
    [property: JsonPropertyName("scope")]        string? Scope
);

public sealed record BackupEntry(
    string? Name,
    string? Link
);

public sealed record BackupResponse(
    List<BackupEntry>? Model,
    int?               StatusCode,
    string?            Message
);
