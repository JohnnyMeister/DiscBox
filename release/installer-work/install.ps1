$ErrorActionPreference = "Stop"

$appName = "DiscBox"
$publisher = "Johnny Meister"
$version = "0.1.0"
$defaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\DiscBox"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\DiscBox"
$payload = Join-Path $PSScriptRoot "DiscBox_payload.zip"
$installerIcon = Join-Path $PSScriptRoot "DiscBox.ico"

if (-not (Test-Path -LiteralPath $payload)) {
    throw "Installer payload not found: $payload"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

function ConvertTo-PowerShellLiteral([string]$value) {
    return "'" + $value.Replace("'", "''") + "'"
}

function Get-PayloadEntries {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($payload)
    try {
        return @(
            $zip.Entries |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_.FullName) -and -not $_.FullName.EndsWith("/") } |
                ForEach-Object { $_.FullName }
        )
    }
    finally {
        $zip.Dispose()
    }
}

function New-Label([string]$text, [int]$x, [int]$y, [int]$width, [int]$height, [float]$size = 9, [bool]$bold = $false) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $text
    $label.SetBounds($x, $y, $width, $height)
    $label.ForeColor = [System.Drawing.Color]::FromArgb(226, 232, 240)
    $label.BackColor = [System.Drawing.Color]::Transparent
    $fontStyle = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    $label.Font = New-Object System.Drawing.Font -ArgumentList "Segoe UI", $size, $fontStyle
    return $label
}

function Install-DiscBox([string]$installDir, [bool]$createDesktopShortcut, [bool]$launchAfterInstall) {
    $installDir = $installDir.Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($installDir)) {
        throw "Choose an installation folder."
    }

    if (-not [System.IO.Path]::IsPathRooted($installDir)) {
        throw "Choose a full installation path."
    }

    New-Item -ItemType Directory -Force -Path $installDir | Out-Null

    $children = @(Get-ChildItem -LiteralPath $installDir -Force -ErrorAction SilentlyContinue)
    $isDiscBoxFolder = Test-Path -LiteralPath (Join-Path $installDir "DiscBox.exe")
    if ($children.Count -gt 0 -and -not $isDiscBoxFolder) {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            "This folder is not empty. DiscBox can install here, but uninstall will only remove files added by DiscBox. Continue?",
            "DiscBox Setup",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) {
            throw "Installation cancelled."
        }
    }

    Expand-Archive -LiteralPath $payload -DestinationPath $installDir -Force

    $entries = Get-PayloadEntries
    $manifestPath = Join-Path $installDir "DiscBox.install_manifest.txt"
    Set-Content -LiteralPath $manifestPath -Value $entries -Encoding UTF8

    $exePath = Join-Path $installDir "DiscBox.exe"
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "DiscBox.exe was not found after extraction."
    }

    $literalInstallDir = ConvertTo-PowerShellLiteral $installDir
    $uninstallScript = @"
`$ErrorActionPreference = "Stop"
`$appName = "DiscBox"
`$installDir = $literalInstallDir
`$startMenuDir = Join-Path `$env:APPDATA "Microsoft\Windows\Start Menu\Programs\DiscBox"
`$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "DiscBox.lnk"
`$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DiscBox"
`$manifestPath = Join-Path `$installDir "DiscBox.install_manifest.txt"
`$uninstallPath = Join-Path `$installDir "Uninstall-DiscBox.ps1"

function Test-InInstallRoot([string]`$path) {
    `$root = [System.IO.Path]::GetFullPath(`$installDir)
    `$rootWithSlash = if (`$root.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { `$root } else { `$root + [System.IO.Path]::DirectorySeparatorChar }
    `$full = [System.IO.Path]::GetFullPath(`$path)
    return `$full.Equals(`$root, [System.StringComparison]::OrdinalIgnoreCase) -or `$full.StartsWith(`$rootWithSlash, [System.StringComparison]::OrdinalIgnoreCase)
}

Remove-Item -LiteralPath `$desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$startMenuDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$uninstallKey -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath `$manifestPath) {
    Get-Content -LiteralPath `$manifestPath | Sort-Object Length -Descending | ForEach-Object {
        `$target = Join-Path `$installDir `$_
        if ((Test-Path -LiteralPath `$target) -and (Test-InInstallRoot `$target)) {
            Remove-Item -LiteralPath `$target -Force -ErrorAction SilentlyContinue
        }
    }
}

Remove-Item -LiteralPath `$manifestPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$uninstallPath -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath `$installDir) {
    Get-ChildItem -LiteralPath `$installDir -Directory -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath `$_.FullName -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath `$_.FullName -Force -ErrorAction SilentlyContinue
            }
        }

    if (-not (Get-ChildItem -LiteralPath `$installDir -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath `$installDir -Force -ErrorAction SilentlyContinue
    }
}
"@

    $uninstallPath = Join-Path $installDir "Uninstall-DiscBox.ps1"
    Set-Content -LiteralPath $uninstallPath -Value $uninstallScript -Encoding UTF8

    New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $startShortcut = Join-Path $startMenuDir "DiscBox.lnk"
    $shortcutPaths = @($startShortcut)
    if ($createDesktopShortcut) {
        $shortcutPaths += Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "DiscBox.lnk"
    }

    foreach ($shortcutPath in $shortcutPaths) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exePath
        $shortcut.WorkingDirectory = $installDir
        $shortcut.Description = "DiscBox"
        $shortcut.IconLocation = "$exePath,0"
        $shortcut.Save()
    }

    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DiscBox"
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value $appName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "DisplayVersion" -Value $version -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "Publisher" -Value $publisher -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $installDir -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "DisplayIcon" -Value "$exePath,0" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallPath`"" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "NoModify" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "NoRepair" -Value 1 -PropertyType DWord -Force | Out-Null

    if ($launchAfterInstall) {
        Start-Process -FilePath $exePath
    }
}

[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text = "DiscBox Setup"
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.ClientSize = New-Object System.Drawing.Size -ArgumentList 620, 330
$form.BackColor = [System.Drawing.Color]::FromArgb(8, 12, 20)

if (Test-Path -LiteralPath $installerIcon) {
    $form.Icon = New-Object System.Drawing.Icon -ArgumentList $installerIcon
}

$title = New-Label "Install DiscBox" 28 24 560 34 18 $true
$subtitle = New-Label "Choose where DiscBox should be installed." 30 61 560 24 9 $false
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(148, 163, 184)

$pathLabel = New-Label "Installation folder" 30 104 560 22 9 $true

$pathBox = New-Object System.Windows.Forms.TextBox
$pathBox.SetBounds(30, 130, 456, 28)
$pathBox.Text = $defaultInstallDir
$pathBox.BackColor = [System.Drawing.Color]::FromArgb(13, 18, 32)
$pathBox.ForeColor = [System.Drawing.Color]::FromArgb(226, 232, 240)
$pathBox.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$pathBox.Font = New-Object System.Drawing.Font -ArgumentList "Segoe UI", 10

$browseButton = New-Object System.Windows.Forms.Button
$browseButton.SetBounds(498, 128, 90, 32)
$browseButton.Text = "Browse..."
$browseButton.BackColor = [System.Drawing.Color]::FromArgb(49, 61, 90)
$browseButton.ForeColor = [System.Drawing.Color]::White
$browseButton.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$browseButton.FlatAppearance.BorderSize = 0

$desktopCheck = New-Object System.Windows.Forms.CheckBox
$desktopCheck.SetBounds(30, 182, 260, 26)
$desktopCheck.Text = "Create desktop shortcut"
$desktopCheck.Checked = $true
$desktopCheck.ForeColor = [System.Drawing.Color]::FromArgb(226, 232, 240)
$desktopCheck.BackColor = [System.Drawing.Color]::Transparent
$desktopCheck.Font = New-Object System.Drawing.Font -ArgumentList "Segoe UI", 9

$launchCheck = New-Object System.Windows.Forms.CheckBox
$launchCheck.SetBounds(30, 212, 260, 26)
$launchCheck.Text = "Launch DiscBox after setup"
$launchCheck.Checked = $true
$launchCheck.ForeColor = [System.Drawing.Color]::FromArgb(226, 232, 240)
$launchCheck.BackColor = [System.Drawing.Color]::Transparent
$launchCheck.Font = New-Object System.Drawing.Font -ArgumentList "Segoe UI", 9

$statusLabel = New-Label "" 30 252 360 24 9 $false
$statusLabel.ForeColor = [System.Drawing.Color]::FromArgb(0, 229, 255)

$installButton = New-Object System.Windows.Forms.Button
$installButton.SetBounds(392, 272, 96, 36)
$installButton.Text = "Install"
$installButton.BackColor = [System.Drawing.Color]::FromArgb(124, 58, 237)
$installButton.ForeColor = [System.Drawing.Color]::White
$installButton.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$installButton.FlatAppearance.BorderSize = 0
$installButton.Font = New-Object System.Drawing.Font -ArgumentList "Segoe UI", 9, ([System.Drawing.FontStyle]::Bold)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.SetBounds(500, 272, 88, 36)
$cancelButton.Text = "Cancel"
$cancelButton.BackColor = [System.Drawing.Color]::FromArgb(31, 45, 69)
$cancelButton.ForeColor = [System.Drawing.Color]::White
$cancelButton.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$cancelButton.FlatAppearance.BorderSize = 0

$browseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Choose the DiscBox installation folder"
    $dialog.SelectedPath = $pathBox.Text
    $dialog.ShowNewFolderButton = $true
    if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $pathBox.Text = $dialog.SelectedPath
    }
})

$cancelButton.Add_Click({
    $form.Close()
})

$installButton.Add_Click({
    try {
        $installButton.Enabled = $false
        $cancelButton.Enabled = $false
        $browseButton.Enabled = $false
        $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        $statusLabel.Text = "Installing..."
        $form.Refresh()

        Install-DiscBox $pathBox.Text $desktopCheck.Checked $launchCheck.Checked

        $statusLabel.Text = "Installed successfully."
        [System.Windows.Forms.MessageBox]::Show($form, "DiscBox was installed successfully.", "DiscBox Setup", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        $form.Close()
    }
    catch {
        if ($_.Exception.Message -ne "Installation cancelled.") {
            [System.Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, "DiscBox Setup", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        }
        $statusLabel.Text = ""
        $installButton.Enabled = $true
        $cancelButton.Enabled = $true
        $browseButton.Enabled = $true
    }
    finally {
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
    }
})

$form.Controls.AddRange(@(
    $title,
    $subtitle,
    $pathLabel,
    $pathBox,
    $browseButton,
    $desktopCheck,
    $launchCheck,
    $statusLabel,
    $installButton,
    $cancelButton
))

$form.AcceptButton = $installButton
$form.CancelButton = $cancelButton
[void]$form.ShowDialog()
