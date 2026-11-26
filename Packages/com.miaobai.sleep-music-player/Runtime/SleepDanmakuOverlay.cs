using System.Text;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// 眠境 GPU 高性能弹幕引擎：最高支持 10,000 条弹幕、分帧无卡顿 JSON 解析、
/// 80 槽位零 GC 对象池 + GPU 4 点片段描边（零 Outline 顶点膨胀、1 DrawCall 批处理、VR 双眼原生兼容）。
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SleepDanmakuOverlay : UdonSharpBehaviour
{
    #region 常量与配置

    private const string LogPrefix = "[眠境弹幕GPU]";
    private const int MaxDanmakuItems = 10000;
    private const int MaxPoolSlots = 80;
    private const int ParseBudgetPerFrame = 500;
    private const int ScrollLaneCount = 12;
    private const int FixedLaneCount = 4;
    private const float FixedDurationSec = 4.0f;
    private const float DefaultHalfW = 960f;
    private const float DefaultHalfH = 540f;
    private const float SeekJumpThresholdSec = 1.5f;

    [Header("GPU 弹幕材质（SleepSanctuary/DanmakuGPU：4点片段描边 + 单 DrawCall）")]
    public Material danmakuGpuMaterial;

    [Header("弹幕开关与显示调优")]
    [Tooltip("是否启用弹幕显示")]
    public bool enableDanmaku = true;

    [Tooltip("弹幕整体透明度")]
    [Range(0.1f, 1.0f)]
    public float danmakuOpacity = 0.90f;

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

    [Header("预生成对象池（80 槽位，运行期零 Instantiate / 零 Outline）")]
    public GameObject overlayRoot;
    public Text[] danmakuPool;

    #endregion

    #region 运行时数据与池状态

    private int _count;
    private float[] _times = new float[MaxDanmakuItems];
    private string[] _texts = new string[MaxDanmakuItems];
    private int[] _types = new int[MaxDanmakuItems];
    private int[] _colors = new int[MaxDanmakuItems];
    private int[] _sizes = new int[MaxDanmakuItems];

    private string _parsingJson = "";
    private int _parsingPos;
    private bool _isParsing;
    private bool _readyToPlay;
    private float _parseStartTime;

    private int _cursor;
    private float _lastSyncTime = -10f;
    private float _clock;
    private bool _poolInitialized;
    private int _nextSlotScan;
    private int _spawnedTotalCount;

    private float _canvasHalfW = DefaultHalfW;
    private float _canvasHalfH = DefaultHalfH;

    private RectTransform[] _slotRects;
    private bool[] _slotActive;
    private int[] _slotType;
    private int[] _slotLane;
    private float[] _slotSpawnClock;
    private float[] _slotDuration;
    private float[] _slotStartX;
    private float[] _slotSpeed;
    private float[] _slotY;
    private float[] _slotWidth;

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

    private void RefreshCanvasBounds()
    {
        RectTransform overlayRt = overlayRoot != null ? overlayRoot.GetComponent<RectTransform>() : GetComponent<RectTransform>();
        if (overlayRt != null)
        {
            Rect r = overlayRt.rect;
            if (r.width > 100f && r.height > 100f)
            {
                _canvasHalfW = r.width * 0.5f;
                _canvasHalfH = r.height * 0.5f;
            }
        }
    }

    private void InitPool()
    {
        if (_poolInitialized) return;
        _poolInitialized = true;

        RefreshCanvasBounds();

        int n = danmakuPool != null ? Mathf.Min(danmakuPool.Length, MaxPoolSlots) : 0;
        _slotRects = new RectTransform[n];
        _slotActive = new bool[n];
        _slotType = new int[n];
        _slotLane = new int[n];
        _slotSpawnClock = new float[n];
        _slotDuration = new float[n];
        _slotStartX = new float[n];
        _slotSpeed = new float[n];
        _slotY = new float[n];
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
        if (!enabled) HideAllSlots();
    }

    /// <summary>清空全部弹幕数据并隐藏所有槽位（不关闭挂载自身的 overlayRoot，防止 Udon 对象失活）。</summary>
    public void ClearDanmaku()
    {
        _count = 0;
        _cursor = 0;
        _isParsing = false;
        _readyToPlay = false;
        _parsingJson = "";
        _parsingPos = 0;
        _lastSyncTime = -10f;
        _clock = 0f;
        _spawnedTotalCount = 0;
        HideAllSlots();
    }

    private void HideAllSlots()
    {
        InitPool();
        if (_slotActive != null)
        {
            for (int i = 0; i < _slotActive.Length; i++)
            {
                _slotActive[i] = false;
                if (danmakuPool != null && i < danmakuPool.Length && danmakuPool[i] != null)
                {
                    danmakuPool[i].gameObject.SetActive(false);
                }
            }
        }
        ResetLanes();
    }

    /// <summary>分帧非阻塞加载弹幕 JSON（最高 10,000 条，完整排序后按精确时间戳发射）。</summary>
    public int LoadFromJson(string json)
    {
        if (overlayRoot != null && !overlayRoot.activeSelf) overlayRoot.SetActive(true);
        InitPool();
        RefreshCanvasBounds();
        ClearDanmaku();
        if (string.IsNullOrEmpty(json)) return 0;

        int arrayStart = FindDanmakuArrayStart(json);
        if (arrayStart < 0) return 0;

        _parsingJson = json;
        _parsingPos = arrayStart;
        _isParsing = true;
        _readyToPlay = false;
        _parseStartTime = Time.realtimeSinceStartup;

        StepIncrementalParse(ParseBudgetPerFrame);
        return _count;
    }

    /// <summary>同步解析全部弹幕（仅用于离线测试）。</summary>
    public int LoadFromJsonImmediate(string json)
    {
        if (overlayRoot != null && !overlayRoot.activeSelf) overlayRoot.SetActive(true);
        InitPool();
        RefreshCanvasBounds();
        ClearDanmaku();
        if (string.IsNullOrEmpty(json)) return 0;
        int arrayStart = FindDanmakuArrayStart(json);
        if (arrayStart < 0) return 0;

        _parsingJson = json;
        _parsingPos = arrayStart;
        _isParsing = true;
        _readyToPlay = false;
        _parseStartTime = Time.realtimeSinceStartup;
        StepIncrementalParse(MaxDanmakuItems);
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
            _readyToPlay = _count > 0;
            _lastSyncTime = -10f;
            RefreshCanvasBounds();

            if (enableDebugLog)
            {
                float ms = (Time.realtimeSinceStartup - _parseStartTime) * 1000f;
                Debug.Log(LogPrefix + " 解析排序完成 | 共 " + _count + " 条弹幕（上限 " + MaxDanmakuItems + "）| 耗时 " + ms.ToString("F1") + " ms | 画布半尺寸: " + _canvasHalfW.ToString("F0") + "x" + _canvasHalfH.ToString("F0"));
            }
        }
    }

    /// <summary>每帧同步播放器本地播放时间，驱动弹幕发射与平滑飞行。</summary>
    public void SyncPlayback(float localTime, bool isPlaying, bool isPaused)
    {
        if (!enableDanmaku) return;
        if (overlayRoot != null && !overlayRoot.activeSelf) overlayRoot.SetActive(true);
        InitPool();

        if (_isParsing)
        {
            StepIncrementalParse(ParseBudgetPerFrame);
            if (_isParsing) return;
        }
        if (!_readyToPlay || _count <= 0) return;

        // 首次定位或拖动进度条 / 中途进图：重置槽位并二分定位到当前时间前 0.5 秒
        if (_lastSyncTime < -5f || Mathf.Abs(localTime - _lastSyncTime) > SeekJumpThresholdSec)
        {
            HideAllSlots();
            _cursor = BinarySearchTime(Mathf.Max(0f, localTime - 0.5f));
            _lastSyncTime = localTime;
            return;
        }

        if (isPaused || !isPlaying)
        {
            _lastSyncTime = localTime;
            return;
        }

        _clock += Time.deltaTime;
        _lastSyncTime = localTime;

        UpdateActiveSlots();

        int spawnedThisFrame = 0;
        while (_cursor < _count && _times[_cursor] <= localTime && spawnedThisFrame < 8)
        {
            if (localTime - _times[_cursor] <= 1.5f)
            {
                SpawnDanmaku(_texts[_cursor], _types[_cursor], _colors[_cursor], _sizes[_cursor], localTime);
                spawnedThisFrame++;
            }
            _cursor++;
        }
    }

    #endregion

    #region 弹幕发射与轨道防重叠

    private void UpdateActiveSlots()
    {
        int n = _slotActive != null ? _slotActive.Length : 0;
        for (int i = 0; i < n; i++)
        {
            if (!_slotActive[i]) continue;
            float elapsed = _clock - _slotSpawnClock[i];
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

            if (_slotType[i] == 1 && _slotRects[i] != null)
            {
                float x = _slotStartX[i] - _slotSpeed[i] * elapsed;
                _slotRects[i].anchoredPosition = new Vector2(x, _slotY[i]);
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

        float width = Mathf.Max(60f, txt.preferredWidth + 20f);
        float height = pixelSize + 12f;
        rt.sizeDelta = new Vector2(width, height);

        if (mode == 1)
        {
            int lane = AllocateScrollLane();
            if (lane < 0) return;

            float usableHeight = (_canvasHalfH * 2f - 40f) * Mathf.Clamp(displayAreaRatio, 0.25f, 1.0f);
            float laneH = usableHeight / ScrollLaneCount;
            float y = _canvasHalfH - 24f - laneH * (lane + 0.5f);

            float dur = Mathf.Max(3.0f, scrollDuration);
            float startX = _canvasHalfW + width * 0.5f + 24f;
            float endX = -_canvasHalfW - width * 0.5f - 24f;
            float speed = (startX - endX) / dur;

            _slotActive[slot] = true;
            _slotType[slot] = 1;
            _slotLane[slot] = lane;
            _slotSpawnClock[slot] = _clock;
            _slotDuration[slot] = dur;
            _slotStartX[slot] = startX;
            _slotSpeed[slot] = speed;
            _slotY[slot] = y;
            _slotWidth[slot] = width;
            _scrollLaneOwner[lane] = slot;

            rt.anchoredPosition = new Vector2(startX, y);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.gameObject.SetActive(true);
        }
        else if (mode == 5)
        {
            int lane = AllocateFixedLane(_topLaneFreeAt, localTime);
            if (lane < 0) return;

            float y = _canvasHalfH - 36f - lane * 56f;
            _slotActive[slot] = true;
            _slotType[slot] = 5;
            _slotLane[slot] = lane;
            _slotSpawnClock[slot] = _clock;
            _slotDuration[slot] = FixedDurationSec;
            _slotY[slot] = y;
            _slotWidth[slot] = width;
            _topLaneFreeAt[lane] = localTime + FixedDurationSec;

            rt.anchoredPosition = new Vector2(0f, y);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.gameObject.SetActive(true);
        }
        else
        {
            int lane = AllocateFixedLane(_bottomLaneFreeAt, localTime);
            if (lane < 0) return;

            float y = -_canvasHalfH + 46f + lane * 56f;
            _slotActive[slot] = true;
            _slotType[slot] = 4;
            _slotLane[slot] = lane;
            _slotSpawnClock[slot] = _clock;
            _slotDuration[slot] = FixedDurationSec;
            _slotY[slot] = y;
            _slotWidth[slot] = width;
            _bottomLaneFreeAt[lane] = localTime + FixedDurationSec;

            rt.anchoredPosition = new Vector2(0f, y);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.gameObject.SetActive(true);
        }

        _spawnedTotalCount++;
        if (_spawnedTotalCount == 1 && enableDebugLog)
        {
            Debug.Log(LogPrefix + " 首条弹幕已上屏 | 内容: " + text);
        }
    }

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

            float elapsed = _clock - _slotSpawnClock[owner];
            float curX = _slotStartX[owner] - _slotSpeed[owner] * elapsed;
            float rightEdge = curX + _slotWidth[owner] * 0.5f;
            if (rightEdge < _canvasHalfW - 48f) return lane;
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
        if (idx >= 0)
        {
            int b = json.IndexOf('[', idx);
            if (b >= 0 && b + 1 < json.Length)
            {
                // 若 comments 为空数组 []，尝试回退到 danmaku 数组
                int nextNonSpace = b + 1;
                while (nextNonSpace < json.Length && (json[nextNonSpace] == ' ' || json[nextNonSpace] == '\n' || json[nextNonSpace] == '\r' || json[nextNonSpace] == '\t')) nextNonSpace++;
                if (nextNonSpace < json.Length && json[nextNonSpace] != ']') return b + 1;
            }
        }
        idx = json.IndexOf("\"danmaku\"");
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
