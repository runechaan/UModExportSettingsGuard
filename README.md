# UModExportSettingsGuard

uMod（Warudo Mod SDK）のエクスポート設定が、スクリプトの再コンパイル時に空になってしまう不具合への対策ツールです。Unity Editor 用のスクリプト1ファイルだけで動作します。

A single Unity Editor script that protects uMod (Warudo Mod SDK) export settings from being wiped on script recompilation.

---

## 日本語

### 解決する問題

Unity でスクリプトを再コンパイルすると、uMod 本体が既存の `ExportSettings.asset` を見失い、空の新しいインスタンスで上書きしてしまうことがあります。その結果、設定していた Mod Export Profile（Mod Name / Asset Directory / Export Directory など）が消え、ビルドできなくなります。

これは uMod 側の挙動のため、MOD 制作者の側で直接修正することはできません。このツールは「空になったら即座に検知して、バックアップから書き戻す」ことで対策します。

### 仕組み

1. **編集の自動保存**: Mod Settings を編集すると、入力が止まってから約2秒後に自動でディスクへ保存し、バックアップを取ります。
2. **リロード前の保存**: ドメインリロード（再コンパイル）の直前にも強制的に保存します。
3. **自動復元**: それでも設定が空になった場合、最新のバックアップから自動で書き戻します。リロード直後の数秒間は特に空になりやすいため、この間は監視間隔を短くして連続で検知・復元します。

空かどうかの判定は、uMod の `exportProfiles` フィールドの配列サイズだけを見ています。

### 導入手順

1. `UModExportSettingsGuard.cs` を、Unity プロジェクトの `Assets/Editor/` フォルダに置きます。`Editor` フォルダが無ければ作成してください。
2. **MOD のアセットフォルダの外に置いてください。** `Assets/Editor/` 配下のスクリプトは Editor 専用として扱われ、MOD のエクスポートには含まれません。
3. Unity がコンパイルを終えると、自動で監視が始まります。設定は不要です。
4. 導入後に一度 **Tools > UMod Settings Guard > Backup Now** を実行しておくことを推奨します。これで「正常な状態のバックアップ」が確実に1件できます。

### メニュー

すべて Unity のメニューバー **Tools > UMod Settings Guard** 配下にあります。

| メニュー | 動作 |
|---|---|
| Backup Now | 今すぐバックアップを取る |
| Restore Latest Backup | 最新のバックアップから手動で復元する |
| Open Backup Folder | バックアップ先のフォルダを開く |
| Auto Restore | 自動復元の ON / OFF を切り替える（初期値は ON） |

### バックアップの保存先

```
<Unity プロジェクトのルート>/UModExportSettingsBackup/
  ExportSettings.latest.asset           最新
  ExportSettings_yyyyMMdd_HHmmss.asset  履歴（最新20件）
```

`Assets` の外に作られるため、Unity にはアセットとして認識されません。

### Git 管理下のプロジェクトで使う場合

`UModExportSettingsBackup/` はローカルの絶対パスを含むスナップショットが溜まり続けるため、Unity プロジェクトの `.gitignore` に次の1行を追加してください。

```
UModExportSettingsBackup/
```

### 動作確認環境と注意

- Unity 2021.3.45f2 / Warudo SDK 0.14.3.5 で動作を確認しています。
- uMod 内部のフィールド名（`exportProfiles`）に依存しているため、将来の uMod 更新で効かなくなる可能性があります。その場合は「exportProfiles フィールドが見つかりません」という警告が1回だけ出て、ツールは何もしなくなります。設定を壊すことはありません。
- ログはすべて `[UMod Guard]` という接頭辞付きで Unity のコンソールに出ます。
- 自動復元の ON / OFF 設定は Unity の EditorPrefs にプロジェクトごとに保存されます。

### ライセンス

MIT License

---

## English

### The problem

When Unity recompiles scripts, uMod can lose track of the existing `ExportSettings.asset` and overwrite it with a fresh, empty instance. Your Mod Export Profiles (Mod Name, Asset Directory, Export Directory, and so on) disappear and you can no longer build.

This happens inside uMod itself and cannot be fixed from the mod author's side. This tool works around it by detecting the wipe immediately and restoring the settings from a backup.

### How it works

1. **Auto-save on edit**: When you edit the Mod Settings, the asset is saved to disk and backed up about two seconds after you stop typing.
2. **Save before reload**: The asset is also force-saved right before every domain reload (recompile).
3. **Auto-restore**: If the settings still end up empty, the latest backup is written back automatically. The first few seconds after a reload are the most fragile, so during that window the tool polls at a much shorter interval.

Emptiness is determined solely by the array size of uMod's `exportProfiles` field.

### Installation

1. Put `UModExportSettingsGuard.cs` into the `Assets/Editor/` folder of your Unity project. Create the `Editor` folder if it does not exist.
2. **Keep it outside your mod's asset folder.** Scripts under `Assets/Editor/` are editor-only and are never included in the mod export.
3. Monitoring starts automatically once Unity finishes compiling. No configuration is needed.
4. It is recommended to run **Tools > UMod Settings Guard > Backup Now** once after installing, so that a known-good backup exists from the start.

### Menu

All items live under **Tools > UMod Settings Guard** in the Unity menu bar.

| Menu item | Action |
|---|---|
| Backup Now | Take a backup right now |
| Restore Latest Backup | Manually restore from the latest backup |
| Open Backup Folder | Open the backup folder |
| Auto Restore | Toggle automatic restore (default: ON) |

### Backup location

```
<Unity project root>/UModExportSettingsBackup/
  ExportSettings.latest.asset           latest
  ExportSettings_yyyyMMdd_HHmmss.asset  history (last 20)
```

The folder is created outside `Assets`, so Unity does not treat it as an asset.

### If your project is under Git

`UModExportSettingsBackup/` accumulates snapshots that contain local absolute paths. Add this line to your Unity project's `.gitignore`:

```
UModExportSettingsBackup/
```

### Tested environment and caveats

- Tested with Unity 2021.3.45f2 and Warudo SDK 0.14.3.5.
- The tool depends on uMod's internal field name (`exportProfiles`), so a future uMod update may stop it from working. In that case it logs a single warning ("exportProfiles field not found") and then does nothing. It will never damage your settings.
- All log messages are prefixed with `[UMod Guard]` in the Unity console. The messages themselves are in Japanese.
- The Auto Restore setting is stored per project in Unity's EditorPrefs.

### License

MIT License
