# 実機ログ解析 2回目

対象: `logs/diagnostic-0.1.1-second.log`（直下 LogOutput.log の保存コピー）。
診断版0.1.1 / ゲーム1.5.0 / Japanese。セッション開始は2026-09-17 07:50:53 JST。

## 結論

今回依頼した再採取は十分。パッチ登録6/6、マスタ163件（読取エラー0）、Layout 28件、
ShopSnapshot 22件、LiveModel 242件、モデル階層 Prefab 23件を取得した。
同じ操作での再採取は不要。実際の外見差し替えや効果不変の実証はまだ行っていない。

## 配置

FromSaveData 後（seq181）、編集開始後（seq239）、CloseBazaarCustom 後（seq601）の
current 配置11枠は同一。キャンセル後の current / edit（seq601/602）も一致する。
プレビューの表示変更が、少なくとも観測した current 配置の変更を必要としないことを支持する。
編集開始メソッドの同期 return 時点では edit が未準備なので、全編集過程を比較できたわけではない。

## モデル階層で確認できたこと

- 採取した Tent は Transform / MeshFilter / MeshRenderer の構成。
- 採取した OrnamentS も描画系の構成。
- OrnamentL には Animator / SkinnedMeshRenderer を持つものがある。
- OrnamentL の `fld_cst_043_01(Clone)` は BoxCollider を持つ。
- Shelf は BazaarShopShelf、BazaarShopItem、FieldSalePoint、複数Collider、NavMeshObstacle、
  UI等を含む。生成済み階層の観測なので、すべてが元Prefabに含まれていたかは未確定。
  ただし生成済みの棚オブジェクト丸ごとの差し替えは、販売機能や当たり判定を巻き込むことが分かった。

23件の階層に nodeLimitReached=true はない。深さ上限12で未探索となった枝の有無はこのフラグでは判定できない。
今回未採取のモデルに同じ構成を一般化しない。

## 残った診断エラー

`snapshot:ToSaveData` の DumpLayout 内で IL2CPP List のインデクサが NullReferenceException。
Guard が捕捉しており、その後の配置・モデル採取は継続している。
同じエラーは抑制されるため、ログ1件から実発生回数は判定できない。
初期化途中の読み取り、共有ネイティブ実装へのフック等を原因候補として調べる必要があり、原因は未確定。
この警告は診断の配置読取で発生したもので、ログ上はゲームの保存処理失敗を示すものではない。
次の実装では ToSaveData フックを外すか、観測対象・タイミングを絞り、安定した編集イベントで比較する。

## 実装への示唆

最初の試作は構造を確認した同カテゴリの小オブジェ1枠で行う。
棚や当たり判定付きモデルは、機能部分を保持して描画部分だけを扱う方法を検討する。
ID＋カテゴリ＋modelNameによる保存と照合方針は継続。
