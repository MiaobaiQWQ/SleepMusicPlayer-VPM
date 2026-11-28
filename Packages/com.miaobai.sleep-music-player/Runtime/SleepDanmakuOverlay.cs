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

    // 弹幕类型常量（与主流 B 站 / 弹幕接口 ty 字段一致，方便后续扩展新模式）
    private const int ModeScroll = 1;
    private const int ModeBottom = 4;
    private const int ModeTop = 5;
    private const int ModeReverseScroll = 6;
    private const int ModeAdvanced = 7;

    [Header("GPU 弹幕材质（SleepSanctuary/DanmakuGPU：4点片段描边 + 单 DrawCall）")]
    public Material danmakuGpuMaterial;

    [Header("弹幕开关与显示调优")]
    [Tooltip("是否启用弹幕显示")]
    public bool enableDanmaku = true;

    [Header("分类型显示控制与屏蔽扩展（支持 UI 按钮/外部脚本动态切换）")]
    [Tooltip("是否显示滚动弹幕 (ty=1/2/3)")]
    public bool showScrollDanmaku = true;

    [Tooltip("是否显示顶部固定弹幕 (ty=5)")]
    public bool showTopDanmaku = true;

    [Tooltip("是否显示底部固定弹幕 (ty=4)")]
    public bool showBottomDanmaku = true;

    [Tooltip("是否显示逆向滚动弹幕 (ty=6)")]
    public bool showReverseDanmaku = true;

    [Tooltip("是否显示高级弹幕 (ty>=7)")]
    public bool showAdvancedDanmaku = true;

    [Tooltip("是否显示彩色弹幕（关闭后隐藏非白色弹幕）")]
    public bool showColorDanmaku = true;

    [Tooltip("可选：屏蔽关键词列表（包含任意关键词的弹幕将被自动过滤）")]
    public string[] blockedKeywords;

    [Tooltip("弹幕整体透明度")]
    [Range(0.1f, 1.0f)]
    public float danmakuOpacity = 0.90f;

    [Tooltip("滚动弹幕基础横穿时间（秒，越小越快）")]
    [Range(4.0f, 16.0f)]
    public float scrollDuration = 8.5f;

    [Tooltip("滚动速度倍率（0.4x 慢速 ~ 2.5x 极速，支持运行期实时调速）")]
    [Range(0.4f, 2.5f)]
    public float scrollSpeedMultiplier = 1.0f;

    [Tooltip("滚动弹幕占据屏幕上方区域比例（默认 0.75，避免遮挡底部字幕）")]
    [Range(0.25f, 1.0f)]
    public float displayAreaRatio = 0.75f;

    [Tooltip("全局字号缩放倍率（将接口 fs=25 映射为 1920x1080 画布清晰字号）")]
    [Range(0.6f, 2.5f)]
    public float fontSizeScale = 1.65f;

    [Tooltip("顶部/底部固定弹幕字号额外倍率")]
    [Range(0.6f, 2.0f)]
    public float fixedFontSizeScale = 1.0f;

    [Tooltip("防重叠轨道调度（关闭后高密度弹幕允许叠行显示不丢弃）")]
    public bool preventOverlap = true;

    [Tooltip("弹幕文字加粗")]
    public bool boldDanmakuText = true;

    [Tooltip("开启 GPU 4 点片段描边")]
    public bool enableOutline = true;

    [Tooltip("GPU 描边像素宽度")]
    [Range(0.5f, 3.0f)]
    public float outlineWidth = 1.6f;

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
        ApplyRuntimeVisualSettings();
        HideAllSlots();
    }

    /// <summary>刷新画布边界（兼容自适应 16:9 / 21:9 / 32:9 画布尺寸）。</summary>
    public void RefreshCanvasBounds()
    {
        RectTransform overlayRt = overlayRoot != null ? overlayRoot.GetComponent<RectTransform>() : GetComponent<RectTransform>();
        if (overlayRt != null)
        {
            Rect r = overlayRt.rect;
            if (r.width > 100f && r.height > 100f)
            {
                _canvasHalfW = r.width * 0.5f;
                _canvasHalfH = r.height * 0.5f;
                return;
            }
            if (overlayRt.sizeDelta.x > 100f && overlayRt.sizeDelta.y > 100f)
            {
                _canvasHalfW = overlayRt.sizeDelta.x * 0.5f;
                _canvasHalfH = overlayRt.sizeDelta.y * 0.5f;
                return;
            }
        }
        Canvas parentCanvas = GetComponentInParent<Canvas>();
        if (parentCanvas != null)
        {
            RectTransform crt = parentCanvas.GetComponent<RectTransform>();
            if (crt != null && crt.sizeDelta.x > 100f && crt.sizeDelta.y > 100f)
            {
                _canvasHalfW = crt.sizeDelta.x * 0.5f;
                _canvasHalfH = crt.sizeDelta.y * 0.5f;
            }
        }
    }

    /// <summary>同步曲面屏弯曲弧度与画布尺寸至弹幕 GPU 着色器（含屏幕边缘裁剪）。</summary>
    public void ApplyScreenCurvature(float curveDegrees, float stageWidth)
    {
        if (danmakuGpuMaterial == null) return;
        danmakuGpuMaterial.renderQueue = 3003;
        danmakuGpuMaterial.SetFloat("_CurveAngle", Mathf.Clamp(curveDegrees, -90f, 90f) * Mathf.Deg2Rad);
        danmakuGpuMaterial.SetFloat("_StageWidth", Mathf.Max(640f, stageWidth));
        danmakuGpuMaterial.SetFloat("_StageHeight", Mathf.Max(360f, _canvasHalfH * 2f));
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

    /// <summary>切换滚动弹幕显示状态。</summary>
    public void ToggleScrollDanmaku() { SetDanmakuTypeVisible(ModeScroll, !showScrollDanmaku); }

    /// <summary>切换顶部固定弹幕显示状态。</summary>
    public void ToggleTopDanmaku() { SetDanmakuTypeVisible(ModeTop, !showTopDanmaku); }

    /// <summary>切换底部固定弹幕显示状态。</summary>
    public void ToggleBottomDanmaku() { SetDanmakuTypeVisible(ModeBottom, !showBottomDanmaku); }

    /// <summary>切换高级弹幕显示状态。</summary>
    public void ToggleAdvancedDanmaku() { SetDanmakuTypeVisible(ModeAdvanced, !showAdvancedDanmaku); }

    /// <summary>切换彩色弹幕显示状态。</summary>
    public void ToggleColorDanmaku() { showColorDanmaku = !showColorDanmaku; }

    /// <summary>切换防重叠模式。</summary>
    public void TogglePreventOverlap() { preventOverlap = !preventOverlap; }

    /// <summary>切换文字加粗并立即同步在屏槽位。</summary>
    public void ToggleBoldText() { boldDanmakuText = !boldDanmakuText; ApplyRuntimeVisualSettings(); }

    /// <summary>切换 GPU 描边开关并立即同步材质。</summary>
    public void ToggleOutline() { enableOutline = !enableOutline; ApplyRuntimeVisualSettings(); }

    /// <summary>设置滚动弹幕速度倍率（0.4x ~ 2.5x）。</summary>
    public void SetScrollSpeedMultiplier(float multiplier)
    {
        scrollSpeedMultiplier = Mathf.Clamp(multiplier, 0.4f, 2.5f);
    }

    /// <summary>设置滚动弹幕基础耗时（4 ~ 16 秒）。</summary>
    public void SetScrollDuration(float durationSec)
    {
        scrollDuration = Mathf.Clamp(durationSec, 4.0f, 16.0f);
    }

    /// <summary>循环切换滚动速度预设（0.75x 慢速 → 1.0x 标准 → 1.35x 轻快 → 1.75x 高速）。</summary>
    public void CycleScrollSpeedPreset()
    {
        if (scrollSpeedMultiplier < 0.9f) scrollSpeedMultiplier = 1.0f;
        else if (scrollSpeedMultiplier < 1.2f) scrollSpeedMultiplier = 1.35f;
        else if (scrollSpeedMultiplier < 1.6f) scrollSpeedMultiplier = 1.75f;
        else scrollSpeedMultiplier = 0.75f;
    }

    /// <summary>设置全局弹幕字号缩放倍率（0.6x ~ 2.5x）。</summary>
    public void SetFontSizeScale(float scale)
    {
        fontSizeScale = Mathf.Clamp(scale, 0.6f, 2.5f);
    }

    /// <summary>循环切换弹幕字号预设（1.1x 小字 → 1.45x 标准 → 1.8x 偏大 → 2.15x 大字）。</summary>
    public void CycleFontSizePreset()
    {
        if (fontSizeScale < 1.3f) fontSizeScale = 1.45f;
        else if (fontSizeScale < 1.65f) fontSizeScale = 1.8f;
        else if (fontSizeScale < 2.0f) fontSizeScale = 2.15f;
        else fontSizeScale = 1.1f;
    }

    /// <summary>设置弹幕不透明度（0.1 ~ 1.0）并立即刷新在屏弹幕。</summary>
    public void SetDanmakuOpacity(float opacity)
    {
        danmakuOpacity = Mathf.Clamp(opacity, 0.1f, 1.0f);
        ApplyRuntimeVisualSettings();
    }

    /// <summary>设置弹幕显示区域占比（0.25 ~ 1.0）。</summary>
    public void SetDisplayAreaRatio(float ratio)
    {
        displayAreaRatio = Mathf.Clamp(ratio, 0.25f, 1.0f);
    }

    /// <summary>循环切换弹幕区域预设（0.25 顶部 → 0.50 半屏 → 0.75 避让字幕 → 1.00 全屏）。</summary>
    public void CycleDisplayAreaPreset()
    {
        if (displayAreaRatio < 0.38f) displayAreaRatio = 0.50f;
        else if (displayAreaRatio < 0.63f) displayAreaRatio = 0.75f;
        else if (displayAreaRatio < 0.88f) displayAreaRatio = 1.00f;
        else displayAreaRatio = 0.25f;
    }

    /// <summary>将透明度、加粗与 GPU 描边设置即时同步到材质与在屏槽位。</summary>
    public void ApplyRuntimeVisualSettings()
    {
        if (danmakuGpuMaterial != null)
        {
            danmakuGpuMaterial.SetFloat("_OutlineWidth", enableOutline ? Mathf.Clamp(outlineWidth, 0.5f, 3.0f) : 0f);
        }
        if (danmakuPool == null) return;
        FontStyle style = boldDanmakuText ? FontStyle.Bold : FontStyle.Normal;
        for (int i = 0; i < danmakuPool.Length; i++)
        {
            Text t = danmakuPool[i];
            if (t == null) continue;
            t.fontStyle = style;
            if (_slotActive != null && i < _slotActive.Length && _slotActive[i])
            {
                Color c = t.color;
                c.a = Mathf.Clamp(danmakuOpacity, 0.1f, 1.0f);
                t.color = c;
            }
        }
    }

    /// <summary>按类型编号显示/隐藏指定弹幕（1=滚动，4=底部，5=顶部，6=逆向滚动，7=高级弹幕），隐藏时立即清除屏幕上对应类型的在屏弹幕。</summary>
    public void SetDanmakuTypeVisible(int typeCode, bool visible)
    {
        int mode = NormalizeDanmakuMode(typeCode);
        if (mode == ModeScroll) showScrollDanmaku = visible;
        else if (mode == ModeTop) showTopDanmaku = visible;
        else if (mode == ModeBottom) showBottomDanmaku = visible;
        else if (mode == ModeReverseScroll) showReverseDanmaku = visible;
        else if (mode == ModeAdvanced) showAdvancedDanmaku = visible;

        if (!visible) HideActiveSlotsByType(mode);
    }

    /// <summary>立即隐藏屏幕上当前正在显示的所有指定类型弹幕。</summary>
    public void HideActiveSlotsByType(int typeCode)
    {
        if (_slotActive == null) return;
        int targetMode = NormalizeDanmakuMode(typeCode);
        for (int i = 0; i < _slotActive.Length; i++)
        {
            if (!_slotActive[i] || _slotType[i] != targetMode) continue;
            _slotActive[i] = false;
            if ((targetMode == ModeScroll || targetMode == ModeReverseScroll) &&
                _slotLane[i] >= 0 && _slotLane[i] < ScrollLaneCount && _scrollLaneOwner[_slotLane[i]] == i)
            {
                _scrollLaneOwner[_slotLane[i]] = -1;
            }
            if (danmakuPool != null && i < danmakuPool.Length && danmakuPool[i] != null)
            {
                danmakuPool[i].gameObject.SetActive(false);
            }
        }
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
                if ((_slotType[i] == ModeScroll || _slotType[i] == ModeReverseScroll) &&
                    _slotLane[i] >= 0 && _slotLane[i] < ScrollLaneCount && _scrollLaneOwner[_slotLane[i]] == i)
                {
                    _scrollLaneOwner[_slotLane[i]] = -1;
                }
                if (danmakuPool[i] != null) danmakuPool[i].gameObject.SetActive(false);
                continue;
            }

            if ((_slotType[i] == ModeScroll || _slotType[i] == ModeReverseScroll) && _slotRects[i] != null)
            {
                float x = _slotStartX[i] - _slotSpeed[i] * elapsed;
                _slotRects[i].anchoredPosition = new Vector2(x, _slotY[i]);
            }
            else if (_slotType[i] == ModeAdvanced && _slotRects[i] != null)
            {
                UpdateAdvancedSlotMotion(i, elapsed);
            }
        }
    }

    private int NormalizeDanmakuMode(int ty)
    {
        if (ty == ModeBottom || ty == ModeTop || ty == ModeReverseScroll) return ty;
        if (ty >= ModeAdvanced) return ModeAdvanced;
        return ModeScroll;
    }

    private bool ShouldDisplayDanmaku(string text, int ty, int colorInt)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int mode = NormalizeDanmakuMode(ty);
        if (mode == ModeScroll && !showScrollDanmaku) return false;
        if (mode == ModeTop && !showTopDanmaku) return false;
        if (mode == ModeBottom && !showBottomDanmaku) return false;
        if (mode == ModeReverseScroll && !showReverseDanmaku) return false;
        if (mode == ModeAdvanced && !showAdvancedDanmaku) return false;

        if (!showColorDanmaku && colorInt > 0 && colorInt != 16777215) return false;

        if (blockedKeywords != null)
        {
            for (int i = 0; i < blockedKeywords.Length; i++)
            {
                string kw = blockedKeywords[i];
                if (!string.IsNullOrEmpty(kw) && text.Contains(kw)) return false;
            }
        }
        return true;
    }

    private void SpawnDanmaku(string text, int ty, int colorInt, int fs, float localTime)
    {
        if (!ShouldDisplayDanmaku(text, ty, colorInt)) return;
        if (_slotActive == null || _slotActive.Length == 0) return;
        int slot = FindFreeSlotRing();
        if (slot < 0) return;

        Text txt = danmakuPool[slot];
        RectTransform rt = _slotRects[slot];
        if (txt == null || rt == null) return;

        int mode = NormalizeDanmakuMode(ty);
        float typeSizeScale = (mode == ModeTop || mode == ModeBottom) ? (fontSizeScale * fixedFontSizeScale) : fontSizeScale;
        int pixelSize = Mathf.Clamp(Mathf.RoundToInt((fs > 0 ? fs : 25) * typeSizeScale), 20, 88);
        txt.fontSize = pixelSize;
        txt.fontStyle = boldDanmakuText ? FontStyle.Bold : FontStyle.Normal;
        txt.text = text;
        txt.color = IntToColor(colorInt, danmakuOpacity);
        if (danmakuGpuMaterial != null && txt.material != danmakuGpuMaterial) txt.material = danmakuGpuMaterial;

        // 先激活文本再读取 preferredWidth，避免未激活状态下返回 0 导致长弹幕被截断或重叠
        txt.gameObject.SetActive(true);
        rt.localScale = Vector3.one;
        float estWidth = 0f;
        for (int ci = 0; ci < text.Length; ci++) estWidth += text[ci] > 127 ? pixelSize : pixelSize * 0.56f;
        float width = Mathf.Max(Mathf.Max(60f, estWidth + 24f), txt.preferredWidth + 24f);
        float height = pixelSize + 14f;
        rt.sizeDelta = new Vector2(width, height);

        if (mode == ModeScroll || mode == ModeReverseScroll)
        {
            int lane = AllocateScrollLane();
            if (lane < 0)
            {
                if (preventOverlap)
                {
                    txt.gameObject.SetActive(false);
                    return;
                }
                lane = _spawnedTotalCount % ScrollLaneCount;
            }

            float usableHeight = (_canvasHalfH * 2f - 40f) * Mathf.Clamp(displayAreaRatio, 0.25f, 1.0f);
            float laneH = usableHeight / ScrollLaneCount;
            float y = _canvasHalfH - 24f - laneH * (lane + 0.5f);

            float dur = Mathf.Clamp(scrollDuration / Mathf.Max(0.25f, scrollSpeedMultiplier), 2.0f, 24.0f);
            bool reverse = mode == ModeReverseScroll;
            float rightX = _canvasHalfW + width * 0.5f + 24f;
            float leftX = -_canvasHalfW - width * 0.5f - 24f;
            float startX = reverse ? leftX : rightX;
            float endX = reverse ? rightX : leftX;
            float speed = (startX - endX) / dur;

            _slotActive[slot] = true;
            _slotType[slot] = mode;
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
        else if (mode == ModeTop)
        {
            int lane = AllocateFixedLane(_topLaneFreeAt, localTime);
            if (lane < 0)
            {
                if (preventOverlap)
                {
                    txt.gameObject.SetActive(false);
                    return;
                }
                lane = _spawnedTotalCount % FixedLaneCount;
            }

            float y = _canvasHalfH - 36f - lane * 56f;
            _slotActive[slot] = true;
            _slotType[slot] = ModeTop;
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
        else if (mode == ModeBottom)
        {
            int lane = AllocateFixedLane(_bottomLaneFreeAt, localTime);
            if (lane < 0)
            {
                if (preventOverlap)
                {
                    txt.gameObject.SetActive(false);
                    return;
                }
                lane = _spawnedTotalCount % FixedLaneCount;
            }

            float y = -_canvasHalfH + 46f + lane * 56f;
            _slotActive[slot] = true;
            _slotType[slot] = ModeBottom;
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
        else
        {
            SpawnAdvancedDanmaku(slot, txt, rt, width);
        }

        _spawnedTotalCount++;
        if (_spawnedTotalCount == 1 && enableDebugLog)
        {
            Debug.Log(LogPrefix + " 首条弹幕已上屏 | 内容: " + text);
        }
    }

    // 高级弹幕扩展点（ty >= 7）：默认居中弹性弹出展示，后续可在此扩展路径动画/旋转/脚本化弹幕
    private void SpawnAdvancedDanmaku(int slot, Text txt, RectTransform rt, float width)
    {
        _slotActive[slot] = true;
        _slotType[slot] = ModeAdvanced;
        _slotLane[slot] = -1;
        _slotSpawnClock[slot] = _clock;
        _slotDuration[slot] = FixedDurationSec;
        _slotY[slot] = 0f;
        _slotWidth[slot] = width;

        rt.anchoredPosition = Vector2.zero;
        rt.localScale = Vector3.one;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.gameObject.SetActive(true);
    }

    // 高级弹幕每帧动态插值扩展点（入场弹性放大，退场平滑保持）
    private void UpdateAdvancedSlotMotion(int slot, float elapsed)
    {
        float t = Mathf.Clamp01(elapsed / 0.28f);
        float s = 0.75f + 0.25f * (t * (2f - t));
        _slotRects[slot].localScale = new Vector3(s, s, 1f);
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
