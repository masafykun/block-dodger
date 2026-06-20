using UnityEngine;

using System.Collections.Generic;

// ブロックよけ (Block Dodger) — コードからシーンを丸ごと構築する。
// .unity を手編集せず、RuntimeInitializeOnLoadMethod で自動生成する方針。
// Step 1: 正射影カメラ・地面・自機キューブを生成し、自機を左右移動できるようにする。
// Step 2: 上から赤いブロックを一定間隔で生成し、落下させ、画面外で破棄する。
// Step 3: 自機とブロックの当たり判定 → ゲームオーバー（落下停止）、R でリスタート。
public class BlockDodger : MonoBehaviour
{
    // プレイフィールドの大きさ（カメラ orthographicSize と 16:9 を基準に算出）。
    public const float OrthoSize = 6f;          // 縦半分の見える範囲
    public const float HalfWidth = OrthoSize * (16f / 9f); // 横半分 ≒ 10.67
    public const float PlayerY = -4.5f;         // 自機の高さ
    public const float PlayerSpeed = 12f;       // 横移動速度

    // ブロック生成・落下のパラメータ。難易度は生存時間で上昇する。
    public const float SpawnIntervalStart = 0.95f; // 開始時の出現間隔（秒）
    public const float SpawnIntervalMin = 0.32f;   // 最短の出現間隔
    public const float BlockFallSpeedStart = 6f;   // 開始時の落下速度
    public const float BlockFallSpeedMax = 13f;    // 最高落下速度
    public const float RampSeconds = 45f;          // ここまでで最高難易度に到達
    public const float SpawnY = OrthoSize + 1f;  // 出現高さ（画面上端の少し上）
    public const float DespawnY = -OrthoSize - 1f; // 破棄する高さ（画面下端の少し下）

    public const float BlockSize = 1.2f;        // ブロック/自機の一辺
    public const float HitShrink = 0.2f;        // 当たり判定をやや甘くする余白

    // ニアミス（際どい回避）判定の帯。当たり判定の外〜この距離を「すれすれ」とみなしボーナス。
    public const float NearBand = 2.1f;         // 中心間距離がこの内側＝ニアミス候補
    public const int NearBonus = 5;             // ニアミス1回のボーナス点

    Transform player;
    float spawnTimer;
    readonly List<Transform> blocks = new List<Transform>();
    // すでにニアミス加点したブロックを記録（多重加点を防ぐ）。
    readonly HashSet<Transform> nearScored = new HashSet<Transform>();

    bool gameOver;

    // スコア = 生存時間（秒）＋ ニアミスボーナス。ハイスコアはセッション中だけ保持する。
    float surviveTime;
    float bestTime;
    int nearMisses;     // 際どい回避の回数（ミクロ報酬）
    int bonusScore;     // ニアミスで稼いだ加点
    float Score => surviveTime + bonusScore;       // 総合スコア
    float bestScore;
    TextMesh scoreLabel; // ワールド空間のHUD（カメラ描画＝スクショに写る）

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("__BlockDodger");
        go.AddComponent<BlockDodger>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        BuildScene();
    }

    void BuildScene()
    {
        // テスト用の置物（"Cube"）が残っていれば撤去してビューをきれいにする。
        var stray = GameObject.Find("Cube");
        if (stray != null) Destroy(stray);

        // --- カメラ：正面・正射影でXY平面を見る ---
        var cam = Camera.main;
        if (cam == null)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            cam = camGo.AddComponent<Camera>();
        }
        cam.orthographic = true;
        cam.orthographicSize = OrthoSize;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.transform.rotation = Quaternion.identity;
        cam.backgroundColor = new Color(0.07f, 0.09f, 0.14f);
        cam.clearFlags = CameraClearFlags.SolidColor;

        // --- 地面（下端のライン） ---
        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        ground.transform.position = new Vector3(0f, PlayerY - 1f, 0f);
        ground.transform.localScale = new Vector3(HalfWidth * 2f, 0.4f, 1f);
        Paint(ground, new Color(0.25f, 0.28f, 0.35f));

        // --- 自機 ---
        var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
        p.name = "Player";
        p.transform.position = new Vector3(0f, PlayerY, 0f);
        p.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
        Paint(p, new Color(0.30f, 0.65f, 1f));
        player = p.transform;

        // --- スコアHUD（ワールド空間テキスト。左上に配置） ---
        scoreLabel = MakeText("ScoreHUD", new Vector3(-HalfWidth + 0.3f, OrthoSize - 0.3f, 0f), TextAnchor.UpperLeft);
        UpdateScoreLabel();
    }

    // ワールド空間の TextMesh を生成。orthoカメラに写るのでスクショに残る。
    TextMesh MakeText(string name, Vector3 pos, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * 0.18f;
        var tm = go.AddComponent<TextMesh>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        tm.font = font;
        tm.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        tm.fontSize = 64;
        tm.characterSize = 1f;
        tm.anchor = anchor;
        tm.color = Color.white;
        return tm;
    }

    // 生存時間に応じた難易度 0(開始)→1(最高)。RampSeconds で頭打ち。
    float Difficulty => Mathf.Clamp01(surviveTime / RampSeconds);

    float CurrentSpawnInterval => Mathf.Lerp(SpawnIntervalStart, SpawnIntervalMin, Difficulty);
    float CurrentFallSpeed => Mathf.Lerp(BlockFallSpeedStart, BlockFallSpeedMax, Difficulty);

    void UpdateScoreLabel()
    {
        if (scoreLabel != null)
            scoreLabel.text = string.Format("SCORE {0:0}\nBEST {1:0}\nNEAR {2}   LV {3}", Score, bestScore, nearMisses, 1 + Mathf.FloorToInt(Difficulty * 9f));
    }

    void Update()
    {
        if (player == null) return;

        if (gameOver)
        {
            // ゲームオーバー中は R でリスタートのみ受け付ける。
            if (Input.GetKeyDown(KeyCode.R)) Restart();
            return;
        }

        float dir = 0f;
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) dir -= 1f;
        if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) dir += 1f;

        var pos = player.position;
        pos.x += dir * PlayerSpeed * Time.deltaTime;
        float limit = HalfWidth - 0.6f; // 自機の半幅ぶん内側でクランプ
        pos.x = Mathf.Clamp(pos.x, -limit, limit);
        player.position = pos;

        surviveTime += Time.deltaTime; // 生き延びた時間がそのままスコア
        UpdateScoreLabel();

        UpdateBlocks();
        CheckCollisions();
    }

    // 自機と各ブロックの AABB 重なりを判定し、当たればゲームオーバーへ。
    void CheckCollisions()
    {
        float reach = BlockSize - HitShrink; // 2つの矩形の中心間距離がこれ未満なら接触
        var pp = player.position;
        foreach (var b in blocks)
        {
            if (b == null) continue;
            var bp = b.position;
            if (Mathf.Abs(pp.x - bp.x) < reach && Mathf.Abs(pp.y - bp.y) < reach)
            {
                gameOver = true;
                if (Score > bestScore) bestScore = Score; // ベスト更新
                UpdateScoreLabel();
                Paint(player.gameObject, new Color(0.5f, 0.5f, 0.55f)); // 被弾で灰色に
                Juice.Lose();              // 低音＋強めの画面シェイク
                Juice.Pop(pp, new Color(1f, 0.4f, 0.3f), 16); // 被弾の赤い飛散
                break;
            }
        }
    }

    // 全ブロックを消し、自機を中央へ戻して再開する。
    void Restart()
    {
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i] != null) Destroy(blocks[i].gameObject);
        }
        blocks.Clear();
        nearScored.Clear();
        spawnTimer = 0f;
        surviveTime = 0f;
        nearMisses = 0;
        bonusScore = 0;
        gameOver = false;
        player.position = new Vector3(0f, PlayerY, 0f);
        Paint(player.gameObject, new Color(0.30f, 0.65f, 1f));
        UpdateScoreLabel();
    }

    void OnGUI()
    {
        // 生存スコアのHUDは TextMesh(ワールド空間) 側で常時表示している。
        // ここではゲームオーバー時の中央メッセージだけを描く。
        if (!gameOver) return;

        // ゲームオーバー時は中央に結果とリスタート案内を表示。
        var center = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 32,
            fontStyle = FontStyle.Bold,
        };
        center.normal.textColor = Color.white;
        var rect = new Rect(0f, Screen.height * 0.5f - 60f, Screen.width, 120f);
        GUI.Label(rect, string.Format("GAME OVER\nScore {0:0}  Best {1:0}  Near {2}\nPress R to Restart", Score, bestScore, nearMisses), center);
    }

    // 一定間隔でブロックを生成し、毎フレーム落下させ、画面下で破棄する。
    void UpdateBlocks()
    {
        spawnTimer -= Time.deltaTime;
        if (spawnTimer <= 0f)
        {
            spawnTimer = CurrentSpawnInterval;
            SpawnBlock();
        }

        float fall = CurrentFallSpeed;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            if (b == null) { blocks.RemoveAt(i); continue; }
            var p = b.position;
            p.y -= fall * Time.deltaTime;
            b.position = p;
            // 落下中はゆっくり自転させて単調さを和らげる（見た目の調整）。
            b.Rotate(0f, 0f, 90f * Time.deltaTime, Space.Self);

            // ニアミス（際どい回避）＝ミクロ報酬。プレイヤーの高さを当たらずに通過し、
            // かつ横の中心間距離が「当たり判定の外〜NearBand」に収まったら 1回だけ加点。
            if (!gameOver && !nearScored.Contains(b) && p.y <= PlayerY)
            {
                float dx = Mathf.Abs(p.x - player.position.x);
                float reach = BlockSize - HitShrink;
                if (dx >= reach && dx < NearBand)
                {
                    nearScored.Add(b);
                    nearMisses++;
                    bonusScore += NearBonus;
                    Juice.Score(p);                 // 効果音＋黄色パーティクルが弾ける
                    UpdateScoreLabel();
                }
            }

            if (p.y < DespawnY)
            {
                nearScored.Remove(b);
                Destroy(b.gameObject);
                blocks.RemoveAt(i);
            }
        }
    }

    void SpawnBlock()
    {
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.name = "Block";
        float limit = HalfWidth - 0.6f;
        float x = Random.Range(-limit, limit);
        b.transform.position = new Vector3(x, SpawnY, 0f);
        b.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
        // 難易度が上がるほど橙→黄へ熱を帯びさせ、速さを色でも伝える。
        var hot = Difficulty;
        Paint(b, new Color(1f, 0.32f + 0.45f * hot, 0.30f - 0.20f * hot));
        blocks.Add(b.transform);
    }

    static void Paint(GameObject go, Color c)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;
        // URP/Built-in どちらでも壊れない単純な不透明マテリアル。
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var m = new Material(shader);
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        r.material = m;
    }
}
