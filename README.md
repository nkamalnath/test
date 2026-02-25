## Copy_Files.ps1 – Usage

### Overview

`Copy_Files.ps1` copies files from a source folder to a destination folder with:

- Date/prev‑date masks in folder paths and file names.
- Wildcard and regex filename matching.
- Destination file renaming (`%ORIG%`, `%ORIG_NO_EXT%`).
- Optional temp suffix while writing.
- Options to skip unchanged or already‑downloaded files.
- Optional deletion of source files after copy.

### Parameters

- **`SourceFolder`** (mandatory)  
  Root folder to search for source files.  
  Supports `{name:format}` date masks, e.g.:
  - `C:\logs\{dateMask:yyyy}\{PrevDateMask:MM-dd}`

- **`DestinationFolder`** (mandatory)  
  Folder where files are copied to; created if it does not exist.  
  Also supports date masks in the path.

- **`FileNameMask`** (optional, default `*`)  
  Controls which files are selected under `SourceFolder`.  
  - Wildcards: `test*.txt`, `*_full.log`  
  - Multiple masks: `test*.txt, re:^error_.*\.log$`  
  - Regex: prefix with `re:`  
  - Can include date masks: `test_{dateMask:yyyy}_{PrevDateMask:MM-dd}_*.txt`

- **`DestinationFileMask`** (optional, default `%ORIG%`)  
  Pattern for the destination **file name** (not path).  
  - `%ORIG%` – original file name with extension.  
  - `%ORIG_NO_EXT%` – original file name without extension.  
  - Supports date masks (`{dateMask:yyyyMMdd}`, `{PrevDateMask:MM-dd}`, etc.).  
  Examples:  
  - `%ORIG%` → keep original name.  
  - `%ORIG_NO_EXT%_backup.txt` → `app.log` → `app_backup.txt`.  
  - `backup_{dateMask:yyyyMMdd}_%ORIG%`.

- **`UseTempSuffix`** (switch, default `$true`)  
  When set, files are written as `finalName__TEMP` then renamed to `finalName` after successful copy.  
  When not set, files are copied directly to the final name (overwriting if needed).

- **`DeleteSourceFiles`** (switch, default `$false`)  
  When set, deletes each source file after it is successfully copied (and temp renamed if enabled).

- **`IgnoreUnchangedFiles`** (switch, default `$false`)  
  When set and a destination file already exists, the script compares:
  - `Length` and `LastWriteTimeUtc` of source vs destination.  
  If both match, it treats the file as unchanged and skips copying.

- **`SkipExistingFiles`** (switch, default `$false`)  
  When set, if a destination file with the same resolved name already exists, the file is always skipped, even if modified.  
  This takes precedence over `IgnoreUnchangedFiles`.

- **`IgnoreMissingSourceFolder`** (switch, default `$false`)  
  Behavior when the (resolved) `SourceFolder` does not exist:  
  - When **false**: script logs an error and exits with code 1.  
  - When **true**: script logs a warning and exits cleanly without error.

### Examples

**1. Basic copy, overwrite changed files, keep originals**

```powershell
.\Copy_Files.ps1 `
  -SourceFolder "C:\logs" `
  -DestinationFolder "D:\backup"
```

**2. Daily logs, only new/changed files**

```powershell
.\Copy_Files.ps1 `
  -SourceFolder "C:\logs\{dateMask:yyyy}\{PrevDateMask:MM-dd}" `
  -DestinationFolder "D:\backup" `
  -FileNameMask "app_{dateMask:yyyyMMdd}_*.log" `
  -IgnoreUnchangedFiles
```

**3. First-time copy, never touch existing destination files**

```powershell
.\Copy_Files.ps1 `
  -SourceFolder "C:\incoming" `
  -DestinationFolder "D:\archive" `
  -FileNameMask "re:^report_.*\.csv$" `
  -SkipExistingFiles
```

**4. Move files (delete from source) with date-stamped destination names**

```powershell
.\Copy_Files.ps1 `
  -SourceFolder "C:\jobs\in" `
  -DestinationFolder "C:\jobs\processed" `
  -FileNameMask "job_*.json" `
  -DestinationFileMask "{dateMask:yyyyMMdd}_%ORIG%" `
  -UseTempSuffix `
  -DeleteSourceFiles
```

**5. Scheduled run, don’t fail if today’s source folder is missing**

```powershell
.\Copy_Files.ps1 `
  -SourceFolder "C:\logs\{dateMask:yyyy}\{PrevDateMask:MM-dd}" `
  -DestinationFolder "D:\backup" `
  -IgnoreMissingSourceFolder
```
