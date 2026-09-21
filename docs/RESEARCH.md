# Bazaar Decor Transmog 調査

調査日: 2026-09-17

## 調査範囲と確度

対象はリポジトリ直下の `decompile_Assembly-CSharp_dll`。これは Il2CppInterop の生成ラッパーを逆コンパイルしたコードであり、ゲーム本体の C# 実装ではない。型・フィールド・メソッドシグネチャは確認できるが、`il2cpp_runtime_invoke` の先の処理、Prefab 内容、実際の UI 到達性は未確認。以下の「未発見」は機能の不存在を証明しない。ゲーム起動・セーブ変更・MOD 実装は行っていない。

ソース参照は同ディレクトリ内の `BokuMono/` を基準とし、行番号は今回の解析コードに対応する。

## 1. 見た目だけ差し替える既存機能

**専用の外見装備・Transmog 機能は未発見。ただしモデルだけを扱う API が存在する。**

- `BazaarMyShop.cs:1745` — `RefreshBazaarPartsModel(uint itemId, PartsCategory category, int index, bool isCreate, bool isEdit = false)`
- `BazaarMyShop.cs:1761` — `SetBazaarPartsModel(...)`
- `BazaarMyShop.cs:1894` — `LoadBazaarPartsModel(uint itemId, PartsCategory category, int index, Action<GameObject> succeeded, Action failed = null)`
- `ResourceManager.cs:7449` — `GetLoadCustomPartsModelData(string modelName, Action<bool, GameObject> endCallback, CacheLevel cacheLevel)`

配置データ側は `BazaarManager.SetCustomParts` (`BazaarManager.cs:11732`) が担当する別の入口を持つ。モデルロードの入力だけを置き換える方式が候補。既存の `isEdit` は編集状態に関する引数であり、外見専用設定を意味する根拠はない。

外見用 ID を保持する独立したフィールドは関連型で確認できなかった。MOD 側で「実際に置いているパーツ」と「表示用パーツ」を別管理する設計が必要と考えられる。

## 2. プリセット保存

**現在の配置保存と、記録用の配置スナップショットに相当する構造が存在する。名前付き複数プリセットの保存・適用機能は未発見。**

- `BazaarManager.cs:92` — `CustomData`。
- `BazaarManager.cs:106` — `List<PutPartsData> PutPartsDataDic`。
- `BazaarManager.cs:121` — `int BazaarShelfUpgradeCount`。
- `BazaarManager.cs:35,48` — `PutPartsData.Category` と `List<uint> DataDic`。名前は Dic だが実型はリスト。
- `BazaarManager.cs:9099,9114` — `customData` と `editCustomData`。
- `BazaarManager.cs:12008,12020` — `ToSaveData()` / `FromSaveData(CustomData, int currentDevelopLevel)`。
- `UserInfoSaveData.cs:872` — 保存データの `BazaarManager.CustomData CustomData`。
- `BazaarManager.cs:889` — `MaxPriceInfo.customData`。
- `BazaarManager.cs:1145` — `BazaarSaveData.maxPriceInfo`。
- `UIBazaarMaxPriceCustomLogPage.cs:258,273,303,318` — 効果詳細、パーツアイコン一覧、集計データを持つ記録ページ。

最高売上時の配置閲覧に使う構造と推定できる。ただし記録ページから配置を再適用できるかは、`DialogChoices()` やコールバックの本体が見えないため未確定。任意の複数プリセット管理の証拠にはならない。

`CustomData(CustomData saveData)` というコピー用らしいコンストラクタも存在する (`BazaarManager.cs:185`)。深いコピーかは未確認なので、MOD の保存では通常の C# データへ ID を明示的にコピーする方が検証しやすい。

検索で出る `GetPresetPrefererenceId` / `BazaarVisitorMasterData.PresetIdList` は来客・嗜好系の名前と型であり、配置プリセットの根拠ではない。

## 3. 未使用 UI・デバッグ機能

**デバッグ名の型は残存するが、バザール外見変更用の隠し UI は未発見。未使用かどうかは静的ラッパーだけでは判定できない。**

- `UICameraDebug.cs:12` — Sprite を設定・解除する小さなデバッグ UI。
- `UIDebugSceneChanger.cs:10` — 型とコンストラクタが残るが、このラッパーにはシーン選択機能のメソッドがない。
- `Debug/Wrapper.cs` — `DrawLine` / `DrawRay` など。
- `UIBazaarCustomPage` / `UIBazaarCustomDetail` / `UIBazaarEffectDetail` / `UICustomPartsListItem` は既存 UI の再利用候補。
- `UILoadKey.cs:25,60` — `BazaarCustom = 113` と `UIBazaarMaxPriceCustomLogPage = 148` が登録されている。

`UIBazaarCustomPage.cs:1280,1292` の `FocusCategoryBeforeModel` / `OnFocusIn` は選択中プレビューの調査候補。名前からプレビュー処理を推測できるが、呼び出し順序は実機で確認する。

`CallerCount(0)` は未使用の証明にしない。Unity イベント、仮想呼び出し、シリアライズされた参照などの有無は別途確認が必要。

## 4. Bazaar Decor 相当の内部構造

| 役割 | 型・主な要素 |
|---|---|
| パーツ定義 | `BokuMono.Data.CustomPartsMasterData : TableDataBase` |
| 定義取得 | `CustomPartsMaster.GetMasterData(uint id)` / `GetCategoryMasterData(PartsCategory)` |
| UI 用項目 | `BazaarCustomItemData : ItemIconData`。`PartsData`, `Category`, `PutIndex`, `IsPut`, `MainCategory`, `SubIndex` |
| 配置・編集・保存 | `BazaarManager.CustomData` / `PutPartsData` |
| 所持品 | `BazaarCustomPartsStorageManager`。配置保存とは別に `UserInfoSaveData.BazaarCustomPartsStorageSaveData` が存在 |
| 実体モデル | `BazaarMyShop`。`putPartsModelDic` はカテゴリ別の `GameObject[]` 相当 (`BazaarMyShop.cs:1431`) |
| 閉店時のモデル | `BazaarMyShopClosed.LoadParts` (`BazaarMyShopClosed.cs:468`) |
| 設置数など | `BazaarCustomSetting`。レベル別 `PutTentNum`, `PutShelfNum`, `PutOrnamentSNum`, `PutOrnamentLNum`, `PutOrnamentSpNum` |
| 効果集計 | `CustomPartsCompositeLevel`, `CustomPartsBuffParam`, `BokuMono.API.Bazaar` |

`BazaarCustomItemData.PartsCategory` (`BazaarCustomItemData.cs:14`) は `None=-1`, `Tent=0`, `Shelf=1`, `OrnamentS=2`, `OrnamentL=3`, `OrnamentSp=4`。日本語の「オブジェ」だけを対象とするなら Ornament 系が中心で、Decor 全体なら Tent / Shelf も含める必要がある。

`Data/BazaarCustomPageCategory.cs` ではテント、棚の左右中央、小オブジェ4位置、大オブジェ3位置、特殊オブジェが区別される。これは UI のカテゴリ定義であり、そのまま現在解放済みの設置数とみなさない。

## 5. 見た目 Prefab と効果の分離

**データ参照・API は分離されている。ただしパーツ ID が両者を結び付け、Prefab が視覚要素だけである保証はない。**

`Data/CustomPartsMasterData.cs` には以下が共存する。

| フィールド | 行 | 用途 |
|---|---:|---|
| `Category` | 30 | パーツカテゴリ |
| `SeriesCategory` | 43 | シリーズ分類 |
| `modelName` | 56 | モデル識別文字列 |
| `ScreenShotId` | 70 | 画像識別文字列 |
| `PassiveEffectId` | 84 | 効果マスタへの参照 |
| `Priority` | 97 | 優先順位値（具体的用途未確認） |
| `DlcId` | 110 | DLC ID |
| `IsBaseParts` | 123 | プロパティ（計算内容未確認） |

効果側は `CustomPartsPassiveEffectMasterData`、`CustomPartsSeriesEffectMasterData` を持ち、`API/Bazaar.cs:79,95` に配置リストからの `RequestCompositeCustomPartsEffect`、`:107,123` に `RequestCustomPartsBuffParams` が存在する。`BazaarManager.cs:12904` に `SetupPartsBuff()` もある。

モデル側には `ResourceManager.m_CustomPartsModelDatas` (`ResourceManager.cs:5992`) という文字列キーのキャッシュと、読込・解放・未使用モデルの除去 API がある。具体的な modelName の値、Addressables のアドレス、Prefab 階層はこのコードからは取得できていない。

注意点: `BazaarMyShop.InitializeBazaarPartsModel(category, prefab)` (`BazaarMyShop.cs:1881`) が存在し、`BazaarShopShelfRoot` には `SetShelfObject`, `SetOpenObjBazaarCollider`, `ChangeTent` がある。同じカテゴリでも棚の構造・容量・当たり判定等に影響する可能性がある。初回の実証は Ornament 同カテゴリから始め、Prefab 内コンポーネントを比較するのが妥当。

## 6. 保存向けの安定 ID

**第一候補はパーツマスタの `uint Id`。設置位置は `PartsCategory + index`。**

- `TableDataBase.cs:26` — `uint Id`。
- `CustomPartsMasterId.cs:3` — `enum CustomPartsMasterId : uint`。`None=0`、例として `119000`, `119001` 等の明示値。
- `Data/CustomPartsMaster.cs:42` — `GetMasterData(uint id)`。
- 配置保存自体が `List<uint>` を持ち、配置 API は `uint itemId` を受け取る。

したがって同じマスタ構成ではセッションをまたぐ保存に適した候補。アップデート間の不変性は保証できない。実際の ID とモデル名・表示名・カテゴリの対応は実機ダンプで確認する。

MOD の保存形式案（未実装）:

```text
schemaVersion
presets[]
  id              : MOD が生成するプリセット ID
  name            : ユーザー指定名
  slots[]
    category      : PartsCategory の名前
    index         : カテゴリ内設置位置
    visualPartsId : 表示用マスタ ID (uint)
```

外見だけのプリセットなら効果用 ID や `BazaarShelfUpgradeCount` は変更しない。ロード時には ID の存在、カテゴリ一致、現在の設置枠、DLC / モデル利用可否を検証し、不正な設定は元の外見へ戻す設計を推奨。空き枠と ID 0 の扱いは実機で確認する。

GameObject の InstanceID、ポインタ、UI 一覧順は永続キーにしない。`modelName` は診断情報には有用だが、保存の主キーにはマスタ ID を優先する。セーブごとに外見設定を分ける場合のセーブ識別子は今回未調査。

## 次の実装判断に必要な確認

1. 読取専用ログで ID・カテゴリ・modelName・PassiveEffectId・SeriesCategory と現在配置を取得する。
2. `LoadBazaarPartsModel` と `GetLoadCustomPartsModelData` の呼び出しを記録し、初期表示・編集プレビュー・確定・キャンセル時の経路を確認する。
3. 閉店時は `BazaarMyShopClosed.LoadParts` の別経路も記録する。`BazaarMyShop` だけのフックで全状態を処理できるとは限らない。
4. 同カテゴリの Ornament 1枠でモデル読込だけの置換を試し、元の配置 ID と集計された効果が変わらないことを比較する。
5. エリア再入場・セーブ再読込・閉店/開店・編集取消・キャッシュ解放で表示を確認する。

現段階の第一候補は、元の配置データとマスタを保ったまま、対象枠のモデルロードだけ表示用 ID / modelName に振り替える方式。非同期ロードのため共有マスタの `modelName` を一時書換えする設計は避け、読込引数やロード結果に介入できるかを先に検証する。
