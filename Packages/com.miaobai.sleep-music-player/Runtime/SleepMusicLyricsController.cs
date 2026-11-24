using System.Text;
using UdonSharp;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.Image;
using VRC.SDK3.Rendering;
using VRC.SDK3.StringLoading;
using VRC.SDK3.Video.Components.Base;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// 眠境音乐舞台控制器：负责音频接口拉取、GPU BC1 封面解码、双语歌词同步上色与 VRC 中文性能日志。
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SleepMusicLyricsController : UdonSharpBehaviour
{
    #region 常量定义（避免散落魔法字符串）

    private const string LogPrefix = "[眠境音乐]";
    private const string PropMainTex = "_MainTex";
    private const string PropHasCover = "_HasCover";
    private const string PropFlipFade = "_FlipFade";
    private const string PropStartX = "_StartX";
    private const string PropTextWidth = "_TextWidth";
    private const string PropSweep = "_Sweep";

    private const string KeySongName = "\"song_name\"";
    private const string KeyTitle = "\"title\"";
    private const string KeySinger = "\"singer\"";
    private const string KeyArtist = "\"artist\"";
    private const string KeyPlatform = "\"platform\"";
    private const string KeyDuration = "\"duration\"";
    private const string KeyLyrics = "\"lyrics\"";
    private const string KeyTransLyrics = "\"tlyrics\"";
    private const string KeyCoverUrl = "\"cover_url\"";
    private const string KeySongs = "\"songs\"";
    private const string KeyItems = "\"items\"";
    private const string KeySlotShort = "\"i\"";
    private const string KeySlotFull = "\"slot\"";
    private const string KeyTime = "\"time\"";
    private const string KeyText = "\"text\"";
    private const string KeyWords = "\"words\"";

    private const string PathSlotMarker = "/s/";
    private const string PathDirectMarker = "/v4/vrc";
    private const string PathSubmitMarker = "/p?";
    private const string PathExcludeMarker = "/sell/";
    private const string QueryUrlKey = "u=";

    private const float PollIntervalSec = 0.15f;
    private const float CoverFadeSpeed = 2.2f;
    private const float LineFlipSpeed = 9.0f;
    private const float ScrollTransitionDuration = 0.38f;
    private const float ScrollTravelPx = 88f;
    private const float TransMatchWindowSec = 0.65f;

    #endregion

    #region 接口与音乐池配置（可通过 Inspector 一键替换）

    [Header("接口与音乐池配置（支持面板一键替换）")]
    [Tooltip("接口基础域名，例如 https://api.kipfel.link")]
    public string apiDomain = "https://api.kipfel.link";

    [Tooltip("世界标识参数（world）")]
    public string worldId = "ktv";

    [Tooltip("音乐池名称参数（pool）")]
    public string poolName = "music";

    [Tooltip("音乐池烘焙槽位总数")]
    [Range(1, 500)]
    public int poolCapacity = 100;

    [Tooltip("直链输入前缀模板，支持 {world} / {pool}")]
    public string directPathTemplate = "/v4/vrc?world={world}&url=";

    [Tooltip("音乐池轮询路径模板，支持 {world} / {pool}")]
    public string pollPathTemplate = "/poll?world={world}&pool={pool}";

    [Tooltip("槽位音频路径模板，支持 {slot} / {world}")]
    public string slotPathTemplate = "/s/{slot}?world={world}";

    [Tooltip("槽位 GPU 封面路径模板，支持 {slot} / {world}")]
    public string coverPathTemplate = "/s/{slot}/cover-gpu.png?world={world}";

    #endregion

    #region 通用播放器与烘焙链接引用（支持任意 VRChat 视频播放器）

    [Header("通用播放器绑定（支持 VizVid / ProTV / Yama / IWsync / USharpVideo / 原生播放器）")]
    [Tooltip("可选：场景中任意播放器的核心控制脚本（UdonSharpBehaviour），用于自动读取当前播放链接与缓冲状态")]
    [FormerlySerializedAs("vvmwCore")]
    public UdonSharpBehaviour playerController;

    [Tooltip("可选：若使用纯 Udon Graph 播放器，可拖入其 UdonBehaviour")]
    public UdonBehaviour rawUdonPlayer;

    [Tooltip("推荐：拖入播放器底层的 VRCAVProVideoPlayer / VRCUnityVideoPlayer（可点击上方一键自动绑定）")]
    public BaseVRCVideoPlayer[] videoPlayers;

    [Tooltip("可选：自定义播放器中保存当前 VRCUrl 的 Udon 变量名（留空则自动兼容主流播放器）")]
    public string customUrlVarName = "";

    [Header("输入框与音乐池切歌桥接（可选）")]
    public VRCUrlInputField urlInputField;
    public GameObject urlInputTextObject;

    [Tooltip("可选：音乐池切歌时要触发的播放器提交目标 UdonBehaviour（留空则直接调用底层 VideoPlayer.PlayURL）")]
    public UdonBehaviour playerSubmitTarget;

    [Tooltip("可选：音乐池切歌时在提交目标上触发的自定义事件名（例如 OnUrlInput / _ChangeMedia / OnURLChanged）")]
    public string playerSubmitEvent = "";

    [Header("已烘焙的 VRCUrl 池（由上方设置一键生成）")]
    public VRCUrl fixedInputPrefix;
    public VRCUrl musicPollUrl;
    public VRCUrl[] musicSlotUrls;
    public VRCUrl[] coverGpuUrls;

    #endregion

    #region GPU 封面与歌词舞台引用

    [Header("GPU 封面与背景渲染")]
    public GameObject musicStageRoot;
    public Material bc1DecodeMaterial;
    public RenderTexture coverRenderTarget;
    public Material ambientBackdropMat;
    public Material albumCardMat;

    [Header("歌词与歌曲信息 UI")]
    public Text songTitleText;
    public Text songArtistText;
    public Text[] lyricLineTexts;
    public Text activeTranslationText;
    public Material activeLyricGpuMat;
    public Text statusBadgeText;
    [Tooltip("可选：左侧封面卡片与中线，纯音乐时自动居中展示（留空将自动按子物体名查找）")]
    public RectTransform coverCardRect;
    public GameObject centerDividerObject;

    [Header("同步、上色与日志调优")]
    [Tooltip("歌词时间补偿（微调可对齐本地音频缓冲，单位：秒）")]
    public float lyricTimeOffset = -0.05f;

    [Tooltip("无逐字时间轴时，单句最大上色占比（同时受字数自然唱速约束，防止间奏拖慢）")]
    [Range(0.2f, 1.0f)]
    public float sweepDurationRatio = 0.92f;

    [Tooltip("是否在 VRChat 控制台输出详细中文性能日志")]
    public bool enableVrcDebugLog = true;

    #endregion

    #region 运行时状态

    private VRCImageDownloader _imageDownloader;
    private IVRCImageDownload _pendingImage;

    private string _lastPlayedUrl = "";
    private VRCUrl _manualSubmittedUrl;
    private string _pendingStringUrl = "";
    private bool _pendingIsPollOrSubmit;
    private bool _hasMusicLyrics;
    private bool _hasCover;
    private float _coverBlend;

    // 性能计时点（使用 realtimeSinceStartup 精确记录毫秒耗时）
    private float _stringFetchStartTime;
    private float _coverFetchStartTime;
    private float _lineSweepStartTime;
    private bool _lineSweepFinishedLogged;

    // 解析后的歌曲与歌词缓存
    private string _songName = "";
    private string _singer = "";
    private float _duration;
    private int _lyricCount;
    private float[] _lyricTimes = new float[0];
    private string[] _lyricTexts = new string[0];
    private int _transCount;
    private float[] _transTimes = new float[0];
    private string[] _transTexts = new string[0];

    // 逐字时间轴扁平缓存（兼容 YRC / KRC 的 words: [[offsetMs, durMs, len], ...]）
    private bool _hasWordTimings;
    private int[] _wordStartIdx = new int[0];
    private int[] _wordCounts = new int[0];
    private float[] _wordOffsets = new float[0];
    private float[] _wordDurs = new float[0];
    private int[] _wordLens = new int[0];
    private int _wordTotalCount;

    // 当前高亮句的预计算字宽归一化区间（切行时计算一次，每帧零分配）
    private float[] _activeWordStartU = new float[128];
    private float[] _activeWordEndU = new float[128];
    private int _activeWordCount;

    // 切行与丝滑滚动过渡动画状态
    private int _currentLineIdx = -2;
    private float _flipProgress = 1f;
    private float _scrollProgress = 1f;
    private float _scrollDirection = -1f;
    private float _nextSyncCheck;

    // 预缓存 5 行歌词与翻译的基准布局坐标与透明度（用于平滑滚动插值，零每帧分配）
    private bool _layoutCached;
    private RectTransform[] _lineRects;
    private float[] _lineBaseY;
    private float[] _lineBaseAlpha;
    private RectTransform _transRect;
    private float _transBaseY;
    private float _transBaseAlpha;
    private float _activeExactWidth = 520f;

    // 左侧封面/标题基准 X（用于在「带歌词双栏」与「纯音乐居中唱片」之间动态切换）
    private float _leftColumnBaseX = -460f;
    private bool _isInstrumentalMode;
    private float _lastLocalTime = -1f;

    // JSON 解析临时缓冲
    private float[] _parseTimes;
    private string[] _parseTexts;
    private int _parseCount;
    private int[] _parseWordStarts;
    private int[] _parseWordCounts;
    private float[] _parseWordOffsets;
    private float[] _parseWordDurs;
    private int[] _parseWordLens;
    private int _parseWordTotal;

    #endregion

    #region 生命周期

    private void Start()
    {
        CacheLyricLayout();
        if (musicStageRoot != null) musicStageRoot.SetActive(false);
        SetCoverBlend(0f);
        RestoreFixedPrefixIfEmpty();
    }

    // 缓存 5 行歌词与翻译的初始位置与透明度，供切行滚动动画复用
    private void CacheLyricLayout()
    {
        if (_layoutCached) return;
        _layoutCached = true;

        if (lyricLineTexts != null && lyricLineTexts.Length > 0)
        {
            int n = lyricLineTexts.Length;
            _lineRects = new RectTransform[n];
            _lineBaseY = new float[n];
            _lineBaseAlpha = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (lyricLineTexts[i] == null) continue;
                _lineRects[i] = lyricLineTexts[i].rectTransform;
                _lineBaseY[i] = _lineRects[i].anchoredPosition.y;
                _lineBaseAlpha[i] = lyricLineTexts[i].color.a;
            }
        }

        if (activeTranslationText != null)
        {
            _transRect = activeTranslationText.rectTransform;
            _transBaseY = _transRect.anchoredPosition.y;
            _transBaseAlpha = activeTranslationText.color.a;
        }

        // 自动查找封面卡片与中线，支持纯音乐自动居中动态排版
        if (musicStageRoot != null)
        {
            Transform rootT = musicStageRoot.transform;
            if (coverCardRect == null)
            {
                Transform cardT = rootT.Find("GPU_AlbumCoverCard");
                if (cardT != null) coverCardRect = cardT.GetComponent<RectTransform>();
            }
            if (centerDividerObject == null)
            {
                Transform divT = rootT.Find("Divider_Vertical");
                if (divT != null) centerDividerObject = divT.gameObject;
            }
        }
        if (coverCardRect != null) _leftColumnBaseX = coverCardRect.anchoredPosition.x;
        else if (songTitleText != null) _leftColumnBaseX = songTitleText.rectTransform.anchoredPosition.x;
    }

    // 根据当前播放内容动态切换舞台形态：带歌词双栏 ↔ 纯音乐居中黑胶卡片
    private void ApplyContentLayoutMode(bool instrumental)
    {
        CacheLyricLayout();
        _isInstrumentalMode = instrumental;
        float targetX = instrumental ? 0f : _leftColumnBaseX;

        if (coverCardRect != null)
        {
            Vector2 p = coverCardRect.anchoredPosition;
            p.x = targetX;
            coverCardRect.anchoredPosition = p;
        }
        if (songTitleText != null)
        {
            Vector2 p = songTitleText.rectTransform.anchoredPosition;
            p.x = targetX;
            songTitleText.rectTransform.anchoredPosition = p;
        }
        if (songArtistText != null)
        {
            Vector2 p = songArtistText.rectTransform.anchoredPosition;
            p.x = targetX;
            songArtistText.rectTransform.anchoredPosition = p;
        }
        if (statusBadgeText != null)
        {
            Vector2 p = statusBadgeText.rectTransform.anchoredPosition;
            p.x = targetX;
            statusBadgeText.rectTransform.anchoredPosition = p;
        }
        if (centerDividerObject != null) centerDividerObject.SetActive(!instrumental);

        if (lyricLineTexts != null)
        {
            for (int i = 0; i < lyricLineTexts.Length; i++)
            {
                if (lyricLineTexts[i] != null) lyricLineTexts[i].gameObject.SetActive(!instrumental);
            }
        }
        if (activeTranslationText != null) activeTranslationText.gameObject.SetActive(!instrumental);
    }

    private void Update()
    {
        UpdateInputVisibility();

        // 定时检测播放器链接变化
        if (Time.time >= _nextSyncCheck)
        {
            _nextSyncCheck = Time.time + PollIntervalSec;
            PollPlayerState();
        }

        // 平滑淡入 GPU 封面与氛围背景
        float targetCover = _hasCover ? 1f : 0f;
        if (Mathf.Abs(_coverBlend - targetCover) > 0.002f)
        {
            _coverBlend = Mathf.MoveTowards(_coverBlend, targetCover, Time.deltaTime * CoverFadeSpeed);
            SetCoverBlend(_coverBlend);
        }

        // 严格跟随当前玩家本地播放位置与播放状态（支持任意底层播放器、中途进图、拖动进度条、暂停与缓冲提示）
        if (_hasMusicLyrics && HasAnyPlayerBound() && musicStageRoot != null && musicStageRoot.activeSelf)
        {
            bool playing = IsAnyPlayerPlaying();
            bool loading = IsAnyPlayerLoading();
            bool paused = IsAnyPlayerPaused();

            if (loading && !playing)
            {
                SetStatus("正在缓冲同步当前进度...");
            }
            else if (paused)
            {
                float pausedTime = GetPlaybackTime();
                if (Mathf.Abs(pausedTime - _lastLocalTime) > 0.05f)
                {
                    _lastLocalTime = pausedTime;
                    UpdateLyrics(pausedTime);
                }
            }
            else
            {
                float localTime = GetPlaybackTime();
                _lastLocalTime = localTime;
                if (!_isInstrumentalMode) SetStatus("");
                UpdateLyrics(localTime);
                UpdateLineFlipAnimation();
            }
        }
    }

    #endregion

    #region 通用播放器适配层（零第三方编译依赖）

    private bool HasAnyPlayerBound()
    {
        if (playerController != null || rawUdonPlayer != null) return true;
        if (videoPlayers != null)
        {
            for (int i = 0; i < videoPlayers.Length; i++)
            {
                if (videoPlayers[i] != null) return true;
            }
        }
        return false;
    }

    private BaseVRCVideoPlayer GetActiveVideoPlayer()
    {
        if (videoPlayers == null) return null;
        BaseVRCVideoPlayer readyFallback = null;
        for (int i = 0; i < videoPlayers.Length; i++)
        {
            BaseVRCVideoPlayer vp = videoPlayers[i];
            if (vp == null) continue;
            if (vp.IsPlaying) return vp;
            if (readyFallback == null && vp.IsReady) readyFallback = vp;
        }
        return readyFallback;
    }

    private object ReadPlayerVar(string varName)
    {
        if (string.IsNullOrEmpty(varName)) return null;
        if (playerController != null)
        {
            object v = playerController.GetProgramVariable(varName);
            if (v != null) return v;
        }
        if (rawUdonPlayer != null)
        {
            object v = rawUdonPlayer.GetProgramVariable(varName);
            if (v != null) return v;
        }
        return null;
    }

    private bool ReadPlayerBool(string varName)
    {
        object v = ReadPlayerVar(varName);
        return v != null && v.GetType() == typeof(bool) && (bool)v;
    }

    private float ReadPlayerFloat(string varName, float fallback)
    {
        object v = ReadPlayerVar(varName);
        if (v != null && v.GetType() == typeof(float)) return (float)v;
        return fallback;
    }

    private VRCUrl ReadPlayerUrlVar(string varName)
    {
        object v = ReadPlayerVar(varName);
        if (v != null && v.GetType() == typeof(VRCUrl))
        {
            VRCUrl u = (VRCUrl)v;
            if (!VRCUrl.IsNullOrEmpty(u)) return u;
        }
        return null;
    }

    private bool IsAnyPlayerPlaying()
    {
        BaseVRCVideoPlayer vp = GetActiveVideoPlayer();
        if (vp != null && vp.IsPlaying) return true;
        return ReadPlayerBool("isPlaying") || ReadPlayerBool("_isPlaying") || ReadPlayerBool("playing");
    }

    private bool IsAnyPlayerReady()
    {
        BaseVRCVideoPlayer vp = GetActiveVideoPlayer();
        if (vp != null && vp.IsReady) return true;
        return ReadPlayerBool("isReady") || ReadPlayerBool("_isReady");
    }

    private bool IsAnyPlayerLoading()
    {
        return ReadPlayerBool("isLoading") || ReadPlayerBool("_isLoading") || ReadPlayerBool("loading");
    }

    private bool IsAnyPlayerPaused()
    {
        BaseVRCVideoPlayer vp = GetActiveVideoPlayer();
        if (vp != null)
        {
            if (vp.IsPlaying) return false;
            if (vp.IsReady && vp.GetTime() > 0.01f) return true;
        }
        return ReadPlayerBool("isPaused") || ReadPlayerBool("paused") || ReadPlayerBool("_paused");
    }

    private float GetPlaybackTime()
    {
        BaseVRCVideoPlayer vp = GetActiveVideoPlayer();
        if (vp != null) return vp.GetTime();

        float t = ReadPlayerFloat("time", -1f);
        if (t >= 0f) return t;
        t = ReadPlayerFloat("currentTime", -1f);
        if (t >= 0f) return t;
        t = ReadPlayerFloat("videoTime", -1f);
        if (t >= 0f) return t;
        t = ReadPlayerFloat("_time", -1f);
        return t >= 0f ? t : 0f;
    }

    // 兼容主流 VRChat 播放器（VizVid / ProTV / YamaPlayer / USharpVideo / IWsync / 自定义）自动读取当前 VRCUrl
    private VRCUrl GetCurrentPlayerUrl()
    {
        if (!string.IsNullOrEmpty(customUrlVarName))
        {
            VRCUrl custom = ReadPlayerUrlVar(customUrlVarName);
            if (!VRCUrl.IsNullOrEmpty(custom)) return custom;
        }

        VRCUrl u = ReadPlayerUrlVar("localUrl");
        if (!VRCUrl.IsNullOrEmpty(u)) return u;
        u = ReadPlayerUrlVar("url");
        if (!VRCUrl.IsNullOrEmpty(u)) return u;
        u = ReadPlayerUrlVar("syncedURL");
        if (!VRCUrl.IsNullOrEmpty(u)) return u;
        u = ReadPlayerUrlVar("_syncedUrl");
        if (!VRCUrl.IsNullOrEmpty(u)) return u;
        u = ReadPlayerUrlVar("syncedUrl");
        if (!VRCUrl.IsNullOrEmpty(u)) return u;
        u = ReadPlayerUrlVar("lastUrl");
        if (!VRCUrl.IsNullOrEmpty(u)) return u;

        if (!VRCUrl.IsNullOrEmpty(_manualSubmittedUrl)) return _manualSubmittedUrl;
        return VRCUrl.Empty;
    }

    // 通用切歌：优先通过已绑定的播放器输入框/事件提交，否则直接调用底层 BaseVRCVideoPlayer.PlayURL
    private bool PlayTargetUrl(VRCUrl targetUrl)
    {
        if (VRCUrl.IsNullOrEmpty(targetUrl)) return false;
        _manualSubmittedUrl = targetUrl;

        if (playerSubmitTarget != null && !string.IsNullOrEmpty(playerSubmitEvent))
        {
            if (urlInputField != null) urlInputField.SetUrl(targetUrl);
            playerSubmitTarget.SendCustomEvent(playerSubmitEvent);
            return true;
        }

        if (videoPlayers != null)
        {
            for (int i = 0; i < videoPlayers.Length; i++)
            {
                if (videoPlayers[i] != null)
                {
                    videoPlayers[i].PlayURL(targetUrl);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>供外部自定义播放器主动推送当前播放的 VRCUrl（无需依赖变量反射）。</summary>
    public void SetExternalUrl(VRCUrl url)
    {
        _manualSubmittedUrl = url;
        if (VRCUrl.IsNullOrEmpty(url))
        {
            ClearMusicStage();
            return;
        }
        string urlText = url.Get();
        if (urlText != _lastPlayedUrl)
        {
            _lastPlayedUrl = urlText;
            TryActivateMusicStage(url, urlText);
        }
    }

    #endregion

    #region 播放器监听与网络拉取

    /// <summary>输入框提交回调：捕获用户输入的链接并触发单曲解析或音乐舞台激活。</summary>
    public void OnUrlInputSubmitted()
    {
        if (urlInputField == null) return;
        VRCUrl typed = urlInputField.GetUrl();
        if (VRCUrl.IsNullOrEmpty(typed)) return;

        string url = typed.Get();
        if (string.IsNullOrEmpty(url)) return;

        _manualSubmittedUrl = typed;
        if (url.Contains(PathSubmitMarker) && url.Contains(QueryUrlKey) && !url.EndsWith(QueryUrlKey))
        {
            StartStringFetch(typed, true, "正在解析单曲...");
        }
    }

    /// <summary>手动拉取当前音乐池中的最新单曲。</summary>
    public void PollLatestMusic()
    {
        if (VRCUrl.IsNullOrEmpty(musicPollUrl)) return;
        StartStringFetch(musicPollUrl, true, "正在同步音乐池...");
    }

    // 播放时自动隐藏输入框文字，避免与底部时间进度条重叠
    private void UpdateInputVisibility()
    {
        if (urlInputField == null) return;
        bool busy = HasAnyPlayerBound() && (IsAnyPlayerLoading() || IsAnyPlayerPlaying() || IsAnyPlayerPaused());
        bool focused = urlInputField.isFocused;

        if (urlInputTextObject != null)
        {
            bool visible = focused || !busy;
            if (urlInputTextObject.activeSelf != visible) urlInputTextObject.SetActive(visible);
        }

        if (!busy || focused) RestoreFixedPrefixIfEmpty();
    }

    // 输入框为空时自动补回直链前缀
    private void RestoreFixedPrefixIfEmpty()
    {
        if (urlInputField == null || VRCUrl.IsNullOrEmpty(fixedInputPrefix)) return;
        VRCUrl current = urlInputField.GetUrl();
        if (VRCUrl.IsNullOrEmpty(current) || string.IsNullOrEmpty(current.Get()))
        {
            urlInputField.SetUrl(fixedInputPrefix);
        }
    }

    // 监听任意播放器播放状态与链接切换
    private void PollPlayerState()
    {
        if (!HasAnyPlayerBound()) return;

        bool stopped = !IsAnyPlayerLoading() && !IsAnyPlayerPlaying() && !IsAnyPlayerPaused() && !IsAnyPlayerReady();
        if (stopped)
        {
            if (_hasMusicLyrics || _hasCover || !string.IsNullOrEmpty(_lastPlayedUrl)) ClearMusicStage();
            _manualSubmittedUrl = VRCUrl.Empty;
            return;
        }

        VRCUrl currentUrl = GetCurrentPlayerUrl();
        string urlText = VRCUrl.IsNullOrEmpty(currentUrl) ? "" : currentUrl.Get();
        if (urlText == _lastPlayedUrl) return;

        _lastPlayedUrl = urlText;
        if (string.IsNullOrEmpty(urlText))
        {
            ClearMusicStage();
            return;
        }

        TryActivateMusicStage(currentUrl, urlText);
    }

    // 识别音乐接口链接并同时拉取歌词 JSON 与 GPU 封面（遇到常见视频站点直链立即隐身，绝不遮挡视频）
    private void TryActivateMusicStage(VRCUrl currentUrl, string urlText)
    {
        if (IsKnownVideoUrl(urlText))
        {
            ClearMusicStage();
            return;
        }

        int slotIdx = ExtractSlotIndex(urlText);
        bool isDirect = urlText.Contains(PathDirectMarker);
        bool isSlot = slotIdx >= 0 || (urlText.Contains(PathSlotMarker) && !urlText.Contains(PathExcludeMarker));

        if (!isDirect && !isSlot)
        {
            ClearMusicStage();
            return;
        }

        _hasMusicLyrics = false;
        _hasCover = false;
        _currentLineIdx = -2;
        SetCoverBlend(0f);

        // 直链模式先等待 JSON 确认有歌词后再显示，避免误盖普通视频画面
        if (musicStageRoot != null) musicStageRoot.SetActive(!isDirect);

        LogInfo("切歌", "开始拉取歌曲信息 | 槽位: " + (slotIdx >= 0 ? "#" + slotIdx : "直链") + " | 链接: " + urlText);
        StartStringFetch(currentUrl, false, "正在加载歌词与封面...");

        if (slotIdx >= 0 && coverGpuUrls != null && slotIdx < coverGpuUrls.Length && !VRCUrl.IsNullOrEmpty(coverGpuUrls[slotIdx]))
        {
            StartCoverFetch(coverGpuUrls[slotIdx]);
        }
    }

    // 统一发起文本接口拉取并记录起始耗时
    private void StartStringFetch(VRCUrl url, bool isPollOrSubmit, string statusText)
    {
        _pendingStringUrl = url.Get();
        _pendingIsPollOrSubmit = isPollOrSubmit;
        _stringFetchStartTime = Time.realtimeSinceStartup;
        SetStatus(statusText);
        VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        float fetchMs = ElapsedMs(_stringFetchStartTime);
        string json = result.Result;
        if (string.IsNullOrEmpty(json))
        {
            LogWarn("拉取警告", "成功返回但内容为空 | 耗时 " + FormatMs(fetchMs));
            return;
        }

        // 处理轮询/提交响应，跳转到分配到的音乐槽位
        if (_pendingIsPollOrSubmit)
        {
            _pendingIsPollOrSubmit = false;
            int slot = ExtractFirstSongSlot(json);
            if (slot >= 0 && musicSlotUrls != null && slot < musicSlotUrls.Length && HasAnyPlayerBound())
            {
                LogInfo("拉取成功", "音乐池同步完成 | 耗时 " + FormatMs(fetchMs) + " | 命中槽位 #" + slot);
                SetStatus("正在播放音乐 #" + slot);
                VRCUrl targetUrl = musicSlotUrls[slot];
                PlayTargetUrl(targetUrl);
            }
            else
            {
                LogWarn("拉取结果", "音乐池无可用单曲 | 耗时 " + FormatMs(fetchMs));
                SetStatus("音乐池暂无可用单曲");
            }
            return;
        }

        // 解析歌曲元信息与双语歌词，并记录解析耗时
        float parseStart = Time.realtimeSinceStartup;
        string platform = ReadTopString(json, KeyPlatform).ToLower();
        ParseMetadataAndLyrics(json);
        float parseMs = ElapsedMs(parseStart);

        // 严格区分视频与音乐：非音乐平台（如 bilibili / youtube / douyin / generic）直接隐藏音乐舞台，绝不遮挡视频画面
        bool isMusic = IsMusicPlatform(platform);
        if (!isMusic && _lyricCount <= 0)
        {
            ClearMusicStage();
            LogInfo("视频模式", "识别为非音乐视频 (" + (string.IsNullOrEmpty(platform) ? "video" : platform) + ")，已隐藏音乐舞台 | 耗时 " + FormatMs(fetchMs));
            return;
        }

        // 若直链返回了对应槽位的封面地址，则自动触发 GPU 封面拉取
        if (!_hasCover && _pendingImage == null && coverGpuUrls != null)
        {
            int coverSlot = ExtractSlotIndex(ReadTopString(json, KeyCoverUrl));
            if (coverSlot >= 0 && coverSlot < coverGpuUrls.Length && !VRCUrl.IsNullOrEmpty(coverGpuUrls[coverSlot]))
            {
                StartCoverFetch(coverGpuUrls[coverSlot]);
            }
        }

        if (_lyricCount <= 0 && string.IsNullOrEmpty(_songName))
        {
            ClearMusicStage();
            return;
        }

        _hasMusicLyrics = true;
        _currentLineIdx = -2;
        _lastLocalTime = -1f;
        bool isInstrumental = _lyricCount <= 0 || (_lyricCount == 1 && _lyricTexts[0].Contains("纯音乐"));
        ApplyContentLayoutMode(isInstrumental);

        if (songTitleText != null) songTitleText.text = string.IsNullOrEmpty(_songName) ? "正在播放" : _songName;
        if (songArtistText != null) songArtistText.text = _singer;
        SetStatus(isInstrumental ? "♪ 静享此刻 · 纯音乐 ♪" : "");
        if (musicStageRoot != null) musicStageRoot.SetActive(true);

        LogInfo("拉取成功",
            "网络耗时 " + FormatMs(fetchMs) +
            " | 解析耗时 " + FormatMs(parseMs) +
            " | 歌名: " + _songName +
            " | 歌词 " + _lyricCount + " 行 / 翻译 " + _transCount + " 行" +
            " | 逐字模式: " + (_hasWordTimings ? "真逐字 (YRC/KRC)" : "智能拟真 (LRC)"));

        UpdateLyrics(GetPlaybackTime());
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        float fetchMs = ElapsedMs(_stringFetchStartTime);
        int code = result != null ? result.ErrorCode : 0;
        string rawError = result != null ? result.Error : "";
        string reason = FormatHttpReason(code, rawError);

        LogError("拉取失败",
            "耗时 " + FormatMs(fetchMs) +
            " | 状态码: " + reason +
            " | 详情: " + rawError +
            " | 链接: " + _pendingStringUrl);

        if (_pendingIsPollOrSubmit)
        {
            _pendingIsPollOrSubmit = false;
            SetStatus("接口拉取失败 (" + (code > 0 ? code.ToString() : "网络异常") + ")");
        }
    }

    #endregion

    #region GPU 封面拉取与解码

    // 使用点采样下载 BC1 数据图集，确保 GPU 字节解码零失真
    private void StartCoverFetch(VRCUrl gpuUrl)
    {
        if (_pendingImage != null) _pendingImage.Dispose();
        if (!Utilities.IsValid(_imageDownloader)) _imageDownloader = new VRCImageDownloader();

        TextureInfo info = new TextureInfo();
        info.WrapModeU = TextureWrapMode.Clamp;
        info.WrapModeV = TextureWrapMode.Clamp;
        info.WrapModeW = TextureWrapMode.Clamp;
        info.GenerateMipMaps = false;
        info.FilterMode = FilterMode.Point;
        info.AnisoLevel = 0;

        _coverFetchStartTime = Time.realtimeSinceStartup;
        _pendingImage = _imageDownloader.DownloadImage(gpuUrl, null, (IUdonEventReceiver)this, info);
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        if (result != _pendingImage)
        {
            result.Dispose();
            return;
        }

        _pendingImage = null;
        float fetchMs = ElapsedMs(_coverFetchStartTime);
        Texture2D atlas = result.Result;

        if (atlas != null && bc1DecodeMaterial != null && coverRenderTarget != null)
        {
            float gpuStart = Time.realtimeSinceStartup;
            atlas.filterMode = FilterMode.Point;
            atlas.anisoLevel = 0;
            if (!coverRenderTarget.IsCreated()) coverRenderTarget.Create();

            // 通过自定义 Shader 在 GPU 一次性完成 BC1 解码并自动生成背景 MipMap
            VRCGraphics.Blit(atlas, coverRenderTarget, bc1DecodeMaterial);
            float gpuMs = ElapsedMs(gpuStart);

            _hasCover = true;
            if (ambientBackdropMat != null) ambientBackdropMat.SetTexture(PropMainTex, coverRenderTarget);
            if (albumCardMat != null) albumCardMat.SetTexture(PropMainTex, coverRenderTarget);

            LogInfo("封面成功",
                "拉取耗时 " + FormatMs(fetchMs) +
                " | GPU解码渲染耗时 " + FormatMs(gpuMs) +
                " | 分辨率 " + coverRenderTarget.width + "x" + coverRenderTarget.height +
                " | 体积 " + (result.SizeInMemoryBytes / 1024) + " KB");
        }
        else
        {
            LogWarn("封面警告", "封面下载成功但解码材质或 RenderTexture 未绑定 | 拉取耗时 " + FormatMs(fetchMs));
        }

        result.Dispose();
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        float fetchMs = ElapsedMs(_coverFetchStartTime);
        if (result == _pendingImage) _pendingImage = null;

        string errType = result != null ? result.Error.ToString() : "Unknown";
        string errMsg = result != null ? result.ErrorMessage : "";
        int parsedCode = ExtractHttpCodeFromMessage(errMsg);

        LogError("封面失败",
            "耗时 " + FormatMs(fetchMs) +
            " | 类型: " + errType +
            " | 状态码: " + FormatHttpReason(parsedCode, errMsg) +
            " | 详情: " + errMsg);

        if (result != null) result.Dispose();
    }

    #endregion

    #region 歌词同步渲染与 GPU 金色上色

    // 根据播放进度更新当前句，并计算实时扫光上色进度
    private void UpdateLyrics(float timeSec)
    {
        if (lyricLineTexts == null || lyricLineTexts.Length < 5) return;

        if (_lyricCount <= 0)
        {
            for (int k = 0; k < 5; k++)
            {
                if (lyricLineTexts[k] != null) lyricLineTexts[k].text = k == 2 ? "♪ 静享此刻 · 纯音乐 ♪" : "";
            }
            if (activeTranslationText != null) activeTranslationText.text = "";
            if (activeLyricGpuMat != null) activeLyricGpuMat.SetFloat(PropSweep, 1f);
            return;
        }

        float effectiveTime = timeSec + lyricTimeOffset;
        int idx = FindActiveLineIndex(effectiveTime);

        if (idx != _currentLineIdx)
        {
            SwitchLyricLine(idx);
        }

        UpdateKaraokeSweep(idx, effectiveTime);
    }

    // 二分查找当前时间命中的歌词行索引
    private int FindActiveLineIndex(float effectiveTime)
    {
        int idx = -1;
        int lo = 0;
        int hi = _lyricCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_lyricTimes[mid] <= effectiveTime)
            {
                idx = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return idx;
    }

    // 切换歌词显示行，触发整组 5 行平滑上推过渡动画并统计本次渲染耗时
    private void SwitchLyricLine(int idx)
    {
        CacheLyricLayout();
        float renderStart = Time.realtimeSinceStartup;

        int prevIdx = _currentLineIdx;
        _currentLineIdx = idx;
        bool isSequentialStep = prevIdx >= -1 && Mathf.Abs(idx - prevIdx) == 1;
        _flipProgress = isSequentialStep ? 0.65f : 1f;
        _scrollProgress = isSequentialStep ? 0f : 1f;
        _scrollDirection = (idx < prevIdx) ? 1f : -1f;
        _lineSweepStartTime = renderStart;
        _lineSweepFinishedLogged = false;

        string currentLine = (idx >= 0 && idx < _lyricCount) ? _lyricTexts[idx] : "";

        for (int slot = 0; slot < 5; slot++)
        {
            int lineIndex = idx + (slot - 2);
            if (lyricLineTexts[slot] != null)
            {
                lyricLineTexts[slot].text = (lineIndex >= 0 && lineIndex < _lyricCount) ? _lyricTexts[lineIndex] : "";
            }
        }

        if (activeTranslationText != null)
        {
            activeTranslationText.text = FindMatchingTranslation(idx);
        }

        float renderMs = ElapsedMs(renderStart);

        // 根据本句在 Canvas 内的真实左边界与精确渲染宽度更新 GPU 上色参数
        float colorStart = Time.realtimeSinceStartup;
        BuildActiveLineWordLayout(idx, currentLine);
        if (activeLyricGpuMat != null)
        {
            Text activeTxt = lyricLineTexts[2];
            float startX = -162f;
            float exactW = EstimateLinePixelWidth(currentLine);
            if (activeTxt != null)
            {
                RectTransform art = activeTxt.rectTransform;
                if (art != null) startX = art.anchoredPosition.x - art.rect.width * art.pivot.x;
                float prefW = activeTxt.preferredWidth;
                if (prefW > 20f) exactW = Mathf.Clamp(prefW, 40f, art != null ? art.rect.width : 860f);
            }
            _activeExactWidth = exactW;
            activeLyricGpuMat.SetFloat(PropFlipFade, 0.65f);
            activeLyricGpuMat.SetFloat(PropStartX, startX);
            activeLyricGpuMat.SetFloat(PropTextWidth, exactW);
        }
        ApplyScrollAnimationFrame();
        float colorMs = ElapsedMs(colorStart);

        if (idx >= 0 && !string.IsNullOrEmpty(currentLine))
        {
            float vocalSpan = ComputeVocalSpan(idx);
            LogInfo("歌词渲染",
                "第 " + (idx + 1) + "/" + _lyricCount + " 句" +
                " | 模式: " + (_activeWordCount > 0 ? "真逐字(" + _activeWordCount + "词)" : "拟真") +
                " | 渲染耗时 " + FormatMs(renderMs) +
                " | 上色提交 " + FormatMs(colorMs) +
                " | 预计上色 " + vocalSpan.ToString("F2") + " s" +
                " | " + currentLine);
        }
    }

    // 预计算当前句每个词元在水平方向上的像素归一化起止点 (0..1)
    private void BuildActiveLineWordLayout(int idx, string text)
    {
        _activeWordCount = 0;
        if (idx < 0 || idx >= _wordCounts.Length || string.IsNullOrEmpty(text)) return;
        int wCount = _wordCounts[idx];
        if (wCount <= 0) return;

        int wStart = _wordStartIdx[idx];
        if (wCount > _activeWordStartU.Length)
        {
            _activeWordStartU = new float[wCount];
            _activeWordEndU = new float[wCount];
        }

        float totalPx = 0f;
        for (int ci = 0; ci < text.Length; ci++) totalPx += text[ci] > 127 ? 56f : 28f;
        if (totalPx < 1f) totalPx = 1f;

        int charCursor = 0;
        float accPx = 0f;
        for (int k = 0; k < wCount; k++)
        {
            int len = _wordLens[wStart + k];
            if (len < 1) len = 1;
            float wPx = 0f;
            for (int c = 0; c < len && charCursor < text.Length; c++)
            {
                wPx += text[charCursor] > 127 ? 56f : 28f;
                charCursor++;
            }
            _activeWordStartU[k] = Mathf.Clamp01(accPx / totalPx);
            accPx += wPx;
            _activeWordEndU[k] = Mathf.Clamp01(accPx / totalPx);
        }
        if (wCount > 0) _activeWordEndU[wCount - 1] = 1f;
        _activeWordCount = wCount;
    }

    // 更新当前句的逐字金色扫光进度（优先按真逐字时间轴卡点，无逐字数据时平滑拟真）
    private void UpdateKaraokeSweep(int idx, float effectiveTime)
    {
        if (activeLyricGpuMat == null) return;

        float sweep = 0f;
        if (idx >= 0 && idx < _lyricCount)
        {
            float relSec = effectiveTime - _lyricTimes[idx];
            if (_activeWordCount > 0 && idx < _wordStartIdx.Length)
            {
                sweep = EvaluateWordSweep(idx, relSec);
            }
            else
            {
                float vocalSpan = ComputeVocalSpan(idx);
                sweep = Mathf.Clamp01(relSec / vocalSpan);
            }

            if (sweep >= 0.999f && !_lineSweepFinishedLogged)
            {
                _lineSweepFinishedLogged = true;
                float actualSec = Time.realtimeSinceStartup - _lineSweepStartTime;
                LogInfo("歌词上色", "第 " + (idx + 1) + "/" + _lyricCount + " 句上色完成 | 实际耗时 " + actualSec.ToString("F2") + " s");
            }
        }

        activeLyricGpuMat.SetFloat(PropSweep, sweep);
    }

    // 根据 YRC/KRC 逐字时间轴精确计算当前水平扫光坐标（停顿换气时自然停住）
    private float EvaluateWordSweep(int lineIdx, float relSec)
    {
        int wStart = _wordStartIdx[lineIdx];
        if (relSec <= _wordOffsets[wStart]) return 0f;

        float lastEndU = 0f;
        for (int k = 0; k < _activeWordCount; k++)
        {
            int wi = wStart + k;
            float ws = _wordOffsets[wi];
            float wd = _wordDurs[wi];
            if (relSec < ws) return lastEndU;
            if (relSec <= ws + wd)
            {
                float frac = Mathf.Clamp01((relSec - ws) / Mathf.Max(0.02f, wd));
                return Mathf.Lerp(_activeWordStartU[k], _activeWordEndU[k], frac);
            }
            lastEndU = _activeWordEndU[k];
        }
        return 1f;
    }

    // 平滑推进切行上浮、5 行整体滚动位移与主行缩放过渡动画（Cubic Ease-Out）
    private void UpdateLineFlipAnimation()
    {
        if (_flipProgress < 1f)
        {
            _flipProgress = Mathf.MoveTowards(_flipProgress, 1f, Time.deltaTime * LineFlipSpeed);
            if (activeLyricGpuMat != null)
            {
                float ease = _flipProgress * _flipProgress * (3f - 2f * _flipProgress);
                activeLyricGpuMat.SetFloat(PropFlipFade, ease);
            }
        }

        if (_scrollProgress < 1f)
        {
            _scrollProgress = Mathf.MoveTowards(_scrollProgress, 1f, Time.deltaTime / ScrollTransitionDuration);
            ApplyScrollAnimationFrame();
        }
    }

    // 应用当前帧的 5 行滚动偏移、主/副行弹性缩放与边缘淡入，并实时同步 GPU 扫光左边界
    private void ApplyScrollAnimationFrame()
    {
        if (!_layoutCached || _lineRects == null) return;

        // Cubic Ease-Out: 初段利落响应，末段丝滑减速落位
        float inv = 1f - Mathf.Clamp01(_scrollProgress);
        float remain = inv * inv * inv;
        float offsetY = _scrollDirection * ScrollTravelPx * remain;

        for (int i = 0; i < _lineRects.Length; i++)
        {
            RectTransform rt = _lineRects[i];
            if (rt == null) continue;

            Vector2 pos = rt.anchoredPosition;
            pos.y = _lineBaseY[i] + offsetY;
            rt.anchoredPosition = pos;

            // 当前句由 0.88 平滑放大到 1.00，刚唱完上移的上一句由 1.12 平滑回落到 1.00
            float scale = 1f;
            if (i == 2) scale = 1f - 0.12f * remain;
            else if (i == 1 && _scrollDirection < 0f) scale = 1f + 0.12f * remain;
            rt.localScale = new Vector3(scale, scale, 1f);

            // 新进入底部的第 5 行柔和淡入
            if (i == 4 && lyricLineTexts[i] != null)
            {
                Color c = lyricLineTexts[i].color;
                c.a = _lineBaseAlpha[i] * (1f - 0.85f * remain);
                lyricLineTexts[i].color = c;
            }
        }

        if (_transRect != null)
        {
            Vector2 tpos = _transRect.anchoredPosition;
            tpos.y = _transBaseY + offsetY * 0.85f;
            _transRect.anchoredPosition = tpos;

            if (activeTranslationText != null)
            {
                Color tc = activeTranslationText.color;
                tc.a = _transBaseAlpha * (1f - 0.65f * remain);
                activeTranslationText.color = tc;
            }
        }

        // 缩放动画进行期间同步更新 _StartX 与 _TextWidth，保证逐字扫光全程像素级锁死
        if (activeLyricGpuMat != null && _lineRects.Length > 2 && _lineRects[2] != null)
        {
            RectTransform art = _lineRects[2];
            float s = art.localScale.x;
            float startX = art.anchoredPosition.x - art.rect.width * art.pivot.x * s;
            activeLyricGpuMat.SetFloat(PropStartX, startX);
            activeLyricGpuMat.SetFloat(PropTextWidth, Mathf.Max(40f, _activeExactWidth * s));
        }
    }

    // 结合「行时间间隔」与「本句字数自然唱速」估算真实演唱时长，防止间奏拖慢或快歌跟不上
    private float ComputeVocalSpan(int idx)
    {
        float t0 = _lyricTimes[idx];
        float t1 = (idx + 1 < _lyricCount) ? _lyricTimes[idx + 1] : (_duration > t0 ? _duration : t0 + 4.0f);
        float rawGap = Mathf.Max(0.35f, t1 - t0);
        float gapLimit = rawGap * sweepDurationRatio;

        string line = (idx >= 0 && idx < _lyricCount) ? _lyricTexts[idx] : "";
        float syllables = 0f;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c > 127) syllables += 1.0f;                                      // 中/日/韩单字按 1 拍计算
            else if (c == ' ' || c == ',' || c == '.' || c == '，' || c == '。') syllables += 0.18f;
            else syllables += 0.36f;                                             // 英文字符按音节折算
        }

        // 自然演唱估算时长（每拍约 0.28s + 起唱尾音 0.25s）；取与行间隔的较小值，快段提速、间奏不拖慢
        float naturalSpan = 0.25f + syllables * 0.28f;
        return Mathf.Clamp(Mathf.Min(gapLimit, naturalSpan), 0.35f, 6.5f);
    }

    private float EstimateLinePixelWidth(string text)
    {
        float px = 0f;
        for (int i = 0; i < text.Length; i++) px += text[i] > 127 ? 56f : 28f;
        return Mathf.Clamp(px, 140f, 840f);
    }

    private string FindMatchingTranslation(int idx)
    {
        if (idx < 0 || _transCount <= 0) return "";
        float curT = _lyricTimes[idx];
        float bestDiff = TransMatchWindowSec;
        string result = "";
        for (int i = 0; i < _transCount; i++)
        {
            float diff = Mathf.Abs(_transTimes[i] - curT);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                result = _transTexts[i];
            }
        }
        return result;
    }

    private void ClearMusicStage()
    {
        _lastPlayedUrl = "";
        _hasMusicLyrics = false;
        _hasCover = false;
        _coverBlend = 0f;
        _lyricCount = 0;
        _transCount = 0;
        _currentLineIdx = -2;
        SetCoverBlend(0f);
        if (_pendingImage != null)
        {
            _pendingImage.Dispose();
            _pendingImage = null;
        }
        if (musicStageRoot != null) musicStageRoot.SetActive(false);
    }

    private void SetCoverBlend(float value)
    {
        if (ambientBackdropMat != null) ambientBackdropMat.SetFloat(PropHasCover, value);
        if (albumCardMat != null) albumCardMat.SetFloat(PropHasCover, value);
    }

    private void SetStatus(string message)
    {
        if (statusBadgeText != null) statusBadgeText.text = message;
    }

    #endregion

    #region VRC 中文日志与 HTTP 错误码翻译

    private void LogInfo(string stage, string message)
    {
        if (enableVrcDebugLog) Debug.Log(LogPrefix + "[" + stage + "] " + message);
    }

    private void LogWarn(string stage, string message)
    {
        if (enableVrcDebugLog) Debug.LogWarning(LogPrefix + "[" + stage + "] " + message);
    }

    private void LogError(string stage, string message)
    {
        Debug.LogError(LogPrefix + "[" + stage + "] " + message);
    }

    private float ElapsedMs(float startRealtime)
    {
        float ms = (Time.realtimeSinceStartup - startRealtime) * 1000f;
        return ms < 0f ? 0f : ms;
    }

    private string FormatMs(float ms)
    {
        return ms.ToString("F2") + " ms";
    }

    // 将常见 HTTP 状态码转换为易读中文说明（支持 402/502 等）
    private string FormatHttpReason(int code, string rawMessage)
    {
        if (code <= 0) code = ExtractHttpCodeFromMessage(rawMessage);
        if (code == 400) return "400 (请求参数错误)";
        if (code == 401) return "401 (身份未授权)";
        if (code == 402) return "402 (额度不足/需要付费)";
        if (code == 403) return "403 (接口拒绝访问)";
        if (code == 404) return "404 (歌曲或资源不存在)";
        if (code == 408) return "408 (请求超时)";
        if (code == 429) return "429 (请求过于频繁/限流)";
        if (code == 500) return "500 (服务器内部错误)";
        if (code == 502) return "502 (网关错误/上游无响应)";
        if (code == 503) return "503 (服务暂不可用)";
        if (code == 504) return "504 (网关超时)";
        return code > 0 ? code + " (HTTP 异常)" : "网络或解析异常";
    }

    // 从错误文本中提取 3 位 HTTP 状态码（如 "HTTP/1.1 502"）
    private int ExtractHttpCodeFromMessage(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return 0;
        for (int i = 0; i <= msg.Length - 3; i++)
        {
            char c0 = msg[i];
            char c1 = msg[i + 1];
            char c2 = msg[i + 2];
            if ((c0 == '4' || c0 == '5') && c1 >= '0' && c1 <= '9' && c2 >= '0' && c2 <= '9')
            {
                bool prevDigit = i > 0 && msg[i - 1] >= '0' && msg[i - 1] <= '9';
                bool nextDigit = i + 3 < msg.Length && msg[i + 3] >= '0' && msg[i + 3] <= '9';
                if (!prevDigit && !nextDigit)
                {
                    return (c0 - '0') * 100 + (c1 - '0') * 10 + (c2 - '0');
                }
            }
        }
        return 0;
    }

    #endregion

    #region 轻量 Udon 安全 JSON 与槽位解析

    // 判断链接是否明确指向视频平台（B站/YouTube/抖音/A站/快手/Twitch等）
    private bool IsKnownVideoUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        string u = url.ToLower();
        return u.Contains("bilibili.com") || u.Contains("b23.tv") ||
               u.Contains("youtube.com") || u.Contains("youtu.be") ||
               u.Contains("douyin.com") || u.Contains("iesdouyin.com") ||
               u.Contains("acfun.cn") || u.Contains("kuaishou.com") ||
               u.Contains("twitch.tv") || u.Contains("nicovideo.jp");
    }

    // 仅音乐平台允许在无歌词时以「纯音乐居中黑胶」形态展示
    private bool IsMusicPlatform(string platform)
    {
        if (string.IsNullOrEmpty(platform)) return false;
        return platform == "netease" || platform == "qqmusic" || platform == "qq" ||
               platform == "kugou" || platform == "kuwo" || platform == "qishui" ||
               platform == "spotify" || platform == "applemusic";
    }

    // 从链接中解析 /s/<数字> 槽位号，范围受 poolCapacity 约束
    private int ExtractSlotIndex(string url)
    {
        if (string.IsNullOrEmpty(url)) return -1;
        int idx = url.IndexOf(PathSlotMarker);
        if (idx < 0) return -1;

        int start = idx + PathSlotMarker.Length;
        int end = start;
        while (end < url.Length && url[end] >= '0' && url[end] <= '9') end++;
        if (end <= start) return -1;

        int maxSlots = musicSlotUrls != null && musicSlotUrls.Length > 0 ? musicSlotUrls.Length : poolCapacity;
        int slot;
        return int.TryParse(url.Substring(start, end - start), out slot) && slot >= 0 && slot < maxSlots ? slot : -1;
    }

    private int ExtractFirstSongSlot(string json)
    {
        int start = FindArrayStart(json, KeySongs);
        if (start < 0) start = FindArrayStart(json, KeyItems);
        if (start < 0) return -1;

        int objStart = json.IndexOf('{', start);
        if (objStart < 0) return -1;
        int objEnd = FindObjectEnd(json, objStart);
        if (objEnd <= objStart) return -1;

        int keyPos = FindKeyStart(json, objStart, objEnd, KeySlotShort);
        if (keyPos < 0) keyPos = FindKeyStart(json, objStart, objEnd, KeySlotFull);
        if (keyPos < 0) return -1;

        int next;
        int slot = Mathf.RoundToInt(ParseNumberAt(json, keyPos, out next));
        int maxSlots = musicSlotUrls != null && musicSlotUrls.Length > 0 ? musicSlotUrls.Length : poolCapacity;
        return (slot >= 0 && slot < maxSlots) ? slot : -1;
    }

    private void ParseMetadataAndLyrics(string json)
    {
        _songName = ReadTopString(json, KeySongName);
        if (string.IsNullOrEmpty(_songName)) _songName = ReadTopString(json, KeyTitle);
        _singer = ReadTopString(json, KeySinger);
        if (string.IsNullOrEmpty(_singer)) _singer = ReadTopString(json, KeyArtist);
        _duration = ReadTopNumber(json, KeyDuration);

        int lStart = FindArrayStart(json, KeyLyrics);
        if (lStart >= 0)
        {
            ParseLyricArray(json, lStart, true);
            _lyricTimes = _parseTimes;
            _lyricTexts = _parseTexts;
            _lyricCount = _parseCount;
            _wordStartIdx = _parseWordStarts;
            _wordCounts = _parseWordCounts;
            _wordOffsets = _parseWordOffsets;
            _wordDurs = _parseWordDurs;
            _wordLens = _parseWordLens;
            _wordTotalCount = _parseWordTotal;
            _hasWordTimings = _wordTotalCount > 0;
        }
        else
        {
            _lyricTimes = new float[0];
            _lyricTexts = new string[0];
            _lyricCount = 0;
            _wordStartIdx = new int[0];
            _wordCounts = new int[0];
            _wordTotalCount = 0;
            _hasWordTimings = false;
        }

        int tStart = FindArrayStart(json, KeyTransLyrics);
        if (tStart >= 0)
        {
            ParseLyricArray(json, tStart, false);
            _transTimes = _parseTimes;
            _transTexts = _parseTexts;
            _transCount = _parseCount;
        }
        else
        {
            _transTimes = new float[0];
            _transTexts = new string[0];
            _transCount = 0;
        }

        // 自动识别毫秒时间戳并统一换算为秒
        float maxTime = 0f;
        for (int i = 0; i < _lyricCount; i++) if (_lyricTimes[i] > maxTime) maxTime = _lyricTimes[i];
        if ((_duration > 1f && maxTime > _duration * 3f) || maxTime > 2000f)
        {
            for (int i = 0; i < _lyricCount; i++) _lyricTimes[i] *= 0.001f;
            for (int i = 0; i < _transCount; i++) _transTimes[i] *= 0.001f;
        }
    }

    private void ParseLyricArray(string json, int arrayStart, bool parseWords)
    {
        _parseTimes = new float[128];
        _parseTexts = new string[128];
        _parseWordStarts = new int[128];
        _parseWordCounts = new int[128];
        _parseCount = 0;

        if (parseWords)
        {
            _parseWordOffsets = new float[512];
            _parseWordDurs = new float[512];
            _parseWordLens = new int[512];
            _parseWordTotal = 0;
        }

        int i = arrayStart + 1;
        int len = json.Length;
        while (i < len)
        {
            char c = json[i];
            if (c == ']') break;
            if (c == '{')
            {
                int end = FindObjectEnd(json, i);
                if (end > i)
                {
                    int tPos = FindKeyStart(json, i, end, KeyTime);
                    int sPos = FindKeyStart(json, i, end, KeyText);
                    if (tPos >= 0 && sPos >= 0)
                    {
                        int next;
                        float tm = ParseNumberAt(json, tPos, out next);
                        string tx = ParseStringAt(json, sPos, out next);
                        int wStart = _parseWordTotal;
                        int wCount = 0;
                        if (parseWords)
                        {
                            int wPos = FindKeyStart(json, i, end, KeyWords);
                            if (wPos >= 0 && json[wPos] == '[')
                            {
                                wCount = ParseWordArray(json, wPos, end);
                            }
                        }
                        AppendParsedLine(tm, tx, wStart, wCount);
                    }
                    i = end + 1;
                    continue;
                }
            }
            i++;
        }
    }

    // 解析单句的 words: [[offsetMs, durMs, charLen], ...] 逐字数组
    private int ParseWordArray(string json, int startBracket, int objEnd)
    {
        int count = 0;
        int i = startBracket + 1;
        while (i < objEnd)
        {
            char c = json[i];
            if (c == ']') break;
            if (c == '[')
            {
                i++;
                int next;
                while (i < objEnd && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
                float offMs = ParseNumberAt(json, i, out next);
                i = next;
                while (i < objEnd && (json[i] == ',' || json[i] == ' ' || json[i] == '\t')) i++;
                float durMs = ParseNumberAt(json, i, out next);
                i = next;
                int charLen = 1;
                while (i < objEnd && (json[i] == ' ' || json[i] == '\t')) i++;
                if (i < objEnd && json[i] == ',')
                {
                    i++;
                    while (i < objEnd && (json[i] == ' ' || json[i] == '\t')) i++;
                    charLen = Mathf.Max(1, Mathf.RoundToInt(ParseNumberAt(json, i, out next)));
                    i = next;
                }
                while (i < objEnd && json[i] != ']') i++;
                if (i < objEnd && json[i] == ']') i++;

                AppendParsedWord(offMs * 0.001f, Mathf.Max(0.02f, durMs * 0.001f), charLen);
                count++;
                continue;
            }
            i++;
        }
        return count;
    }

    private void AppendParsedWord(float offsetSec, float durSec, int charLen)
    {
        if (_parseWordTotal >= _parseWordOffsets.Length)
        {
            int nextSize = _parseWordOffsets.Length * 2;
            float[] nOff = new float[nextSize];
            float[] nDur = new float[nextSize];
            int[] nLen = new int[nextSize];
            for (int k = 0; k < _parseWordTotal; k++)
            {
                nOff[k] = _parseWordOffsets[k];
                nDur[k] = _parseWordDurs[k];
                nLen[k] = _parseWordLens[k];
            }
            _parseWordOffsets = nOff;
            _parseWordDurs = nDur;
            _parseWordLens = nLen;
        }
        _parseWordOffsets[_parseWordTotal] = offsetSec;
        _parseWordDurs[_parseWordTotal] = durSec;
        _parseWordLens[_parseWordTotal] = charLen;
        _parseWordTotal++;
    }

    private void AppendParsedLine(float timeSec, string text, int wordStart, int wordCount)
    {
        if (_parseCount >= _parseTimes.Length)
        {
            int nextSize = _parseTimes.Length * 2;
            float[] nextTimes = new float[nextSize];
            string[] nextTexts = new string[nextSize];
            int[] nextWStarts = new int[nextSize];
            int[] nextWCounts = new int[nextSize];
            for (int k = 0; k < _parseCount; k++)
            {
                nextTimes[k] = _parseTimes[k];
                nextTexts[k] = _parseTexts[k];
                nextWStarts[k] = _parseWordStarts[k];
                nextWCounts[k] = _parseWordCounts[k];
            }
            _parseTimes = nextTimes;
            _parseTexts = nextTexts;
            _parseWordStarts = nextWStarts;
            _parseWordCounts = nextWCounts;
        }
        _parseTimes[_parseCount] = timeSec;
        _parseTexts[_parseCount] = text;
        _parseWordStarts[_parseCount] = wordStart;
        _parseWordCounts[_parseCount] = wordCount;
        _parseCount++;
    }

    // 全文扫描顶层字段（Go map 序列化按字母排序，song_name/singer 位于 lyrics 之后，不能截断在 4096 字节）
    private string ReadTopString(string json, string key)
    {
        int pos = FindKeyStart(json, 0, json.Length, key);
        if (pos < 0 || json[pos] != '"') return "";
        int next;
        return ParseStringAt(json, pos, out next);
    }

    private float ReadTopNumber(string json, string key)
    {
        int pos = FindKeyStart(json, 0, json.Length, key);
        if (pos < 0) return 0f;
        int next;
        return ParseNumberAt(json, pos, out next);
    }

    private int FindArrayStart(string json, string key)
    {
        int k = json.IndexOf(key);
        if (k < 0) return -1;
        for (int i = k + key.Length; i < json.Length; i++)
        {
            if (json[i] == '[') return i;
            if (json[i] == '{' || json[i] == '}' || json[i] == ']') return -1;
        }
        return -1;
    }

    private int FindObjectEnd(string json, int start)
    {
        int depth = 0;
        bool inStr = false;
        bool esc = false;
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth <= 0) return i;
        }
        return -1;
    }

    private int FindKeyStart(string json, int start, int end, string key)
    {
        int found = json.IndexOf(key, start);
        if (found < 0 || found + key.Length > end) return -1;
        int i = found + key.Length;
        while (i < end && (json[i] == ':' || json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
        return i < end ? i : -1;
    }

    private float ParseNumberAt(string json, int start, out int next)
    {
        next = start;
        bool neg = false;
        float val = 0f;
        int i = start;

        if (i < json.Length && json[i] == '-') { neg = true; i++; }
        while (i < json.Length && json[i] >= '0' && json[i] <= '9') { val = val * 10f + (json[i] - '0'); i++; }
        if (i < json.Length && json[i] == '.')
        {
            i++;
            float scale = 0.1f;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9')
            {
                val += (json[i] - '0') * scale;
                scale *= 0.1f;
                i++;
            }
        }
        next = i;
        return neg ? -val : val;
    }

    private string ParseStringAt(string json, int start, out int next)
    {
        next = start;
        if (start >= json.Length || json[start] != '"') return "";

        StringBuilder sb = new StringBuilder();
        int i = start + 1;
        while (i < json.Length)
        {
            char c = json[i++];
            if (c == '"') { next = i; return sb.ToString(); }
            if (c == '\\' && i < json.Length)
            {
                char e = json[i++];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'u' && i + 4 <= json.Length)
                {
                    int code = 0;
                    for (int h = 0; h < 4; h++)
                    {
                        char hc = json[i++];
                        int v = (hc >= '0' && hc <= '9') ? (hc - '0')
                              : (hc >= 'a' && hc <= 'f') ? (hc - 'a' + 10)
                              : (hc >= 'A' && hc <= 'F') ? (hc - 'A' + 10) : 0;
                        code = (code << 4) | v;
                    }
                    sb.Append((char)code);
                }
                else sb.Append(e);
            }
            else sb.Append(c);
        }
        next = i;
        return sb.ToString();
    }

    #endregion
}
