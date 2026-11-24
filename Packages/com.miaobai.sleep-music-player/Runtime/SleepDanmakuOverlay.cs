using System.Text;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// 眠境通用弹幕渲染引擎：兼容 /v3/vrc-danmaku 与 /v4/vrc 返回的 comments/danmaku 结构，
/// 采用零每帧分配对象池、多轨道防重叠排版、24位 RGB 颜色还原、暂停定格与拖动进度二分定位。
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SleepDanmakuOverlay : UdonSharpBehaviour
{
    #region 常量与配置

    private const string LogPrefix = "[眠境弹幕]";
    private const int MaxDanmakuItems = 2000;
    private const int ScrollLaneCount = 10;
    private const int FixedLaneCount = 4;
    private const float FixedDurationSec = 4.0f;
    private const float CanvasHalfW = 960f;
    private const float CanvasHalfH = 540f;
    private const float SeekJumpThresholdSec = 1.5f;

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

    [Tooltip("是否输出 VRChat 控制台中文弹幕日志")]
    public bool enableDebugLog = true;

    [Header("预生成对象池（由预制体自动绑定，运行期零 Instantiate）")]
    public GameObject overlayRoot;
    public Text[] danmakuPool;

    #endregion

    #region 运行时数据与池状态

    // 解析后的弹幕数组（按时间 t 升序）
    private int _count;
    private float[] _times = new float[0];
    private string[] _texts = new string[0];
    private int[] _types = new int[0];
    private int[] _colors = new int[0];
    private int[] _sizes = new int[0];

    private int _cursor;
    private float _lastSyncTime = -10f;
    private bool _poolInitialized;

    // 预缓存池对象 RectTransform 与槽位状态
    private RectTransform[] _slotRects;
    private bool[] _slotActive;
    private int[] _slotType;        // 1=滚动, 4=底部固定, 5=顶部固定
    private int[] _slotLane;
    private float[] _slotElapsed;
    private float[] _slotDuration;
    private float[] _slotStartX;
    private float[] _slotEndX;
    private float[] _slotWidth;

    // 轨道占用状态（防止弹幕互相重叠）
    private int[] _scrollLaneOwner = new int[ScrollLaneCount];
    private float[] _topLaneFreeAt = new float[FixedLaneCount];
    private float[] _bottomLaneFreeAt = new float[FixedLaneCount];

    public bool HasDanmaku => _count > 0;
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

        int n = danmakuPool != null ? danmakuPool.Length : 0;
        _slotRects = new RectTransform[n];
        _slotActive = new bool[n];
        _slotType = new int[n];
        _slotLane = new int[n];
        _slotElapsed = new float[n];
        _slotDuration = new float[n];
        _slotStartX = new float[n];
        _slotEndX = new float[n];
        _slotWidth = new float[n];

        for (int i = 0; i < n; i++)
        {
            if (danmakuPool[i] != null)
            {
                _slotRects[i] = danmakuPool[i].rectTransform;
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
        if (overlayRoot != null) overlayRoot.SetActive(enabled && _count > 0);
        if (!enabled) HideAllSlots();
    }

    /// <summary>清空全部弹幕数据并隐藏所有弹幕槽位。</summary>
    public void ClearDanmaku()
    {
        _count = 0;
        _cursor = 0;
        _lastSyncTime = -10f;
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
                if (danmakuPool != null && i < danmakuPool.Length && danmakuPool[i] != null)
                {
                    danmakuPool[i].gameObject.SetActive(false);
                }
            }
        }
        ResetLanes();
    }

    /// <summary>
    /// 解析弹幕接口 JSON（同时兼容 { "data": { "comments": [...] } } 与顶层 "comments"/"danmaku"）。
    /// 返回解析出的有效弹幕条数。
    /// </summary>
    public int LoadFromJson(string json)
    {
        InitPool();
        ClearDanmaku();
        if (string.IsNullOrEmpty(json)) return 0;

        float parseStart = Time.realtimeSinceStartup;
        int arrayStart = FindDanmakuArrayStart(json);
        if (arrayStart < 0) return 0;

        float[] tmpT = new float[MaxDanmakuItems];
        string[] tmpC = new string[MaxDanmakuItems];
        int[] tmpTy = new int[MaxDanmakuItems];
        int[] tmpCo = new int[MaxDanmakuItems];
        int[] tmpFs = new int[MaxDanmakuItems];
        int parsed = 0;

        int pos = arrayStart;
        int len = json.Length;
        while (pos < len && parsed < MaxDanmakuItems)
        {
            char ch = json[pos];
            if (ch == ']') break;
            if (ch != '{')
            {
                pos++;
                continue;
            }

            int objEnd = FindMatchingBrace(json, pos);
            if (objEnd <= pos) break;

            float t = ReadFieldFloat(json, pos, objEnd, "\"t\"", -1f);
            string c = ReadFieldString(json, pos, objEnd, "\"c\"");
            if (t >= 0f && !string.IsNullOrEmpty(c))
            {
                int ty = ReadFieldInt(json, pos, objEnd, "\"ty\"", 1);
                int co = ReadFieldInt(json, pos, objEnd, "\"co\"", 16777215);
                int fs = ReadFieldInt(json, pos, objEnd, "\"fs\"", 25);

                tmpT[parsed] = t;
                tmpC[parsed] = c;
                tmpTy[parsed] = ty;
                tmpCo[parsed] = co;
                tmpFs[parsed] = fs;
                parsed++;
            }
            pos = objEnd + 1;
        }

        if (parsed <= 0) return 0;

        _count = parsed;
        _times = new float[parsed];
        _texts = new string[parsed];
        _types = new int[parsed];
        _colors = new int[parsed];
        _sizes = new int[parsed];
        for (int i = 0; i < parsed; i++)
        {
            _times[i] = tmpT[i];
            _texts[i] = tmpC[i];
            _types[i] = tmpTy[i];
            _colors[i] = tmpCo[i];
            _sizes[i] = tmpFs[i];
        }

        EnsureSortedByTime();
        _cursor = 0;
        _lastSyncTime = -10f;
        if (overlayRoot != null) overlayRoot.SetActive(enableDanmaku);

        if (enableDebugLog)
        {
            float ms = (Time.realtimeSinceStartup - parseStart) * 1000f;
            Debug.Log(LogPrefix + " 解析完成 | 有效弹幕 " + _count + " 条 | 耗时 " + ms.ToString("F1") + " ms");
        }
        return _count;
    }

    /// <summary>每帧根据播放器本地播放时间与暂停状态驱动弹幕发射与移动。</summary>
    public void SyncPlayback(float localTime, bool isPlaying, bool isPaused)
    {
        if (!enableDanmaku || _count <= 0) return;
        InitPool();
        if (overlayRoot != null && !overlayRoot.activeSelf) overlayRoot.SetActive(true);

        // 拖动进度条或中途进图：重置活跃槽位并二分定位到当前时间点
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

        float dt = Time.deltaTime;
        _lastSyncTime = localTime;

        // 1) 推进现有活跃弹幕槽位
        UpdateActiveSlots(dt, localTime);

        // 2) 发射当前时间点到达的弹幕（单帧最多发射 6 条，防止瞬间峰值卡顿）
        int spawnedThisFrame = 0;
        while (_cursor < _count && _times[_cursor] <= localTime && spawnedThisFrame < 6)
        {
            // 仅发射距离当前时间 1.2 秒内的弹幕，避免过期积压
            if (localTime - _times[_cursor] <= 1.2f)
            {
                SpawnDanmaku(_texts[_cursor], _types[_cursor], _colors[_cursor], _sizes[_cursor], localTime);
                spawnedThisFrame++;
            }
            _cursor++;
        }
    }

    #endregion

    #region 弹幕发射与轨道防重叠排版

    private void UpdateActiveSlots(float dt, float localTime)
    {
        int n = _slotActive != null ? _slotActive.Length : 0;
        for (int i = 0; i < n; i++)
        {
            if (!_slotActive[i]) continue;
            _slotElapsed[i] += dt;
            float dur = _slotDuration[i];
            if (_slotElapsed[i] >= dur)
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
                float u = _slotElapsed[i] / dur;
                Vector2 p = _slotRects[i].anchoredPosition;
                p.x = Mathf.Lerp(_slotStartX[i], _slotEndX[i], u);
                _slotRects[i].anchoredPosition = p;
            }
        }
    }

    private void SpawnDanmaku(string text, int ty, int colorInt, int fs, float localTime)
    {
        if (danmakuPool == null || danmakuPool.Length == 0) return;
        int slot = FindFreeSlot();
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

        if (mode == 1)
        {
            int lane = AllocateScrollLane(width);
            if (lane < 0) return;

            float usableHeight = (CanvasHalfH * 2f - 40f) * Mathf.Clamp(displayAreaRatio, 0.25f, 1.0f);
            float laneH = usableHeight / ScrollLaneCount;
            float y = CanvasHalfH - 24f - laneH * (lane + 0.5f);

            float startX = CanvasHalfW + width * 0.5f + 24f;
            float endX = -CanvasHalfW - width * 0.5f - 24f;

            _slotActive[slot] = true;
            _slotType[slot] = 1;
            _slotLane[slot] = lane;
            _slotElapsed[slot] = 0f;
            _slotDuration[slot] = Mathf.Max(3.0f, scrollDuration);
            _slotStartX[slot] = startX;
            _slotEndX[slot] = endX;
            _slotWidth[slot] = width;
            _scrollLaneOwner[lane] = slot;

            rt.anchoredPosition = new Vector2(startX, y);
            txt.alignment = TextAnchor.MiddleLeft;
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
            _slotElapsed[slot] = 0f;
            _slotDuration[slot] = FixedDurationSec;
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

            float y = -CanvasHalfH + 46f + lane * 56f;
            _slotActive[slot] = true;
            _slotType[slot] = 4;
            _slotLane[slot] = lane;
            _slotElapsed[slot] = 0f;
            _slotDuration[slot] = FixedDurationSec;
            _slotWidth[slot] = width;
            _bottomLaneFreeAt[lane] = localTime + FixedDurationSec;

            rt.anchoredPosition = new Vector2(0f, y);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.gameObject.SetActive(true);
        }
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < _slotActive.Length; i++)
        {
            if (!_slotActive[i]) return i;
        }
        return -1;
    }

    // 检查轨道尾部弹幕是否已完全离开右边界，避免追尾重叠
    private int AllocateScrollLane(float newWidth)
    {
        for (int lane = 0; lane < ScrollLaneCount; lane++)
        {
            int owner = _scrollLaneOwner[lane];
            if (owner < 0 || !_slotActive[owner]) return lane;

            float u = _slotElapsed[owner] / Mathf.Max(0.1f, _slotDuration[owner]);
            float curX = Mathf.Lerp(_slotStartX[owner], _slotEndX[owner], u);
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

        // 仅在接口乱序时执行希尔排序（UdonSharp 零递归、低开销）
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
                if (ch == 'n') sb.Append(' ');
                else if (ch == 'r' || ch == 't') sb.Append(' ');
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
