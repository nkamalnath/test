#!/usr/bin/env dotnet run
// DealCloudBackup.cs
// .NET 10 file-based C# script — run with:  dotnet run DealCloudBackup.cs
//
// Flow:
//   1. POST to DealCloud OAuth token endpoint (client_credentials) -> access_token
//   2. GET the backup endpoint with Bearer token -> JSON containing a download URL
//   3. GET the download URL (usually a pre-signed blob link) and stream to disk
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
//   5  download URL not found in backup response
//   6  download request failed
//   7  downloaded file is empty

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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

    string accessToken;
    using (var doc = JsonDocument.Parse(tokenBody))
    {
        if (!doc.RootElement.TryGetProperty("access_token", out var atEl) || atEl.ValueKind != JsonValueKind.String)
        {
            Log("ERROR", "Token response did not contain access_token.");
            Log("ERROR", tokenBody);
            return 3;
        }
        accessToken = atEl.GetString()!;
    }
    Log("INFO", "Access token acquired.");

    // ---- Step 2: call backup endpoint to get the download link ----
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

    // DealCloud may return either a JSON object with a link field, or a plain string URL.
    string? downloadUrl = null;
    var trimmed = backupBody.TrimStart();
    if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
    {
        using var doc = JsonDocument.Parse(backupBody);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0]
            : doc.RootElement;

        foreach (var name in new[] { "downloadUrl", "downloadLink", "url", "link", "uri", "backupUrl", "location" })
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                downloadUrl = el.GetString();
                break;
            }
        }
    }
    else
    {
        // Bare string/quoted URL fallback
        downloadUrl = backupBody.Trim().Trim('"');
    }

    if (string.IsNullOrWhiteSpace(downloadUrl) ||
        !Uri.TryCreate(downloadUrl, UriKind.Absolute, out _))
    {
        Log("ERROR", "Could not extract a valid download URL from backup response:");
        Log("ERROR", backupBody);
        return 5;
    }
    Log("INFO", "Download URL received.");

    // ---- Step 3: download the backup file ----
    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var outputPath = Path.Combine(outputDir, $"dealcloud-backup-{timestamp}.zip");

    Log("INFO", $"Downloading backup -> {outputPath}");
    using var dlReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
    // Pre-signed blob URLs typically do NOT want the Bearer token. Only attach
    // it if the link points back to the DealCloud site host.
    if (new Uri(downloadUrl!).Host.Equals(new Uri(siteUrl).Host, StringComparison.OrdinalIgnoreCase))
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

    // Honor server-supplied filename if present
    var cd = dlResp.Content.Headers.ContentDisposition;
    var suggested = cd?.FileNameStar ?? cd?.FileName;
    if (!string.IsNullOrWhiteSpace(suggested))
    {
        var cleaned = suggested.Trim('"');
        outputPath = Path.Combine(outputDir, $"{timestamp}-{cleaned}");
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
