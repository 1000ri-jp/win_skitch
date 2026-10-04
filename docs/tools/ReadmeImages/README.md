# README 画像の再生成

Windows と .NET 10 SDK があれば、リポジトリ直下から実行できます。

```powershell
.\docs\tools\ReadmeImages\render.ps1
```

`docs/images/editor-overview.png` は実際の WPF `MainWindow` の内容をオフスクリーン描画したものです。ウィンドウ枠は含みません。`annotation-example.png` は同じデータを実際の `ImageDocument` と `Annotation` の描画処理で書き出した画像です。

注釈の背景はコードで作った架空の確認画面です。実際のデスクトップやクリップボードの読み取り、トレイ常駐、ホットキー登録、スタートアップ設定の変更は行いません。

`docs/images/workflow.png` は操作の流れをコードで描いた説明図です。同じスクリプトで再生成されます。
