using UnityEngine;

// Reusable "game feel" helper. Auto-bootstraps (no setup needed).
// Procedural BGM + SFX + visual pops + screen shake — all self-contained (no asset files).
// Games should call: Juice.Score(pos)/Juice.Hit()/Juice.Pop(pos,color)/Juice.Blip(freq) on events.
public static class Juice
{
    static AudioSource _sfx, _bgm;
    static Camera _cam;
    static Vector3 _appliedOffset;
    static float _shake;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("__Juice");
        Object.DontDestroyOnLoad(go);
        _sfx = go.AddComponent<AudioSource>(); _sfx.playOnAwake = false;
        _bgm = go.AddComponent<AudioSource>(); _bgm.loop = true; _bgm.volume = 0.20f; _bgm.playOnAwake = false;
        _bgm.clip = BuildBgm();
        _bgm.Play();                       // WebGL: resumes on first user input automatically
        go.AddComponent<JuiceRunner>();
    }

    public static void Blip(float freq, float dur = 0.08f, float vol = 0.4f)
    {
        if (_sfx != null) _sfx.PlayOneShot(Tone(freq, dur), vol);
    }
    public static void Score() { Blip(880f, 0.07f, 0.45f); Blip(1320f, 0.06f, 0.3f); }
    public static void Hit()   { Blip(150f, 0.2f, 0.5f); Shake(0.25f); }
    public static void Lose()  { Blip(110f, 0.45f, 0.5f); Shake(0.4f); }
    public static void Score(Vector3 pos) { Score(); Pop(pos, new Color(1f, 0.85f, 0.2f)); }

    public static void Shake(float amount) { _shake = Mathf.Max(_shake, amount); }

    public static void Pop(Vector3 worldPos, Color color, int count = 10)
    {
        var sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        for (int i = 0; i < count; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var col = q.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
            q.transform.position = worldPos;
            q.transform.localScale = Vector3.one * 0.18f;
            var mr = q.GetComponent<MeshRenderer>();
            mr.material = new Material(sh) { color = color };
            float ang = i * Mathf.PI * 2f / count;
            var vel = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * Random.Range(2f, 4f);
            q.AddComponent<JuiceParticle>().Init(vel, mr);
        }
    }

    static AudioClip Tone(float freq, float dur)
    {
        int sr = 44100; int n = Mathf.Max(1, (int)(sr * dur));
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t * 12f);
        }
        var c = AudioClip.Create("sfx", n, 1, sr, false); c.SetData(data, 0); return c;
    }

    static AudioClip BuildBgm()
    {
        int sr = 44100; float beat = 60f / 110f;
        float[] notes = { 220f, 277.18f, 329.63f, 277.18f, 246.94f, 329.63f, 220f, 196f };
        int total = (int)(sr * beat * notes.Length);
        var data = new float[total];
        for (int b = 0; b < notes.Length; b++)
        {
            int start = (int)(sr * beat * b), len = (int)(sr * beat);
            for (int i = 0; i < len && start + i < total; i++)
            {
                float t = (float)i / sr, env = Mathf.Exp(-t * 3f);
                float s = Mathf.Sin(2f * Mathf.PI * notes[b] * t) * 0.5f
                        + Mathf.Sin(2f * Mathf.PI * (notes[b] / 2f) * t) * 0.2f;
                data[start + i] = s * env * 0.6f;
            }
        }
        var c = AudioClip.Create("bgm", total, 1, sr, false); c.SetData(data, 0); return c;
    }

    class JuiceRunner : MonoBehaviour
    {
        void LateUpdate()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            _cam.transform.localPosition -= _appliedOffset;     // undo last frame's shake (no drift)
            if (_shake > 0.001f)
            {
                _appliedOffset = (Vector3)(Random.insideUnitCircle * _shake);
                _shake = Mathf.Lerp(_shake, 0f, Time.deltaTime * 8f);
            }
            else _appliedOffset = Vector3.zero;
            _cam.transform.localPosition += _appliedOffset;
        }
    }

    class JuiceParticle : MonoBehaviour
    {
        Vector3 _vel; MeshRenderer _mr; float _age, _life = 0.5f;
        public void Init(Vector3 vel, MeshRenderer mr) { _vel = vel; _mr = mr; }
        void Update()
        {
            _age += Time.deltaTime;
            transform.position += _vel * Time.deltaTime;
            transform.localScale *= 1f + Time.deltaTime * 1.5f;
            if (_mr != null) { var c = _mr.material.color; c.a = Mathf.Clamp01(1f - _age / _life); _mr.material.color = c; }
            if (_age >= _life) Destroy(gameObject);
        }
    }
}
