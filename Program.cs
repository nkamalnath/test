#!/usr/bin/env dotnet-script
#:sdk Microsoft.NET.Sdk
#:property TargetFramework net10.0
#:property Nullable enable
#:property ImplicitUsings enable
#:package Microsoft.Extensions.Http 9.0.0
#:package Microsoft.Extensions.DependencyInjection 9.0.0
#:package Microsoft.Extensions.Logging.Console 9.0.0

// ┌──────────────────────────────────────────────────────────────────────────────┐
// │  Rundeck Script step – fully self-contained, zero extra files needed.        │
// │  Upload this single file as the Rundeck script and set env vars in the job.  │
// │                                                                              │
// │  REQUIRED env vars (Rundeck Key Storage → job option → env var):            │
// │    DC_BASE_URL        https://your-site.dealcloud.com                        │
// │    DC_CLIENT_ID       API client id                                          │
// │    DC_CLIENT_SECRET   API client secret        (mark as Secret in Rundeck)   │
// │    DC_USERNAME        service account username                               │
// │    DC_PASSWORD        service account password (mark as Secret in Rundeck)   │
// │                                                                              │
// │  OPTIONAL env vars:                                                          │
// │    DC_TOKEN_ENDPOINT   default: /api/rest/v1/login                           │
// │    DC_BACKUP_ENDPOINT  default: /api/rest/v1/sitebackup                     │
// │    DC_OUTPUT_DIR       default: /tmp/dealcloud-backups                       │
// │    DC_TIMEOUT_MINS     default: 30                                           │
// │    DC_LOG_LEVEL        default: Information  (Debug|Information|Warning)     │
// └──────────────────────────────────────────────────────────────────────────────┘

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════════════
// ENV VAR HELPERS
// ═══════════════════════════════════════════════════════════════════════════════

// Fail fast with a clear message if a required var is missing
static string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException(
        $"Required environment variable '{name}' is not set.");

static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) ?? fallback;

// ═══════════════════════════════════════════════════════════════════════════════
// CONFIGURATION  –  no appsettings.json; everything from env vars
// ═══════════════════════════════════════════════════════════════════════════════

record DealCloudConfig(
    string BaseUrl,
    string TokenEndpoint,
    string BackupEndpoint,
    string ClientId,
    string ClientSecret,
    string Username,
    string Password
);

record DownloadConfig(
    string OutputDirectory,
    int    TimeoutMinutes,
    bool   OverwriteExisting
);

var dcCfg = new DealCloudConfig(
    BaseUrl:        Require("DC_BASE_URL").TrimEnd('/'),
    TokenEndpoint:  Env("DC_TOKEN_ENDPOINT",  "/api/rest/v1/login"),
    BackupEndpoint: Env("DC_BACKUP_ENDPOINT", "/api/rest/v1/sitebackup"),
    ClientId:       Require("DC_CLIENT_ID"),
    ClientSecret:   Require("DC_CLIENT_SECRET"),
    Username:       Require("DC_USERNAME"),
    Password:       Require("DC_PASSWORD")
);

var dlCfg = new DownloadConfig(
    OutputDirectory:  Env("DC_OUTPUT_DIR", "/tmp/dealcloud-backups"),
    TimeoutMinutes:   int.TryParse(Env("DC_TIMEOUT_MINS", "30"), out var tm) ? tm : 30,
    OverwriteExisting: false
);

var logLevel = Enum.TryParse<LogLevel>(Env("DC_LOG_LEVEL", "Information"), out var ll)
    ? ll : LogLevel.Information;

// ═══════════════════════════════════════════════════════════════════════════════
// API MODELS
// ═══════════════════════════════════════════════════════════════════════════════

record TokenResponse(
    [property: JsonPropertyName("access_token")]  string  AccessToken,
    [property: JsonPropertyName("token_type")]    string  TokenType,
    [property: JsonPropertyName("expires_in")]    int     ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken
);

// { "name": "Test_Site_DataExport_20250507_1.zip", "link": "https://..." }
record BackupEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("link")] string Link
)
{
    static readonly Regex DateInName = new(@"_(\d{8})_", RegexOptions.Compiled);

    public DateOnly? FileDate
    {
        get
        {
            var m = DateInName.Match(Name);
            return m.Success
                   && DateOnly.TryParseExact(m.Groups[1].Value, "yyyyMMdd", out var d)
                ? d : null;
        }
    }

    public override string ToString() => $"{Name} (date={FileDate:yyyy-MM-dd})";
}

// { "model": [...], "statusCode": 200, "message": "OK" }
record BackupListResponse(
    [property: JsonPropertyName("model")]      List<BackupEntry>? Model,
    [property: JsonPropertyName("statusCode")] int     StatusCode,
    [property: JsonPropertyName("message")]    string? Message
)
{
    public IReadOnlyList<BackupEntry> Entries => Model ?? [];
}

// ═══════════════════════════════════════════════════════════════════════════════
// AUTH SERVICE
// ═══════════════════════════════════════════════════════════════════════════════

sealed class AuthService(HttpClient http, DealCloudConfig cfg, ILogger<AuthService> log)
{
    string?        _token;
    DateTimeOffset _expiry = DateTimeOffset.MinValue;
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
            return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiry.AddSeconds(-60))
                return _token;

            log.LogInformation("[Auth] Requesting token from {Endpoint}", cfg.TokenEndpoint);

            var form = new Dictionary<string, string>
            {
                ["grant_type"]    = "password",
                ["client_id"]     = cfg.ClientId,
                ["client_secret"] = cfg.ClientSecret,
                ["username"]      = cfg.Username,
                ["password"]      = cfg.Password,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };
            using var res = await http.SendAsync(req, ct);

            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"[Auth] Failed ({res.StatusCode}): {body}");
            }

            var tok = await res.Content.ReadFromJsonAsync<TokenResponse>(ct)
                      ?? throw new InvalidOperationException("[Auth] Empty token response.");

            _token  = tok.AccessToken;
            _expiry = DateTimeOffset.UtcNow.AddSeconds(tok.ExpiresIn > 0 ? tok.ExpiresIn : 3600);

            log.LogInformation("[Auth] Token acquired, expires in {Sec}s", tok.ExpiresIn);
            return _token;
        }
        finally { _gate.Release(); }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// BACKUP SERVICE
// ═══════════════════════════════════════════════════════════════════════════════

sealed class BackupService(HttpClient http, AuthService auth, DealCloudConfig cfg,
                           ILogger<BackupService> log)
{
    public async Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        var token = await auth.GetTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, cfg.BackupEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        log.LogInformation("[Backup] GET {Endpoint}", cfg.BackupEndpoint);

        using var res = await http.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"[Backup] List failed ({res.StatusCode}): {body}");
        }

        var payload = await res.Content.ReadFromJsonAsync<BackupListResponse>(ct);

        log.LogInformation("[Backup] statusCode={Code} message={Msg} count={N}",
            payload?.StatusCode, payload?.Message, payload?.Entries.Count ?? 0);

        return payload?.Entries ?? [];
    }

    public async Task<BackupEntry?> GetPreviousDayBackupAsync(CancellationToken ct = default)
    {
        var entries   = await ListAsync(ct);
        var yesterday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddDays(-1).UtcDateTime);

        log.LogInformation("[Backup] Searching for date {Date:yyyy-MM-dd} in filenames", yesterday);

        foreach (var e in entries)
            log.LogDebug("[Backup]   {Entry}", e);

        // MaxBy name picks the highest sequence suffix if multiple files on same day (_1, _2…)
        var match = entries
            .Where(e => e.FileDate == yesterday && !string.IsNullOrWhiteSpace(e.Link))
            .MaxBy(e => e.Name);

        if (match is null)
        {
            var available = string.Join(", ", entries
                .Where(e => e.FileDate.HasValue)
                .Select(e => e.FileDate!.Value)
                .Distinct()
                .OrderByDescending(d => d)
                .Take(10));

            log.LogWarning("[Backup] No match for {Date:yyyy-MM-dd}. Available: {Dates}",
                yesterday, available);
        }
        else
        {
            log.LogInformation("[Backup] Match found: {Entry}", match);
        }

        return match;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// DOWNLOAD SERVICE
// ═══════════════════════════════════════════════════════════════════════════════

sealed class DownloadService(HttpClient http, AuthService auth, DownloadConfig cfg,
                             ILogger<DownloadService> log)
{
    const int BufferSize = 81_920; // 80 KB

    public async Task<string> DownloadAsync(BackupEntry backup, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cfg.OutputDirectory);

        var destPath = Path.Combine(cfg.OutputDirectory, backup.Name);
        var partPath = destPath + ".part";

        if (File.Exists(destPath) && !cfg.OverwriteExisting)
        {
            log.LogInformation("[Download] Already exists, skipping: {Path}", destPath);
            return destPath;
        }

        log.LogInformation("[Download] {Name} -> {Dir}", backup.Name, cfg.OutputDirectory);

        var token = await auth.GetTokenAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(cfg.TimeoutMinutes));

        try
        {
            await StreamToFileAsync(backup.Link, partPath, token, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (File.Exists(partPath)) File.Delete(partPath);
            throw new TimeoutException(
                $"[Download] Timed out after {cfg.TimeoutMinutes} min.");
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(partPath, destPath);

        var sizeMb = new FileInfo(destPath).Length / 1_048_576.0;
        log.LogInformation("[Download] Done: {Path} ({MB:F1} MB)", destPath, sizeMb);

        return destPath;
    }

    async Task StreamToFileAsync(string url, string dest, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"[Download] HTTP {res.StatusCode}: {body}");
        }

        var total = res.Content.Headers.ContentLength;

        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                             FileShare.None, BufferSize, useAsync: true);
        var  buf        = new byte[BufferSize];
        long downloaded = 0;
        int  lastPct    = -1;
        int  bytesRead;

        while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;

            if (total > 0)
            {
                int pct = (int)(downloaded * 100 / total.Value);
                if (pct >= lastPct + 10)
                {
                    lastPct = pct;
                    log.LogInformation("[Download] {Pct}% ({Down:F1}/{Tot:F1} MB)",
                        pct, downloaded / 1_048_576.0, total.Value / 1_048_576.0);
                }
            }
            else if (downloaded / 1_048_576 is long mb && mb > 0
                     && mb % 50 == 0 && (int)mb != lastPct)
            {
                lastPct = (int)mb;
                log.LogInformation("[Download] {MB} MB...", mb);
            }
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// BOOTSTRAP & RUN
// ═══════════════════════════════════════════════════════════════════════════════

var services = new ServiceCollection()
    .AddLogging(b => b
        // SystemdConsole: no ANSI escape codes → renders cleanly in Rundeck execution log
        .AddSystemdConsole(o => { o.IncludeScopes = false; o.UseUtcTimestamp = true; })
        .SetMinimumLevel(logLevel))
    .AddSingleton(dcCfg)
    .AddSingleton(dlCfg)
    .AddHttpClient("dc", (_, c) =>
    {
        c.BaseAddress = new Uri(dcCfg.BaseUrl);
        c.Timeout     = Timeout.InfiniteTimeSpan; // per-operation timeouts used instead
    })
    .Services
    .AddSingleton<AuthService>(sp =>
        new AuthService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("dc"),
            sp.GetRequiredService<DealCloudConfig>(),
            sp.GetRequiredService<ILogger<AuthService>>()))
    .AddSingleton<BackupService>(sp =>
        new BackupService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("dc"),
            sp.GetRequiredService<AuthService>(),
            sp.GetRequiredService<DealCloudConfig>(),
            sp.GetRequiredService<ILogger<BackupService>>()))
    .AddSingleton<DownloadService>(sp =>
        new DownloadService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("dc"),
            sp.GetRequiredService<AuthService>(),
            sp.GetRequiredService<DownloadConfig>(),
            sp.GetRequiredService<ILogger<DownloadService>>()))
    .BuildServiceProvider();

var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Main");

// Rundeck has no TTY – no CancelKeyPress handler needed
using var cts = new CancellationTokenSource();

try
{
    log.LogInformation("[Main] === DealCloud Backup Downloader ===");
    log.LogInformation("[Main] Job={Job} ExecId={Id} OutputDir={Dir} Timeout={Mins}min",
        Env("RD_JOB_NAME",   "(local)"),
        Env("RD_JOB_EXECID", "(local)"),
        dlCfg.OutputDirectory,
        dlCfg.TimeoutMinutes);

    var backupSvc   = services.GetRequiredService<BackupService>();
    var downloadSvc = services.GetRequiredService<DownloadService>();

    var entry = await backupSvc.GetPreviousDayBackupAsync(cts.Token);

    if (entry is null)
    {
        log.LogError("[Main] ERROR: No previous-day backup found.");
        return 1;
    }

    var savedPath = await downloadSvc.DownloadAsync(entry, cts.Token);

    log.LogInformation("[Main] SUCCESS: {Path}", savedPath);
    return 0;
}
catch (OperationCanceledException)
{
    log.LogWarning("[Main] CANCELLED");
    return 2;
}
catch (Exception ex)
{
    log.LogCritical("[Main] FAILED: {Message}", ex.Message);
    log.LogDebug(ex, "[Main] Stack trace");
    return 1;
}
