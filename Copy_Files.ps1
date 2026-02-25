param(
    [Parameter(Mandatory = $true)]
    [string]$SourceFolder,        # e.g. "C:\logs\{dateMask:yyyy}\{PrevDateMask:MM-dd}"

    [Parameter(Mandatory = $true)]
    [string]$DestinationFolder,   # destination directory

    [Parameter(Mandatory = $false)]
    [string]$FileNameMask = "*",        # e.g. "test*.txt" or "test_{dateMask:yyyy}_{PrevDateMask:MM-dd}_*.txt, re:test_{DateMask:yyyy}\d"

    [Parameter(Mandatory = $false)]
    [string]$DestinationFileMask = "%ORIG%",  # e.g. "%ORIG%", "%ORIG_NO_EXT%_backup.txt", "backup_{dateMask:yyyyMMdd}_%ORIG%"

    [Parameter(Mandatory = $false)]
    [switch]$UseTempSuffix = $true,       # when set, use "__TEMP" suffix while writing

    [Parameter(Mandatory = $false)]
    [switch]$DeleteSourceFiles = $false,

    [Parameter(Mandatory = $false)]
    [switch]$IgnoreUnchangedFiles = $false,   # when set, skip copy if destination exists and is unchanged

    [Parameter(Mandatory = $false)]
    [switch]$SkipExistingFiles = $false,      # when set, skip any file that already exists at destination (even if modified)

    [Parameter(Mandatory = $false)]
    [switch]$IgnoreMissingSourceFolder = $false  # when true, do not error if source folder is missing
)

function Resolve-PathMasks {
    param(
        [string]$Path
    )

    # Matches {something:format} – e.g. {dateMask:yyyy}, {PrevDateMask:MM-dd}
    $pattern = '\{([^{}]+?)\}'

    $evaluator = {
        param($match)

        $inner = $match.Groups[1].Value  # "dateMask:yyyy"
        $parts = $inner.Split(':', 2)
        if ($parts.Count -lt 2) {
            return $match.Value   # leave as-is if not name:format
        }

        $name   = $parts[0].Trim()
        $format = $parts[1].Trim()

        $baseDate = Get-Date
        if ($name -like 'Prev*') {
            $baseDate = $baseDate.AddDays(-1)
        }

        try {
            return $baseDate.ToString($format)
        }
        catch {
            # If format invalid, keep original placeholder
            return $match.Value
        }
    }

    return [regex]::Replace($Path, $pattern, $evaluator)
}

# 1) Resolve masks in source & destination folders
$resolvedSourceFolder      = Resolve-PathMasks -Path $SourceFolder
$resolvedDestinationFolder = Resolve-PathMasks -Path $DestinationFolder

if (-not (Test-Path -Path $resolvedSourceFolder -PathType Container)) {
    if ($IgnoreMissingSourceFolder) {
        Write-Warning "Source folder not found after mask resolution: $resolvedSourceFolder. Exiting without error because IgnoreMissingSourceFolder is set."
        return
    }
    else {
        Write-Error "Source folder not found after mask resolution: $resolvedSourceFolder"
        exit 1
    }
}

if (-not (Test-Path -Path $resolvedDestinationFolder -PathType Container)) {
    Write-Host "Destination folder does not exist. Creating: $resolvedDestinationFolder"
    New-Item -ItemType Directory -Path $resolvedDestinationFolder -Force | Out-Null
}

# 2) Split multiple filename masks (comma-separated)
$rawPatterns = $FileNameMask -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }

$matchedFiles = @{}

foreach ($rawPattern in $rawPatterns) {
    # Resolve date masks inside each pattern
    $pattern = Resolve-PathMasks -Path $rawPattern

    if ($pattern.StartsWith('re:', [System.StringComparison]::OrdinalIgnoreCase)) {
        # Regex mode: pattern after "re:"
        $regex = $pattern.Substring(3)

        Get-ChildItem -Path $resolvedSourceFolder -File -Recurse |
            Where-Object { $_.Name -match $regex } |
            ForEach-Object {
                $matchedFiles[$_.FullName] = $_
            }
    }
    else {
        # Wildcard mode
        Get-ChildItem -Path $resolvedSourceFolder -File -Filter $pattern -Recurse |
            ForEach-Object {
                $matchedFiles[$_.FullName] = $_
            }
    }
}

if ($matchedFiles.Count -eq 0) {
    Write-Warning "No files matched patterns '$FileNameMask' in '$resolvedSourceFolder'"
    exit 0
}

Write-Host "Found $($matchedFiles.Count) file(s) to copy."

# 3) Copy all matched files into destination folder
foreach ($file in $matchedFiles.Values) {
    $origName  = $file.Name
    $origNoExt = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)

    # Destination name template from param
    $destNameTemplate = $DestinationFileMask
    if (-not $destNameTemplate) {
        $destNameTemplate = "%ORIG%"
    }

    # Apply original-name tokens
    $destNameTemplate = $destNameTemplate.
        Replace("%ORIG%", $origName).
        Replace("%ORIG_NO_EXT%", $origNoExt)

    # Apply date / prev-date masks in destination name
    $finalDestName = Resolve-PathMasks -Path $destNameTemplate

    $destPath = Join-Path -Path $resolvedDestinationFolder -ChildPath $finalDestName

    # Temp suffix handling (same folder, same base name, with "__TEMP" suffix)
    $tempPath = if ($UseTempSuffix) { "$destPath`__TEMP" } else { $destPath }

    try {
        # Highest precedence: skip any file that already exists at destination
        if ($SkipExistingFiles -and (Test-Path -LiteralPath $destPath)) {
            Write-Host "Skipping existing file '$($file.FullName)' (destination '$destPath' already exists)."
            continue
        }

        # Next: skip files where destination exists and appears unchanged
        if ($IgnoreUnchangedFiles -and (Test-Path -LiteralPath $destPath)) {
            $srcInfo = Get-Item -LiteralPath $file.FullName
            $dstInfo = Get-Item -LiteralPath $destPath

            if ($srcInfo.Length -eq $dstInfo.Length -and
                $srcInfo.LastWriteTimeUtc -eq $dstInfo.LastWriteTimeUtc) {
                Write-Host "Skipping unchanged file '$($file.FullName)' (destination '$destPath' is unchanged)."
                continue
            }
        }

        # Clean up any previous temp file
        if ($UseTempSuffix -and (Test-Path -LiteralPath $tempPath)) {
            Remove-Item -LiteralPath $tempPath -Force
        }

        # If not using temp and destination exists, remove it so we can overwrite cleanly
        if (-not $UseTempSuffix -and (Test-Path -LiteralPath $destPath)) {
            Remove-Item -LiteralPath $destPath -Force
        }

        # Copy into temp or final path (always with -Force to be safe)
        Copy-Item -Path $file.FullName -Destination $tempPath -Force

        # If using temp, rename to final once copy succeeded
        if ($UseTempSuffix) {
            Write-Host "Moving '$tempPath' to '$destPath'"
            if (Test-Path -LiteralPath $destPath) {
                Remove-Item -LiteralPath $destPath -Force
            }
            Move-Item -LiteralPath $tempPath -Destination $destPath
        }

        Write-Host "Copied '$($file.FullName)' -> '$destPath'"

        # Optionally delete source file after successful copy
        if ($DeleteSourceFiles) {
            try {
                Remove-Item -LiteralPath $file.FullName -Force
                Write-Host "Deleted source '$($file.FullName)'"
            }
            catch {
                Write-Error "Failed to delete source '$($file.FullName)': $($_.Exception.Message)"
            }
        }
    }
    catch {
        Write-Error "Copy failed for '$($file.FullName)': $($_.Exception.Message)"
        # Best-effort cleanup of temp file
        if ($UseTempSuffix -and (Test-Path -LiteralPath $tempPath)) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
}