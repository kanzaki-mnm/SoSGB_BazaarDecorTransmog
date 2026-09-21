# 大オブジェ試作 0.6.1

## 0.6.1 操作状態の統合

- 独立していた `Appearance mode` と `Editor preview` を廃止し、`Transmog ON/OFF` に統合。
- Transmog ONで公式編集画面を開くと、自動的に外見編集として動作する。
- Transmog OFFでは公式一覧が通常どおり実配置と効果を編集する。
- 編集画面を閉じてもTransmog ONは維持され、次回も外見編集へ自動的に入る。
- 外見編集中のBackはTransmogをOFFにする。もう一度Backを押すと通常どおり画面を閉じる。
- 公式一覧の上に表示する案内欄は状態表示だけとし、独立したON/OFFボタンを置かない。

`OrnamentL` の3枠を外見指定の対象に追加。公式装飾一覧の Appearance mode と、
MODパネルの Large objects から選択できる。実配置・効果データは変更しない。

## 位置（実機確認済み）

- index 0: Left
- index 1: Right outside
- index 2: Right inside

実機で3枠すべての位置対応を確認済み。

## モデルの扱い

大オブジェには単純な `MeshRenderer` だけでなく、風車などの
`Animator + SkinnedMeshRenderer` 構成がある。0.6.0では次を許可する。

- Transform / MeshFilter / MeshRenderer
- SkinnedMeshRenderer / Animator
- BoxCollider / SphereCollider / CapsuleCollider / MeshCollider

外見Prefabを複製した後、複製側のColliderはすべて無効化する。実配置側のColliderとその他の
コンポーネントは残し、Rendererだけを隠す。ホワイトリスト外のコンポーネントを持つ外見は
安全のため適用しない。

## プリセット

schema 3で、小オブジェ4枠・テント1枠・大オブジェ3枠を同時保存できる。
schema 1/2は読み込み時にメモリ上で移行し、Save presetまではファイルを書き換えない。
保存時は従来どおり直前のファイルを `.bak` に残す。

## 実機確認

1. 大オブジェの3枠それぞれを選び、別々の外見を決定する。
2. 表示された位置とパネルの Left / Right outside / Right inside が一致するか確認する。
3. 静的な外見と、風車など動く外見を少なくとも1種類ずつ試す。
4. 編集画面と開店中の両方で外見を確認する。
5. Transmog OFF/ON、Appearance mode OFF/ON、Use vanillaで復元を確認する。
6. プリセットを保存し、再起動後に3枠が復元されるか確認する。
7. 新しい `LogOutput.log` をプロジェクト直下へコピーする。

確認ログは `TransmogRequest` / `TransmogApplied` の `category: OrnamentL`。
`unchanged: true` なら、配置IDと取得可能な効果値の前後比較が一致している。

ビルドは警告0・エラー0。ID解決、schema移行、8枠共存を含む管理コードテスト34件成功。
ゲーム内で3枠の位置、赤／青のミニ風車のアニメーション、再起動後のプリセット復元を確認済み。
v0.6.1ログでは `OrnamentL` が42要求・42適用、全件 `unchanged:true`、診断エラー0件。
