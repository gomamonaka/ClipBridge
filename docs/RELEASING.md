# Private Release の作成

配布先は [gomamonaka/ClipBridge](https://github.com/gomamonaka/ClipBridge) です。リリース作成スクリプトは、Private であること、タグと製品バージョン、ローカルとリモートのタグが同じコミットを指すことを確認します。既存の Release と配布ファイルは上書きしません。

## 次のバージョンを公開する

1. `src/ClipBridge.App/ClipBridge.App.csproj` の `Version` を更新し、変更内容と文書を確定します。
2. 変更をコミットして `main` を Push します。
3. 同じコミットへ `v1.4.6` のようなタグを作り、Push します。

```powershell
git add .
git commit -m "Release ClipBridge 1.4.6"
git push origin main
git tag -a v1.4.6 -m "ClipBridge 1.4.6"
git push origin v1.4.6
```

GitHub Actions の Windows release がタグのソースを検証・ビルドし、インストーラー、ポータブル ZIP、SHA-256 一覧を添付した Release を作成します。アプリの接続設定、履歴、OneDrive データ、署名用秘密鍵はビルド対象へ含めません。タグ対象を再現可能なソースとして残し、後から公開済みタグを移動しないでください。

## ローカルで作成する

Windows、.NET 10 SDK、Inno Setup 6.7.3、認証済みの GitHub CLI を用意します。タグを Push した、変更のないチェックアウトで実行します。GitHub Actions が同じ Release を作成中の場合は重複実行しないでください。

```powershell
New-Item -ItemType Directory -Path artifacts -Force
Set-Content -LiteralPath artifacts/release-notes.md -Value "今回の変更内容"
.\scripts\release.ps1 -Tag v1.4.6 `
  -NotesFile .\artifacts\release-notes.md `
  -CompilerPath 'C:\Tools\Inno Setup 6\ISCC.exe'
```

全テスト、発行、インストーラー作成、ZIP とハッシュの生成を行ってから Release を作成します。準備済みの配布物を検証して再利用する場合は `-UseExistingArtifacts`、下書きとして作成する場合は `-Draft` を付けます。再利用時も製品バージョンと ZIP 内の配布ファイルの許可リストを検査します。

## 過去版の履歴

v1.0.0〜v1.3.2 のタグは、当時保存した source ZIP から復元したソースのスナップショットです。元の開発中の Git コミット日時・SHA を復元したものではありません。各 Release の ZIP は、当時の保存済み配布物です。インストーラーは v1.4.0 から追加しています。

Inno Setup の公式配布物は SHA-256 と Authenticode の発行元を確認して取得します。アプリと生成したインストーラーのコード署名は、現時点では設定していません。
