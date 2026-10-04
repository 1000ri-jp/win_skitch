param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'WinSkitch\WinSkitch.csproj') -Raw
    $Version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must contain three numeric components, for example 1.1.0.'
}

$releaseDirectory = Join-Path $workspace "artifacts\releases\v$Version"
$assetsDirectory = Join-Path $releaseDirectory 'assets'
$runtimes = @('win-x64', 'win-arm64')
$checksumPath = Join-Path $assetsDirectory 'SHA256SUMS.txt'
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Check all inputs before creating any release files. Package only previously published binaries.
foreach ($runtime in $runtimes) {
    $executable = Join-Path $releaseDirectory "$runtime\WinSkitch.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Publish the $runtime binary first: $executable"
    }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    if ($fileVersion -notmatch ('^' + [regex]::Escape($Version) + '\.\d+$')) {
        throw "The $runtime executable has FileVersion '$fileVersion'; expected $Version.*."
    }
    $zipPath = Join-Path $assetsDirectory "WinSkitch-v$Version-$runtime.zip"
    if (Test-Path -LiteralPath $zipPath) {
        throw "Release asset already exists; preserving it: $zipPath"
    }
}
if (Test-Path -LiteralPath $checksumPath) {
    throw "Release checksums already exist; preserving them: $checksumPath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
$checksums = @()
foreach ($runtime in $runtimes) {
    $runtimeDirectory = Join-Path $releaseDirectory $runtime
    $executable = Join-Path $runtimeDirectory 'WinSkitch.exe'
    $readmePath = Join-Path $runtimeDirectory 'README.txt'
    $zipName = "WinSkitch-v$Version-$runtime.zip"
    $zipPath = Join-Path $assetsDirectory $zipName
    $temporaryZip = Join-Path $assetsDirectory ('.package-' + [guid]::NewGuid().ToString('N') + '.zip')
    if ($runtime -eq 'win-x64') {
        $platform = 'x64（Intel / AMD の一般的な 64 ビット PC）'
        $validation = 'x64 版は自動テストと配布 EXE の起動確認を実施しています。'
    } else {
        $platform = 'ARM64（ARM64 版 Windows の PC）'
        $validation = 'ARM64 版はクロスビルドです。ARM64 実機での動作確認は未実施です。'
    }
    $readme = @"
WinSkitch v$Version - $platform

画面をキャプチャ・録画し、画像に矢印・文字・図形・モザイクなどを加える Windows アプリです。

使い始める・更新する
1. ZIP をすべて展開し、保存したいフォルダーに置いてください。
2. 更新時は、起動中の旧版をトレイの「終了」で閉じてから EXE を上書きしてください。
3. WinSkitch.exe をダブルクリックすると編集画面が開きます。
4. Python や .NET ランタイムの事前インストールは不要です。

キャプチャと画面録画
- Ctrl + Shift + 5: 範囲を選んでキャプチャ。
- Ctrl + Shift + 6: カーソルのあるモニターをキャプチャ。
- Ctrl + Shift + 7: 範囲録画を開始 / 録画中の動画を停止して保存。
- 録画では MP4 の保存場所と範囲を選び、3 秒のカウントダウン後に開始します。
- 「動画」メニューやトレイから、カーソルのあるモニター 1 台全体も録画できます。
- 動画はマウスカーソルを含む H.264 / MP4、15 fps、音声なしです。
- 録画には Windows の Media Foundation と H.264 エンコーダーが必要です。
- 録画範囲は幅・高さそれぞれ 4096 ピクセル以内で、対応サイズは PC のエンコーダーに従います。
- 録画を停止して保存すると MP4 が完成します。動画の再生には外部アプリを使用してください。

保存履歴
- 上部の「履歴」または「ファイル」メニュー →「保存履歴…」で開きます。
- 保存した画像・動画を新しい順に最大 20 件記録し、再起動後も残ります。
- 「保存先を開く」はエクスプローラーでファイルを選択します。
- ファイルが移動・削除済みでも元のフォルダーがあれば、そのフォルダーを開きます。
- 「ファイルを開く」は Windows の既定の画像アプリや動画プレーヤーで開きます。

常駐と自動起動
- ウィンドウの × はトレイに格納します。トレイのダブルクリックで開きます。
- 完全に終了するには、トレイを右クリックして「終了」を選びます。
- 設定またはトレイの「Windowsログイン時に起動」で自動起動を切り替えられます。
- 自動起動を使う場合、EXE を固定した場所に置いてから設定してください。
- EXE を移動する前にトレイから終了し、移動先の EXE で自動起動をオンにし直してください。
- WinSkitch.exe --tray で編集画面を開かずに常駐します。
- 他のアプリが同じキーを使う場合は、画面のボタンやトレイを使用してください。

注意点
- $validation
- この配布はコード署名を行っていないため、Windows が警告を表示する場合があります。
- 初回起動時、同梱ランタイムの一部をユーザーの一時フォルダーへ展開します。
- 保存・コピー時は注釈を合成し、透過部分は白になります。
- 後から注釈を編集するためのプロジェクト保存形式はありません。

詳しい操作・ソース・問題の報告:
https://github.com/1000ri-jp/win_skitch
"@
    [System.IO.File]::WriteAllText($readmePath, $readme.Replace("`r`n", "`n").Replace("`n", "`r`n") + "`r`n", $utf8)
    $sourceHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    try {
        $archive = [System.IO.Compression.ZipFile]::Open($temporaryZip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $executable, 'WinSkitch.exe', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $readmePath, 'README.txt', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        } finally {
            $archive.Dispose()
        }

        $archive = [System.IO.Compression.ZipFile]::OpenRead($temporaryZip)
        try {
            $entries = @($archive.Entries | ForEach-Object { $_.FullName })
            if ($entries.Count -ne 2 -or $entries -notcontains 'WinSkitch.exe' -or $entries -notcontains 'README.txt') {
                throw "Unexpected entries in $zipName."
            }
            $stream = $archive.GetEntry('WinSkitch.exe').Open()
            $sha256 = [System.Security.Cryptography.SHA256]::Create()
            try {
                $packedHash = ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
            } finally {
                $sha256.Dispose()
                $stream.Dispose()
            }
            if ($packedHash -ne $sourceHash) {
                throw "The packaged executable differs from the published executable in $zipName."
            }
            $reader = New-Object System.IO.StreamReader($archive.GetEntry('README.txt').Open(), $utf8)
            try {
                if ($reader.ReadToEnd() -ne [System.IO.File]::ReadAllText($readmePath, $utf8)) {
                    throw "The packaged README differs from the source README in $zipName."
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $archive.Dispose()
        }
        Move-Item -LiteralPath $temporaryZip -Destination $zipPath
    } finally {
        if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryZip
        }
    }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $zipName"
    Write-Host "Verified: $zipPath"
}
[System.IO.File]::WriteAllText($checksumPath, ($checksums -join "`r`n") + "`r`n", $utf8)
foreach ($line in [System.IO.File]::ReadAllLines($checksumPath, $utf8)) {
    $hash, $name = $line -split '  ', 2
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $assetsDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $actualHash) {
        throw "Checksum verification failed for $name."
    }
}
Write-Host "Release assets ready: $assetsDirectory"
