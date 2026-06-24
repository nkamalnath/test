public abstract class DownloadOptionsBase
{
    [Required]
    [ValidateObjectMembers]
    public ScheduleOptions Schedule { get; init; } = new();

    public bool DownloadToArchive { get; init; }

    [Range(1, 365, ErrorMessage = "LookbackDays must be between 1 and 365.")]
    public int LookbackDays { get; init; }
}
public class DownloadErrorReportsOptions : DownloadOptionsBase
{
    public const string SectionName = "DownloadErrorReportsOptions";
}

public class DownloadAlertsOptions : DownloadOptionsBase
{
    public const string SectionName = "DownloadAlertsOptions";
}

public class DownloadEmployeeOptions : DownloadOptionsBase
{
    public const string SectionName = "DownloadEmployeeOptions";
}

public class ScheduleOptions
{
    [Required(AllowEmptyStrings = false)]
    public string CronExpression { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string TimeZone { get; init; } = string.Empty;

    public bool Enabled { get; init; }
}

public class DownloadEmployeeOptions : DownloadOptionsBase
{
    public const string SectionName = "DownloadEmployeeOptions";

    [Range(1, 90, ErrorMessage = "Employee lookback cannot exceed 90 days.")]
    public new int LookbackDays { get; init; }
}

 public static IServiceCollection AddDownloadOptions<T>(
        this IServiceCollection services,
        string sectionName)
        where T : DownloadOptionsBase
    {
        services
            .AddOptions<T>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

builder.Services.AddDownloadOptions<DownloadErrorReportsOptions>(DownloadErrorReportsOptions.SectionName);
builder.Services.AddDownloadOptions<DownloadAlertsOptions>(DownloadAlertsOptions.SectionName);
builder.Services.AddDownloadOptions<DownloadEmployeeOptions>(DownloadEmployeeOptions.SectionName);
