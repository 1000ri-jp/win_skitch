# WinSkitch Python 版

Tkinter と Pillow で作成した従来の Windows 用の画面キャプチャ・画像注釈アプリです。C# / WPF 版の移植元として保存しています。配布用の Windows 版については、[Windows 版 README](../windows/README.md)を参照してください。

## セットアップ・起動

Windows と、tkinter を含む Python 3 が必要です。以下のコマンドはリポジトリのルートで実行します。

```powershell
python -m venv .\python\.venv
.\python\.venv\Scripts\python.exe -m pip install -r .\python\requirements.txt
.\python\start.bat
```

`python\start.bat` は `python\.venv\Scripts\pythonw.exe` を使用します。この仮想環境がない場合は、従来のルートにある `.venv\Scripts\pythonw.exe` を使用します。どちらもなければ、セットアップ手順を表示して終了します。

仮想環境を直接指定して起動することもできます。エラーをコンソールで確認したい場合は、`python.exe` を使用してください。

```powershell
.\python\.venv\Scripts\python.exe .\python\run.pyw
```

## 操作

`Ctrl + Shift + 5` で範囲キャプチャ、`Ctrl + Shift + 6` でカーソルのあるモニターをキャプチャできます。画像を開く・貼り付ける・ドラッグ＆ドロップする操作と、矢印・テキスト・図形・ペン・蛍光ペン・モザイク・スタンプ・切り抜きに対応しています。保存時は注釈を画像に合成し、PNG / JPEG へ書き出します。

ウィンドウの × ボタンは画面を閉じ、トレイに常駐します。再表示はトレイメニューの「ウィンドウを表示」、完全な終了は「終了」を選んでください。

## ソース構成

| ファイル | 役割 |
| --- | --- |
| `run.pyw` | 起動エントリーポイント |
| `winskitch/app.py` | アプリ・トレイ・グローバルホットキー |
| `winskitch/editor.py` | 画像編集画面 |
| `winskitch/annotations.py` | 注釈の描画・編集 |
| `winskitch/capture.py` | キャプチャ範囲の選択 |
| `winskitch/win32.py` | Windows API 連携 |
| `winskitch/icons.py` | アイコン描画 |
| `requirements.txt` | Python 依存パッケージ |
| `start.bat` | 仮想環境を選んで起動 |
