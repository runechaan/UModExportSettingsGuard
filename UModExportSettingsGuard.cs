// =============================================================================
// UModExportSettingsGuard.cs
//
// uMod（Warudo Mod SDK）のエクスポート設定が、スクリプトの再コンパイル時に
// 空になってしまう問題への対策です。
//
// 根本原因（実機で確認済み）
//   ドメインリロード直後、uMod 本体（UMod.Exporter）が既存の ExportSettings
//   アセットを見つけられず、空の新しいインスタンスを生成してファイルへ
//   上書きすることがあります。これは uMod 側の挙動で、こちらからは直接
//   修正できません。ここでは「空になったら即座に検知して復元する」
//   ことで対策します。
//
//   旧バージョンの不具合: 空判定に SerializedObject の汎用スコアリングを
//   使っていましたが、Unity が自動で持つ m_Script（スクリプト自身への
//   参照）まで「中身がある」とカウントしてしまい、exportProfiles が空でも
//   スコアが 0 にならず、自動復元が実質的に機能していませんでした。
//   このバージョンでは exportProfiles フィールドを名前で直接参照し、
//   配列サイズだけで空かどうかを判定します。
//
//   1. 手動編集の保存 : Mod Settings を編集すると、入力が落ち着いた時点で
//                       自動的にディスクへ保存し、バックアップを取ります
//   2. 予防           : ドメインリロードの直前にも強制保存します
//   3. 復旧           : それでも空になった場合、バックアップから自動で書き戻します
//                       リロード直後の数秒間は特に空になりやすいため、
//                       監視間隔を短くして連続で検知・復元します（バースト監視）
//
// 置き場所
//   Assets/Editor/UModExportSettingsGuard.cs
//   （MOD のアセットフォルダの外に置いてください。エクスポートには含まれません）
//
// バックアップの保存先
//   <プロジェクトのルート>/UModExportSettingsBackup/
//     ExportSettings.latest.asset          最新
//     ExportSettings_yyyyMMdd_HHmmss.asset 履歴（最新20件）
//   Assets の外に作られるため Unity には資産として認識されません。
//   Git 管理下のプロジェクトで使う場合は .gitignore に
//   UModExportSettingsBackup/ を追加してください（ローカルの絶対パスを
//   含むスナップショットが溜まり続けるため）。
//
// メニュー
//   Tools/UMod Settings Guard/Backup Now              今すぐバックアップ
//   Tools/UMod Settings Guard/Restore Latest Backup   手動で復元
//   Tools/UMod Settings Guard/Open Backup Folder      保存先を開く
//   Tools/UMod Settings Guard/Auto Restore            自動復元の ON / OFF
// =============================================================================

#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class UModExportSettingsGuard {

    // EditorPrefs はプロジェクト単位ではなくマシン全体で共有されるため、
    // 同じマシンに複数の uMod プロジェクトがあっても設定が混ざらないよう、
    // プロジェクトごとのサフィックスをキーに含めます。
    private static readonly string ProjectSuffix = Application.dataPath.GetHashCode().ToString("X8");
    private static string PrefAutoRestore   { get { return "UModGuard.AutoRestore." + ProjectSuffix; } }
    private static string PrefLastGoodCount { get { return "UModGuard.LastGoodProfileCount." + ProjectSuffix; } }
    private const string BackupFolder      = "UModExportSettingsBackup";
    private const string LatestName        = "ExportSettings.latest.asset";
    private const string HistoryPrefix     = "ExportSettings_";
    private const int    HistoryKeep       = 20;
    private const string ExportProfilesField = "exportProfiles";

    private const double NormalCheckInterval = 1.0;   // 平常時の監視間隔（秒）
    private const double BurstCheckInterval  = 0.15;  // リロード直後の監視間隔（秒）
    private const double BurstDuration       = 6.0;   // リロード直後にバースト監視する時間（秒）
    private const double DebounceSeconds     = 2.0;   // 編集が止まってから保存するまでの猶予
    private const double RestoreCooldown     = 1.0;   // 復元の連発防止（バースト中も最低限）
    private const double MissingRetryInterval = 30.0; // ExportSettings.asset が見つからないときに再検索するまでの間隔（秒）

    private static double nextCheck;
    private static double burstUntil;
    private static double restoreCooldownUntil;
    private static double pendingSince;           // 0 なら保留中の変更なし
    private static double missingRetryAt;         // この時刻までは ExportSettings.asset の再検索をしない
    private static string lastSeenContent;        // 直近チェック時のディスク内容（編集検出用）
    private static string cachedAssetPath;
    private static string lastBackedUpContent;    // 直近にバックアップした内容。null ならまだ latest と比較していない
    private static bool   warnedMissingField;
    private static bool   warnedEmptyWhileAutoRestoreOff;   // Auto Restore OFF 中、空状態の警告を1回だけ出すためのフラグ

    // ------------------------------------------------------------------ 起動

    static UModExportSettingsGuard() {
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        EditorApplication.update += OnUpdate;

        // リロード直後。AssetDatabase が使える状態になってから確認します。
        // uMod がこのタイミングで設定を空の新規インスタンスに差し替えることがあるため、
        // 起動直後はバースト監視で連続チェックします。
        burstUntil = EditorApplication.timeSinceStartup + BurstDuration;
        EditorApplication.delayCall += () => Check(true);
    }

    private static bool AutoRestore {
        get { return EditorPrefs.GetBool(PrefAutoRestore, true); }
        set { EditorPrefs.SetBool(PrefAutoRestore, value); }
    }

    private static int LastGoodProfileCount {
        get { return EditorPrefs.GetInt(PrefLastGoodCount, 0); }
        set { EditorPrefs.SetInt(PrefLastGoodCount, value); }
    }

    // ------------------------------------------------------------ 定期チェック

    private static void OnUpdate() {
        var interval = EditorApplication.timeSinceStartup < burstUntil ? BurstCheckInterval : NormalCheckInterval;
        if (EditorApplication.timeSinceStartup < nextCheck) return;
        nextCheck = EditorApplication.timeSinceStartup + interval;
        Check(false);
    }

    /// <summary>
    /// ドメインリロードの直前。ここでディスクへ書き出しておくのが予防策の本命です。
    /// uMod 側が SetDirty を呼んでいないため、こちらから明示的に汚してから保存します。
    /// </summary>
    private static void OnBeforeAssemblyReload() {
        try {
            var path = FindAssetPath();
            if (path == null) return;

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null) return;

            int profileCount;
            if (!TryGetProfileCount(asset, out profileCount)) return;
            if (profileCount <= 0) return;   // 空の状態を保存しても意味がありません

            LastGoodProfileCount = profileCount;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            var content = ReadAllTextSafe(path);
            lastSeenContent = content;
            Backup(path, content, profileCount, "リロード前");
        } catch (Exception e) {
            Debug.LogWarning("[UMod Guard] リロード前の保存に失敗しました: " + e.Message);
        }
    }

    /// <summary>
    /// ドメインリロード完了直後。ここで uMod が設定を空の新規インスタンスに
    /// 差し替えている場合があるため、即座にチェックしバースト監視を開始します。
    /// </summary>
    private static void OnAfterAssemblyReload() {
        burstUntil = EditorApplication.timeSinceStartup + BurstDuration;
        Check(true);
    }

    // -------------------------------------------------------------- 本体の判定

    private static void Check(bool afterReload) {
        try {
            var path = FindAssetPath();
            if (path == null) return;

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null) return;

            int profileCount;
            if (!TryGetProfileCount(asset, out profileCount)) {
                if (!warnedMissingField) {
                    warnedMissingField = true;
                    Debug.LogWarning("[UMod Guard] exportProfiles フィールドが見つかりません。"
                        + "uMod のバージョンが変わった可能性があります。ガードは何もしません。");
                }
                return;
            }
            warnedMissingField = false;

            // ---- 空になっていたら復元 ----
            if (profileCount <= 0) {
                pendingSince = 0;
                lastSeenContent = ReadAllTextSafe(path);

                if (LastGoodProfileCount <= 0) return;          // 良いバックアップの実績がない
                if (!File.Exists(LatestBackupPath)) return;

                if (!AutoRestore) {
                    // Auto Restore を意図的に切って uMod 側の本来のエラーを観察したい場面があるため、
                    // 空状態が続く間は毎回ログを流さず、最初の1回だけ知らせます。
                    if (!warnedEmptyWhileAutoRestoreOff) {
                        warnedEmptyWhileAutoRestoreOff = true;
                        Debug.LogWarning("[UMod Guard] エクスポート設定（プロファイル）が空になっています。"
                            + "Tools/UMod Settings Guard/Restore Latest Backup で手動復元できます"
                            + "（Auto Restore が OFF のため、このメッセージは状態が変わるまで再表示しません）。");
                    }
                    return;
                }

                if (EditorApplication.timeSinceStartup < restoreCooldownUntil) return;
                Restore(path, true);
                return;
            }

            warnedEmptyWhileAutoRestoreOff = false;
            LastGoodProfileCount = profileCount;

            var currentContent = ReadAllTextSafe(path);
            if (currentContent == null) return;

            // ---- 初回、またはリロード直後の同期 ----
            if (lastSeenContent == null) {
                lastSeenContent = currentContent;
                if (currentContent != lastBackedUpContent) {
                    Backup(path, currentContent, profileCount, afterReload ? "リロード後" : "初回");
                }
                return;
            }

            // ---- 手動編集の検出（変更 → 2秒静かになったら保存）----
            if (currentContent != lastSeenContent) {
                lastSeenContent = currentContent;
                pendingSince = EditorApplication.timeSinceStartup;   // 入力中とみなして待つ
                return;
            }

            if (pendingSince > 0.0
                && EditorApplication.timeSinceStartup - pendingSince >= DebounceSeconds) {
                pendingSince = 0;
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                var savedContent = ReadAllTextSafe(path);
                lastSeenContent = savedContent;
                Backup(path, savedContent, profileCount, "設定を編集");
            }
        } catch (Exception e) {
            Debug.LogWarning("[UMod Guard] チェック中にエラー: " + e.Message);
        }
    }

    /// <summary>
    /// exportProfiles フィールドの配列サイズを取得します。
    /// SerializedObject の汎用走査（NextVisible）は使いません。ドメインリロード直後の
    /// 一時的に不完全なオブジェクトでは m_Script 以降を正しく列挙できないことがあり、
    /// 「中身がある」と誤判定する原因になっていたためです。FindProperty による
    /// 名前指定のアクセスはこの状況でも正しく動作することを実機で確認しています。
    /// </summary>
    private static bool TryGetProfileCount(ScriptableObject asset, out int count) {
        count = 0;
        var so = new SerializedObject(asset);
        var prop = so.FindProperty(ExportProfilesField);
        if (prop == null) return false;
        count = prop.arraySize;
        return true;
    }

    // ------------------------------------------------------ バックアップと復元

    private static void Backup(string assetPath, string content, int profileCount, string reason) {
        if (string.IsNullOrEmpty(content)) return;

        // ドメインリロードで静的フィールドは消えるため、メモリ上の記録が無いときは
        // ディスク上の latest と比較します。こうしないとリロードのたびに同じ内容が
        // 履歴に積まれ、本当に意味のある古い履歴が押し出されてしまいます。
        if (lastBackedUpContent == null) {
            lastBackedUpContent = ReadAllTextSafe(LatestBackupPath);
        }
        if (content == lastBackedUpContent) return;   // 同じ内容なら書かない

        Directory.CreateDirectory(BackupDir);
        File.WriteAllText(LatestBackupPath, content);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(BackupDir, HistoryPrefix + stamp + ".asset"), content);

        TrimHistory();
        lastBackedUpContent = content;
        Debug.Log(string.Format("[UMod Guard] エクスポート設定をバックアップしました（{0}） プロファイル数:{1}",
            reason, profileCount));
    }

    private static void Restore(string assetPath, bool automatic) {
        var content = ReadAllTextSafe(LatestBackupPath);
        if (string.IsNullOrEmpty(content)) {
            Debug.LogWarning("[UMod Guard] バックアップが見つかりません。");
            return;
        }

        File.WriteAllText(ToAbsolute(assetPath), content);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        lastBackedUpContent = content;
        lastSeenContent = null;   // 読み直した内容で取り直します
        pendingSince = 0;
        restoreCooldownUntil = EditorApplication.timeSinceStartup + RestoreCooldown;
        // リロード直後に何度も差し替えられるケースに備え、復元後もバースト監視を継続します。
        burstUntil = Math.Max(burstUntil, EditorApplication.timeSinceStartup + BurstDuration);

        var restoredCount = CountProfilesInYaml(content);
        Debug.LogWarning(string.Format(
            "[UMod Guard] エクスポート設定が空になっていたため、{0}バックアップから復元しました: {1}（プロファイル数:{2}）",
            automatic ? "自動で" : "", assetPath, restoredCount));
    }

    private static int CountProfilesInYaml(string content) {
        if (string.IsNullOrEmpty(content)) return 0;
        int count = 0;
        foreach (var line in content.Split('\n')) {
            if (line.TrimStart().StartsWith("modName:")) count++;
        }
        return count;
    }

    private static void TrimHistory() {
        var files = Directory.GetFiles(BackupDir, HistoryPrefix + "*.asset")
            .OrderByDescending(f => f)
            .Skip(HistoryKeep)
            .ToArray();

        foreach (var f in files) {
            try { File.Delete(f); } catch (Exception) { /* 消せなくても致命的ではありません */ }
        }
    }

    // ---------------------------------------------------------------- パス解決

    private static string ProjectRoot {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    private static string BackupDir {
        get { return Path.Combine(ProjectRoot, BackupFolder); }
    }

    private static string LatestBackupPath {
        get { return Path.Combine(BackupDir, LatestName); }
    }

    /// <summary>
    /// Assets 以下から ExportSettings.asset を探し、プロジェクト相対パスで返します。
    /// force が true なら「見つからなかった」キャッシュを無視して即座に再検索します。
    /// </summary>
    private static string FindAssetPath(bool force = false) {
        if (cachedAssetPath != null && File.Exists(ToAbsolute(cachedAssetPath))) {
            return cachedAssetPath;
        }
        cachedAssetPath = null;

        // 見つからなかった結果もしばらく覚えておきます。そうしないとファイルが無い
        // プロジェクトで毎回 Assets 全体を再帰検索してしまい、Editor が重くなります。
        // メニューからの手動操作は force で即座に再検索します。
        if (!force && EditorApplication.timeSinceStartup < missingRetryAt) return null;

        string[] found;
        try {
            found = Directory.GetFiles(Application.dataPath, "ExportSettings.asset",
                                       SearchOption.AllDirectories);
        } catch (Exception) {
            missingRetryAt = EditorApplication.timeSinceStartup + MissingRetryInterval;
            return null;
        }
        if (found.Length == 0) {
            missingRetryAt = EditorApplication.timeSinceStartup + MissingRetryInterval;
            return null;
        }

        // UMod フォルダ配下のものを優先します
        var best = found.FirstOrDefault(f => f.Replace('\\', '/').Contains("/UMod/")) ?? found[0];

        var relative = "Assets" + best.Substring(Application.dataPath.Length);
        cachedAssetPath = relative.Replace('\\', '/');
        return cachedAssetPath;
    }

    private static string ToAbsolute(string projectRelative) {
        return Path.IsPathRooted(projectRelative)
            ? projectRelative
            : Path.Combine(ProjectRoot, projectRelative);
    }

    private static string ReadAllTextSafe(string path) {
        try {
            var full = ToAbsolute(path);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        } catch (Exception) {
            return null;
        }
    }

    // ------------------------------------------------------------------ メニュー

    [MenuItem("Tools/UMod Settings Guard/Backup Now")]
    private static void MenuBackupNow() {
        var path = FindAssetPath(true);
        if (path == null) {
            Debug.LogWarning("[UMod Guard] ExportSettings.asset が見つかりません。");
            return;
        }

        var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        if (asset == null) return;

        int profileCount;
        if (!TryGetProfileCount(asset, out profileCount)) {
            Debug.LogWarning("[UMod Guard] exportProfiles フィールドが見つかりません。");
            return;
        }
        if (profileCount <= 0) {
            Debug.LogWarning("[UMod Guard] 現在プロファイルが空のため、バックアップしませんでした"
                + "（空の状態で良いバックアップを上書きしないようにしています）。");
            return;
        }

        LastGoodProfileCount = Math.Max(LastGoodProfileCount, profileCount);
        pendingSince = 0;
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        var content = ReadAllTextSafe(path);
        lastSeenContent = content;
        Backup(path, content, profileCount, "手動");
    }

    [MenuItem("Tools/UMod Settings Guard/Restore Latest Backup")]
    private static void MenuRestore() {
        var path = FindAssetPath(true);
        if (path == null) {
            Debug.LogWarning("[UMod Guard] ExportSettings.asset が見つかりません。");
            return;
        }
        Restore(path, false);
    }

    [MenuItem("Tools/UMod Settings Guard/Open Backup Folder")]
    private static void MenuOpenFolder() {
        Directory.CreateDirectory(BackupDir);
        EditorUtility.RevealInFinder(BackupDir);
    }

    [MenuItem("Tools/UMod Settings Guard/Auto Restore")]
    private static void MenuToggleAutoRestore() {
        AutoRestore = !AutoRestore;
        Debug.Log("[UMod Guard] 自動復元: " + (AutoRestore ? "ON" : "OFF"));
    }

    [MenuItem("Tools/UMod Settings Guard/Auto Restore", true)]
    private static bool MenuToggleAutoRestoreValidate() {
        Menu.SetChecked("Tools/UMod Settings Guard/Auto Restore", AutoRestore);
        return true;
    }
}
#endif
