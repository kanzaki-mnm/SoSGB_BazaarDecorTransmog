# 実機ログ解析 1回目

- ゲーム 1.5.0 / Unity 6000.0.28f1 / Japanese / 診断版 0.1.0
- セッション開始: 2026-09-16 22:38:44 UTC（日本時間 2026-09-17 07:38:44）
- 入力: `LogOutput.log`。保存コピー: `logs/diagnostic-0.1.0-first.log`
- SHA256: `6F6D118BD61CCF4CEA5E6CFB08FAF37A80C630BE6D3585DDEFA6CAAAD4202D8A`

## 確認できたこと

マスタ163件を errors=0 で取得。内訳は Tent 30、Shelf 30、OrnamentS 47、OrnamentL 56。
今回のマスタ一覧には OrnamentSp の行はなかった。カテゴリ自体が未使用であるとは断定しない。

全163件で同一IDの ItemMaster が見つかり、表示名とモデル名は空でなかった。
表示名・モデル名それぞれの重複は0件。したがってカテゴリ＋モデル名の重複も0件。
デコードした一覧を `catalog-1.5.0-ja.json` に保存した。

例:

| ID | 表示名 | modelName | PassiveEffectId |
|---:|---|---|---:|
| 119025 | みごとな風車柄のテント | fld_cst_009_02 | 10193 |
| 119023 | 風車柄のテント | fld_cst_009_00 | 10191 |

ModelRequest は85件、ResourceRequest は115件、ClosedModelRequest は6件。
モデル要求の初期に119025、後半に119023が記録されており、テント候補の変化が観測できる。
ただし配置記録が欠けているので、これだけで保存確定時点は断定しない。
リソース読込は通常モデル要求以外でも起こり得るため、直前の ModelRequest と自動的に一対一対応させない。

現バージョンでは「ID＋カテゴリ＋modelName」の保存方針を支持する結果。
将来のアップデート間の安定性は1バージョンのログでは証明できない。

## 診断側の不備と未取得情報

ManagerEvents の登録で `AmbiguousMatchException`。`BazaarManager.FromSaveData` に
`(CustomData, int)` と `(TrendData)` のオーバーロードがあり、名前だけで検索していたのが原因。
Ready は installed=4 / expected=5。ManagerEvent と Layout は0件で、現在配置・編集中配置の比較は未実施。

PrefabInspection は登録済みだが Prefab イベントは0件。入口が今回の経路で呼ばれない可能性や
IL2CPP の呼出し形態などがあり、理由は未確定。Prefab が空・視覚要素のみとは判断できない。

## 修正 0.1.1

- ManagerEvents の7メソッドすべてに引数型を指定。
- SetCustomCursor(PartsCategory, int) と CloseBazaarCustomMode の戻り時に
  putPartsModelDic を読み、既存 GameObject の階層・コンポーネントを採取する経路を追加。
- 生成済みモデルと初期化引数のログを source で区別。
- Release ビルド成功、警告0・エラー0。
- インストール済み Assembly-CSharp.dll のメタデータで、上記9メソッドの正確なシグネチャが
  それぞれ一つ存在することを Mono.Cecil で確認。実機での Harmony 登録成功は次の採取で確認する。

次回は差し替えDLLでセーブをロードし、装飾編集を開いて数枠のカーソルを移動、候補を表示してキャンセルするだけでよい。
Ready=6/6、Layout、ShopSnapshot、LiveModel、Prefab(source=live-instance) の有無を確認する。
