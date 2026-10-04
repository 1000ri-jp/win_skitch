# WinSkitch

Windows 用の画面キャプチャ・画像注釈アプリです。配布用の C# / WPF 版と、移植元の Python 版を分けて管理しています。

| 版 | ソース | 起動・開発手順 |
| --- | --- | --- |
| Windows 版（C# / WPF） | [`windows/WinSkitch/`](windows/WinSkitch/) | [Windows 版 README](windows/README.md) |
| Python 版（Tkinter） | [`python/winskitch/`](python/winskitch/) | [Python 版 README](python/README.md) |

配布する場合は Windows 版を使用してください。公開ビルドの `artifacts\win-x64\WinSkitch.exe` は、Python・.NET ランタイムの事前インストールなしで起動できます。Windows ログイン時の自動起動は、アプリの「設定」→「Windowsログイン時に起動」で切り替えられます。

リポジトリのルートから Windows 版を起動するには、次を実行します。

```powershell
.\windows\start.bat
```

## ディレクトリ構成

```text
python/                 Python 版のソース・依存関係・起動スクリプト
windows/                WPF 版のソース・テスト・ビルド／配布スクリプト
artifacts/              配布 EXE・テスト出力（Git 管理対象外）
.tools/                 ローカルの .NET SDK（Git 管理対象外）
.venv/                  従来の Python 仮想環境（Git 管理対象外）
```
