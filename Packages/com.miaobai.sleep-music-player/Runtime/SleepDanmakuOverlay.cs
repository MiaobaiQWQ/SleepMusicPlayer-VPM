using System.Text;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// 眠境 GPU 高性能弹幕引擎：最高支持 10,000 条弹幕、分帧无卡顿 JSON 解析、
/// 80 槽位预分配对象池 + GPU 顶点驱动平滑滚动 + GPU 片段描边（零 Outline 顶点膨胀、零每帧 Canvas 重建、单 DrawCall 批处理）。
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SleepDanmakuOverlay : UdonSharpBehaviour
{
    #region 常量与配置

    private const string LogPrefix = "[眠境弹幕GPU]";
    private const string PropClock = "_DanmakuClock";
    private const string PropSlotAnim = "_SlotAnim";

    private const int MaxDanmakuItems = 10000;
    private const int MaxGpuSlots = 80;
    private const int ParseBudgetPerFrame = 450;
    private const int ScrollLaneCount = 12;
    private const int FixedLaneCount = 4;
    private const float FixedDurationSec = 4.0f;
    private const float CanvasHalfW = 960f;
    private const float CanvasHalfH = 540f;
    private const float SeekJumpThresholdSec = 1.5f;
    private const float SlotZStep = 0.05f;

    [Header("GPU 弹幕材质（使用 SleepSanctuary/DanmakuGPU，单 DrawCall + 零 Canvas 重建）")]
    public Material danmakuGpuMaterial;

    [Header("弹幕开关与显示调优")]
    [Tooltip("是否启用弹幕显示")]
    public bool enableDanmaku = true;

    [Tooltip("弹幕整体透明度")]
    [Range(0.1f, 1.0f)]
    public float danmakuOpacity = 0.88f;

    [Tooltip("滚动弹幕横穿整个屏幕所需时间（秒）")]
    [Range(4.0f, 16.0f)]
    public float scrollDuration = 8.5f;

    [Tooltip("滚动弹幕占据屏幕上方区域比例（默认 0.75，避免遮挡底部字幕）")]
    [Range(0.25f, 1.0f)]
    public float displayAreaRatio = 0.75f;

    [Tooltip("字号缩放倍率（将接口 fs=25 映射为 1920x1080 画布清晰字号）")]
    [Range(0.8f, 2.5f)]
    public float fontSizeScale = 1.65f;

    [Tooltip("是否输出 VRChat 控制台中文性能日志")]
    public bool enableDebugLog = true;

    [Header("预生成对象池（推荐 80 槽位，运行期零 Instantiate / 零 Outline）")]
    public GameObject overlayRoot;
    public Text[] danmakuPool;

    #endregion

    #region 运行时数据与 GPU 池状态

    // 固定复用缓冲（最高 10,000 条，加载新视频时零重复大数组分配）
    private int _count;
    private float[] _times = new float[MaxDanmakuItems];
    private string[] _texts = new string[MaxDanmakuItems];
    private int[] _types = new int[MaxDanmakuItems];
    private int[] _colors = new int[MaxDanmakuItems];
    private int[] _sizes = new int[MaxDanmakuItems];

    // 分帧增量解析状态（10,000 条弹幕分散到多帧解析，每帧 <0.5ms，VR 零掉帧）
    private string _parsingJson = "";
    private int _parsingPos;
    private bool _isParsing;
    private float _parseStartTime;

    private int _cursor;
    private float _lastSyncTime = -10f;
    private float _gpuClock;
    private bool _poolInitialized;
    private int _nextSlotScan;

    // GPU 槽位运动参数数组：x=startX, y=speedPxPerSec, z=spawnClock, w=isScroll(1/0)
    private Vector4[] _slotAnim = new Vector4[MaxGpuSlots];
    private bool _slotAnimDirty;

    // 预缓存池对象状态
    private RectTransform[] _slotRects;
    private bool[] _slotActive;
    private int[] _slotType;
    private int[] _slotLane;
    private float[] _slotSpawnClock;
    private float[] _slotDuration;
    private float[] _slotStartX;
    private float[] _slotSpeed;
    private float[] _slotWidth;

    // 轨道占用状态（防止弹幕互相重叠）
    private int[] _scrollLaneOwner = new int[ScrollLaneCount];
    private float[] _topLaneFreeAt = new float[FixedLaneCount];
    private float[] _bottomLaneFreeAt = new float[FixedLaneCount];

    public bool HasDanmaku => _count > 0 || _isParsing;
    public int LoadedCount => _count;

    #endregion

    #region 生命周期与公开接口

    private void Start()
    {
        InitPool();
        HideAllSlots();
    }

    private void InitPool()
    {
        if (_poolInitialized) return;
        _poolInitialized = true;

        int n = danmakuPool != null ? Mathf.Min(danmakuPool.Length, MaxGpuSlots) : 0;
        _slotRects = new RectTransform[n];
        _slotActive = new bool[n];
        _slotType = new int[n];
        _slotLane = new int[n];
        _slotSpawnClock = new float[n];
        _slotDuration = new float[n];
        _slotStartX = new float[n];
        _slotSpeed = new float[n];
        _slotWidth = new float[n];

        for (int i = 0; i < n; i++)
        {
            if (danmakuPool[i] != null)
            {
                _slotRects[i] = danmakuPool[i].rectTransform;
                if (danmakuGpuMaterial != null) danmakuPool[i].material = danmakuGpuMaterial;
                danmakuPool[i].gameObject.SetActive(false);
            }
        }
        ResetLanes();
    }

    private void ResetLanes()
    {
        for (int i = 0; i < ScrollLaneCount; i++) _scrollLaneOwner[i] = -1;
        for (int i = 0; i < FixedLaneCount; i++)
        {
            _topLaneFreeAt[i] = -100f;
            _bottomLaneFreeAt[i] = -100f;
        }
    }

    /// <summary>切换弹幕开关（可绑定至世界 UI 按钮）。</summary>
    public void ToggleDanmaku()
    {
        SetDanmakuEnabled(!enableDanmaku);
    }

    /// <summary>显式设置弹幕开关状态。</summary>
    public void SetDanmakuEnabled(bool enabled)
    {
        enableDanmaku = enabled;
        if (overlayRoot != null) overlayRoot.SetActive(enabled && HasDanmaku);
        if (!enabled) HideAllSlots();
    }

    /// <summary>清空全部弹幕数据并隐藏所有槽位。</summary>
    public void ClearDanmaku()
    {
        _count = 0;
        _cursor = 0;
        _isParsing = false;
        _parsingJson = "";
        _parsingPos = 0;
        _lastSyncTime = -10f;
        _gpuClock = 0f;
        HideAllSlots();
        if (overlayRoot != null) overlayRoot.SetActive(false);
    }

    private void HideAllSlots()
    {
        InitPool();
        if (_slotActive != null)
        {
            for (int i = 0; i < _slotActive.Length; i++)
            {
                _slotActive[i] = false;
                _slotAnim[i] = Vector4.zero;
                if (danmakuPool != null && i < danmakuPool.Length && danmakuPool[i] != null)
                {
                    danmakuPool[i].gameObject.SetActive(false);
                }
            }
            _slotAnimDirty = true;
        }
        ResetLanes();
        FlushGpuUniforms();
    }

    /// <summary>
    /// 分帧非阻塞加载弹幕 JSON（最高 10,000 条，首帧立即可播，剩余分帧解析，VR 零掉帧）。
    /// </summary>
    public int LoadFromJson(string json)
    {
        InitPool();
        ClearDanmaku();
        if (string.IsNullOrEmpty(json)) return 0;

        int arrayStart = FindDanmakuArrayStart(json);
        if (arrayStart < 0) return 0;

        _parsingJson = json;
        _parsingPos = arrayStart;
        _isParsing = true;
        _parseStartTime = Time.realtimeSinceStartup;

        // 首帧立即解析第一批（最多 450 条），确保视频开播瞬间即有弹幕
        StepIncrementalParse(ParseBudgetPerFrame);
        if (_count > 0 && overlayRoot != null) overlayRoot.SetActive(enableDanmaku);
        return _count;
    }

    /// <summary>同步解析全部弹幕（仅用于离线测试，运行期请用分帧版 LoadFromJson）。</summary>
    public int LoadFromJsonImmediate(string json)
    {
        InitPool();
        ClearDanmaku();
        if (string.IsNullOrEmpty(json)) return 0;
        int arrayStart = FindDanmakuArrayStart(json);
        if (arrayStart < 0) return 0;

        _parsingJson = json;
        _parsingPos = arrayStart;
        _isParsing = true;
        _parseStartTime = Time.realtimeSinceStartup;
        StepIncrementalParse(MaxDanmakuItems);
        if (_count > 0 && overlayRoot != null) overlayRoot.SetActive(enableDanmaku);
        return _count;
    }

    private void StepIncrementalParse(int budget)
    {
        if (!_isParsing || string.IsNullOrEmpty(_parsingJson)) return;

        string json = _parsingJson;
        int len = json.Length;
        int pos = _parsingPos;
        int parsedThisStep = 0;

        while (pos < len && _count < MaxDanmakuItems && parsedThisStep < budget)
        {
            char ch = json[pos];
            if (ch == ']')
            {
                pos = len;
                break;
            }
            if (ch != '{')
            {
                pos++;
                continue;
            }

            int objEnd = FindMatchingBrace(json, pos);
            if (objEnd <= pos)
            {
                pos = len;
                break;
            }

            float t = ReadFieldFloat(json, pos, objEnd, "\"t\"", -1f);
            string c = ReadFieldString(json, pos, objEnd, "\"c\"");
            if (t >= 0f && !string.IsNullOrEmpty(c))
            {
                _times[_count] = t;
                _texts[_count] = c;
                _types[_count] = ReadFieldInt(json, pos, objEnd, "\"ty\"", 1);
                _colors[_count] = ReadFieldInt(json, pos, objEnd, "\"co\"", 16777215);
                _sizes[_count] = ReadFieldInt(json, pos, objEnd, "\"fs\"", 25);
                _count++;
            }
            pos = objEnd + 1;
            parsedThisStep++;
        }

        _parsingPos = pos;

        if (pos >= len || _count >= MaxDanmakuItems)
        {
            _isParsing = false;
            _parsingJson = "";
            if (_count > 1) EnsureSortedByTime();
            if (_lastSyncTime >= 0f) _cursor = BinarySearchTime(_lastSyncTime);
            if (overlayRoot != null && _count > 0) overlayRoot.SetActive(enableDanmaku);

            if (enableDebugLog)
            {
                float ms = (Time.realtimeSinceStartup - _parseStartTime) * 1000f;
                Debug.Log(LogPrefix + " 分帧解析全部完成 | 共 " + _count + " 条弹幕（上限 " + MaxDanmakuItems + "）| 总跨度 " + ms.ToString("F1") + " ms | GPU 单 DrawCall 渲染就绪");
            }
        }
    }

    /// <summary>每帧驱动 GPU 时钟与弹幕发射（飞行期间零 RectTransform 移动、零 Canvas 重建）。</summary>
    public void SyncPlayback(float localTime, bool isPlaying, bool isPaused)
    {
        if (!enableDanmaku) return;
        InitPool();

        if (_isParsing) StepIncrementalParse(ParseBudgetPerFrame);
        if (_count <= 0) return;

        if (overlayRoot != null && !overlayRoot.activeSelf) overlayRoot.SetActive(true);

        // 拖动进度条或中途进图：清空活跃槽位并二分定位
        if (_lastSyncTime < -5f || Mathf.Abs(localTime - _lastSyncTime) > SeekJumpThresholdSec)
        {
            HideAllSlots();
            _cursor = BinarySearchTime(localTime);
            _lastSyncTime = localTime;
            return;
        }

        if (isPaused || !isPlaying)
        {
            _lastSyncTime = localTime;
            return;
        }

        _gpuClock += Time.deltaTime;
        _lastSyncTime = localTime;

        // 1) 仅回收已飞出屏幕或到期的槽位（飞行中绝不触碰 RectTransform！）
        RecycleExpiredSlots();

        // 2) 发射当前时间点到达的弹幕（单帧上限 8 条，保证极端弹幕雨也零卡顿）
        int spawnedThisFrame = 0;
        while (_cursor < _count && _times[_cursor] <= localTime && spawnedThisFrame < 8)
        {
            if (localTime - _times[_cursor] <= 1.2f)
            {
                SpawnDanmaku(_texts[_cursor], _types[_cursor], _colors[_cursor], _sizes[_cursor], localTime);
                spawnedThisFrame++;
            }
            _cursor++;
        }

        // 3) 仅推进 1 个 GPU float 时钟（有新弹幕发射时才更新一次 Vector4[]）
        FlushGpuUniforms();
    }

    private void FlushGpuUniforms()
    {
        if (danmakuGpuMaterial == null) return;
        danmakuGpuMaterial.SetFloat(PropClock, _gpuClock);
        if (_slotAnimDirty)
        {
            _slotAnimDirty = false;
            danmakuGpuMaterial.SetVectorArray(PropSlotAnim, _slotAnim);
        }
    }

    #endregion

    #region GPU 弹幕发射与轨道防重叠

    private void RecycleExpiredSlots()
    {
        int n = _slotActive != null ? _slotActive.Length : 0;
        bool useCpuFallback = danmakuGpuMaterial == null;

        for (int i = 0; i < n; i++)
        {
            if (!_slotActive[i]) continue;
            float elapsed = _gpuClock - _slotSpawnClock[i];
            if (elapsed >= _slotDuration[i])
            {
                _slotActive[i] = false;
                if (_slotType[i] == 1 && _slotLane[i] >= 0 && _slotLane[i] < ScrollLaneCount && _scrollLaneOwner[_slotLane[i]] == i)
                {
                    _scrollLaneOwner[_slotLane[i]] = -1;
                }
                if (danmakuPool[i] != null) danmakuPool[i].gameObject.SetActive(false);
                continue;
            }

            if (useCpuFallback && _slotType[i] == 1 && _slotRects[i] != null)
            {
                Vector3 p = _slotRects[i].anchoredPosition3D;
                p.x = _slotStartX[i] - _slotSpeed[i] * elapsed;
                _slotRects[i].anchoredPosition3D = p;
            }
        }
    }

    private void SpawnDanmaku(string text, int ty, int colorInt, int fs, float localTime)
    {
        if (_slotActive == null || _slotActive.Length == 0) return;
        int slot = FindFreeSlotRing();
        if (slot < 0) return;

        Text txt = danmakuPool[slot];
        RectTransform rt = _slotRects[slot];
        if (txt == null || rt == null) return;

        int mode = (ty == 4 || ty == 5) ? ty : 1;
        int pixelSize = Mathf.Clamp(Mathf.RoundToInt((fs > 0 ? fs : 25) * fontSizeScale), 26, 68);
        txt.fontSize = pixelSize;
        txt.text = text;
        txt.color = IntToColor(colorInt, danmakuOpacity);

        float width = Mathf.Max(60f, txt.preferredWidth + 16f);
        float height = pixelSize + 12f;
        rt.sizeDelta = new Vector2(width, height);

        float encodedZ = slot * SlotZStep;

        if (mode == 1)
        {
            int lane = AllocateScrollLane();
            if (lane < 0) return;

            float usableHeight = (CanvasHalfH * 2f - 40f) * Mathf.Clamp(displayAreaRatio, 0.25f, 1.0f);
            float laneH = usableHeight / ScrollLaneCount;
            float y = CanvasHalfH - 24f - laneH * (lane + 0.5f);

            float dur = Mathf.Max(3.0f, scrollDuration);
            float startX = CanvasHalfW + width * 0.5f + 24f;
            float endX = -CanvasHalfW - width * 0.5f - 24f;
            float speed = (startX - endX) / dur;

            _slotActive[slot] = true;
            _slotType[slot] = 1;
            _slotLane[slot] = lane;
            _slotSpawnClock[slot] = _gpuClock;
            _slotDuration[slot] = dur;
            _slotStartX[slot] = startX;
            _slotSpeed[slot] = speed;
            _slotWidth[slot] = width;
            _scrollLaneOwner[lane] = slot;

            // GPU 模式下 RectTransform 永远静止在 (0, y, encodedZ)，全部水平位移交给 GPU 顶点着色器！
            rt.anchoredPosition3D = new Vector3(danmakuGpuMaterial != null ? 0f : startX, y, encodedZ);
            _slotAnim[slot] = new Vector4(startX, speed, _gpuClock, 1f);
            _slotAnimDirty = true;

            txt.alignment = TextAnchor.MiddleCenter;
            txt.gameObject.SetActive(true);
        }
        else if (mode == 5)
        {
            int lane = AllocateFixedLane(_topLaneFreeAt, localTime);
            if (lane < 0) return;

            float y = CanvasHalfH - 36f - lane * 56f;
            _slotActive[slot] = true;
            _slotType[slot] = 5;
            _slotLane[slot] = lane;
            _slotSpawnClock[slot] = _gpuClock;
            _slotDuration[slot] = FixedDurationSec;
            _slotWidth[slot] = width;
            _topLaneFreeAt[lane] = localTime + FixedDurationSec;

            rt.anchoredPosition3D = new Vector3(0f, y, encodedZ);
            _slotAnim[slot] = new Vector4(0f, 0f, _gpuClock, 0f);
            _slotAnimDirty = true;

            txt.alignment = TextAnchor.MiddleCenter;
            txt.gameObject.SetActive(true);
        }
        else
        {
            int lane = AllocateFixedLane(_bottomLaneFreeAt, localTime);
            if (lane < 0) return;

            float y = -CanvasHalfH + 46f + lane * 56f;
            _slotActive[slot] = true;
            _slotType[slot] = 4;
            _slotLane[slot] = lane;
            _slotSpawnClock[slot] = _gpuClock;
            _slotDuration[slot] = FixedDurationSec;
            _slotWidth[slot] = width;
            _bottomLaneFreeAt[lane] = localTime + FixedDurationSec;

            rt.anchoredPosition3D = new Vector3(0f, y, encodedZ);
            _slotAnim[slot] = new Vector4(0f, 0f, _gpuClock, 0f);
            _slotAnimDirty = true;

            txt.alignment = TextAnchor.MiddleCenter;
            txt.gameObject.SetActive(true);
        }
    }

    // O(1) 均摊环形扫描查找空闲槽位
    private int FindFreeSlotRing()
    {
        int n = _slotActive.Length;
        for (int step = 0; step < n; step++)
        {
            int idx = (_nextSlotScan + step) % n;
            if (!_slotActive[idx])
            {
                _nextSlotScan = (idx + 1) % n;
                return idx;
            }
        }
        return -1;
    }

    private int AllocateScrollLane()
    {
        for (int lane = 0; lane < ScrollLaneCount; lane++)
        {
            int owner = _scrollLaneOwner[lane];
            if (owner < 0 || !_slotActive[owner]) return lane;

            float elapsed = _gpuClock - _slotSpawnClock[owner];
            float curX = _slotStartX[owner] - _slotSpeed[owner] * elapsed;
            float rightEdge = curX + _slotWidth[owner] * 0.5f;
            if (rightEdge < CanvasHalfW - 48f) return lane;
        }
        return -1;
    }

    private int AllocateFixedLane(float[] freeAt, float localTime)
    {
        for (int i = 0; i < FixedLaneCount; i++)
        {
            if (localTime >= freeAt[i]) return i;
        }
        return -1;
    }

    private Color IntToColor(int rgb, float alpha)
    {
        if (rgb <= 0) return new Color(1f, 1f, 1f, alpha);
        float r = ((rgb >> 16) & 255) / 255f;
        float g = ((rgb >> 8) & 255) / 255f;
        float b = (rgb & 255) / 255f;
        return new Color(r, g, b, alpha);
    }

    #endregion

    #region 排序、二分查找与轻量 JSON 解析

    private void EnsureSortedByTime()
    {
        bool sorted = true;
        for (int i = 1; i < _count; i++)
        {
            if (_times[i] < _times[i - 1]) { sorted = false; break; }
        }
        if (sorted) return;

        for (int gap = _count / 2; gap > 0; gap /= 2)
        {
            for (int i = gap; i < _count; i++)
            {
                float t = _times[i];
                string c = _texts[i];
                int ty = _types[i];
                int co = _colors[i];
                int fs = _sizes[i];
                int j = i;
                while (j >= gap && _times[j - gap] > t)
                {
                    _times[j] = _times[j - gap];
                    _texts[j] = _texts[j - gap];
                    _types[j] = _types[j - gap];
                    _colors[j] = _colors[j - gap];
                    _sizes[j] = _sizes[j - gap];
                    j -= gap;
                }
                _times[j] = t;
                _texts[j] = c;
                _types[j] = ty;
                _colors[j] = co;
                _sizes[j] = fs;
            }
        }
    }

    private int BinarySearchTime(float targetTime)
    {
        int lo = 0;
        int hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_times[mid] < targetTime) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private int FindDanmakuArrayStart(string json)
    {
        int idx = json.IndexOf("\"comments\"");
        if (idx < 0) idx = json.IndexOf("\"danmaku\"");
        if (idx < 0) return -1;
        int bracket = json.IndexOf('[', idx);
        return bracket >= 0 ? bracket + 1 : -1;
    }

    private int FindMatchingBrace(string json, int openPos)
    {
        bool inStr = false;
        bool esc = false;
        int depth = 0;
        int len = json.Length;
        for (int i = openPos; i < len; i++)
        {
            char ch = json[i];
            if (inStr)
            {
                if (esc) { esc = false; continue; }
                if (ch == '\\') { esc = true; continue; }
                if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private float ReadFieldFloat(string json, int start, int end, string key, float fallback)
    {
        int idx = json.IndexOf(key, start, end - start);
        if (idx < 0) return fallback;
        int colon = json.IndexOf(':', idx + key.Length);
        if (colon < 0 || colon >= end) return fallback;
        int p = colon + 1;
        while (p < end && (json[p] == ' ' || json[p] == '\t' || json[p] == '"')) p++;
        int s = p;
        while (p < end && (json[p] == '-' || json[p] == '.' || (json[p] >= '0' && json[p] <= '9'))) p++;
        if (p <= s) return fallback;
        float val;
        return float.TryParse(json.Substring(s, p - s), out val) ? val : fallback;
    }

    private int ReadFieldInt(string json, int start, int end, string key, int fallback)
    {
        int idx = json.IndexOf(key, start, end - start);
        if (idx < 0) return fallback;
        int colon = json.IndexOf(':', idx + key.Length);
        if (colon < 0 || colon >= end) return fallback;
        int p = colon + 1;
        while (p < end && (json[p] == ' ' || json[p] == '\t' || json[p] == '"')) p++;
        int s = p;
        while (p < end && (json[p] == '-' || (json[p] >= '0' && json[p] <= '9'))) p++;
        if (p <= s) return fallback;
        int val;
        return int.TryParse(json.Substring(s, p - s), out val) ? val : fallback;
    }

    private string ReadFieldString(string json, int start, int end, string key)
    {
        int idx = json.IndexOf(key, start, end - start);
        if (idx < 0) return "";
        int colon = json.IndexOf(':', idx + key.Length);
        if (colon < 0 || colon >= end) return "";
        int qStart = json.IndexOf('"', colon + 1);
        if (qStart < 0 || qStart >= end) return "";

        StringBuilder sb = new StringBuilder();
        bool esc = false;
        for (int i = qStart + 1; i < end; i++)
        {
            char ch = json[i];
            if (esc)
            {
                esc = false;
                if (ch == 'n' || ch == 'r' || ch == 't') sb.Append(' ');
                else if (ch == 'u' && i + 4 < end)
                {
                    int code;
                    if (int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                }
                else sb.Append(ch);
                continue;
            }
            if (ch == '\\') { esc = true; continue; }
            if (ch == '"') break;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    #endregion
}
