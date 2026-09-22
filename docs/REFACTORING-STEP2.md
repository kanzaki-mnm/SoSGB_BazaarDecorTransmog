# 第2段階: 調査コードの削除

2026-09-21。第1段階のテスト基盤を維持し、調査専用処理を削除。
保存形式、モデル適用方法、UIの遷移・待機タイミングは変更しない。

| 削除・置換 | 理由 | 置換先・残すもの | 回帰確認が必要な箇所 |
| --- | --- | --- | --- |
| PluginのCatalog / Snapshot / Inspect、未登録のManagerEvents等6種、空のPatch登録ループ | カタログ・実配置・モデル階層の採取専用。調査Patchは既に未登録 | 稼働中のモデル読込通知Patchは保持 | 編集終了、開店・閉店、マップ再入場でのモデル表示 |
| NativeEvidenceと呼出し、RenderInfo / RenderNode / Vector | 調査ログ以外の用途なし | SlotRuntime.Evidenceによる適用前後比較・不一致時Restoreは保持 | 実効果を変えず、見た目だけが変わること |
| 正常系のPlugin.Emitと引数生成 | 操作・適用・復元ごとの大量ログが不要 | 起動の1行、失敗/拒否/復旧のWarn、例外Guardは保持。閉店モデルの検証不一致も警告 | 障害時に警告が記録されること |
| 保存上書き/終了確認のID探索、文字列判別ヘルパー | 公式IDは特定済み。毎フレームの全dialog探索は不要 | 特定済み公式IDの参照を維持 | 公式Yes/Cancel/終了確認の選択肢 |
| PresetNameMasterProbeの登録とクラス、ReportNameMaster | 名前入力マスタの採取のみ | キーボード要求、専用本文、キャンセル許可Patchは保持 | 名前入力→保存、名前入力のキャンセル |
| ProbeStockRows、一覧ロード前後の構造採取 | レイアウトの調査は完了 | ProbeLoadStockRowsをEnsureObjectPreviewTemplateへ改名。公式prefabロードとロード完了時の再試行許可を保持 | 一覧初回表示、オブジェプレビュー、開き直し |
| ReportBanSprite、ClosedTent.ObserveState、アイコン色の採取 | 全スプライト/階層/コンポーネントを採取するだけ | 実配置追従マーク、白いシルエット、モデル安全検査は保持 | 実配置追従アイコンと閉店中の見た目 |
| UpdateIconTrial / ClearIconTrialと試験画像状態 | 試験画像生成は現行UIから呼ばれていない | 製品UIのActualIconVeilを維持 | アイコンの薄い表示が変わらないこと |
| Class1.cs | ビルド対象外の空テンプレート | 置換不要 | なし |

デバッグパネル、OnGUI、パネル用入力、実際に機能を担うProbeクラスは次段階以降の対象として残す。
稼働中のHarmony Patchで削除したのは、診断専用のPresetNameMasterProbeだけ。
保存・復旧アルゴリズムとschema 9は変更していない。保存層の通常ロード通知はホストで出力せず、復旧通知のみWarningとして出力する。

## 検証

- `dotnet build BazaarDecorTransmog/BazaarDecorTransmog.csproj -c Release --no-restore`: 警告0、エラー0。
- `dotnet run --project tests/IdentityChecks/IdentityChecks.csproj --no-restore -- catalog-1.5.0-ja.json`: 75項目成功。
- `git diff --check`: 問題なし。
- 削除した調査APIの参照残り、正常系Emit呼出しがないことを検索で確認。
- ゲーム内動作は未確認。上表の確認に加え、保存/読み込み/削除の完了通知を閉じられること、ホバー姿勢、空きスロット、一覧の位置とフッター復帰を確認する。

ビルド成果物は `BazaarDecorTransmog/bin/Release/net6.0/BazaarDecorTransmog.dll`。ゲーム側へのコピー・commit・pushは行っていない。バージョン番号は0.9.156のまま。
