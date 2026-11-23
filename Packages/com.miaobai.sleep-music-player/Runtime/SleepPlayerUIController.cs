using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 眠境播放器胶囊 UI 控制器：负责音量抽屉展开、设置栏防抖折叠与播放列表开关。
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SleepPlayerUIController : UdonSharpBehaviour
{
    #region Inspector 配置

    [Header("第一行：进度条自适应与音量抽屉")]
    public RectTransform progressRect;
    public RectTransform volumeDrawerRect;
    public CanvasGroup volumeDrawerGroup;
    public Image volumeToggleIcon;

    public float progressWideX = -205f;
    public float progressWideW = 670f;
    public float progressCompactX = -345f;
    public float progressCompactW = 390f;
    public float volumeExpandedW = 245f;
    public float animSpeed = 10f;

    [Header("第二行：输入框与高级设置切换")]
    public GameObject urlInputRow;
    public GameObject settingsRow;
    public Image settingsGearIcon;
    public float autoCollapseDelay = 2.0f;

    [Header("右侧播放列表开关")]
    public GameObject playlistRoot;

    #endregion

    #region 运行时状态

    private bool _volumeOpen;
    private float _volT;
    private bool _settingsOpen;
    private int _settingsHoverCount;
    private float _settingsExitTimer;

    private readonly Color _colIdle = new Color(0.96f, 0.94f, 0.91f, 1f);
    private readonly Color _colActive = new Color(0.95f, 0.72f, 0.44f, 1f);

    #endregion

    #region 生命周期

    private void Start()
    {
        _volumeOpen = false;
        _volT = 0f;
        ApplyVolumeLayout(0f);

        _settingsOpen = false;
        _settingsHoverCount = 0;
        _settingsExitTimer = 0f;
        ApplySettingsState(false);
    }

    private void Update()
    {
        UpdateVolumeDrawerAnimation();
        UpdateSettingsAutoCollapse();
    }

    #endregion

    #region 内部布局与动画

    // 平滑插值进度条宽度与音量抽屉显隐
    private void UpdateVolumeDrawerAnimation()
    {
        float target = _volumeOpen ? 1f : 0f;
        if (Mathf.Abs(_volT - target) <= 0.001f) return;

        _volT = Mathf.MoveTowards(_volT, target, Time.deltaTime * animSpeed);
        float ease = _volT * _volT * (3f - 2f * _volT);
        ApplyVolumeLayout(ease);
    }

    // 指针移出设置区后延迟自动收起，防止误触闪烁
    private void UpdateSettingsAutoCollapse()
    {
        if (!_settingsOpen) return;

        if (_settingsHoverCount > 0)
        {
            _settingsExitTimer = 0f;
            return;
        }

        _settingsExitTimer += Time.deltaTime;
        if (_settingsExitTimer >= autoCollapseDelay)
        {
            _settingsOpen = false;
            _settingsExitTimer = 0f;
            ApplySettingsState(false);
        }
    }

    private void ApplyVolumeLayout(float t)
    {
        if (progressRect != null)
        {
            Vector2 pos = progressRect.anchoredPosition;
            pos.x = Mathf.Lerp(progressWideX, progressCompactX, t);
            progressRect.anchoredPosition = pos;

            Vector2 size = progressRect.sizeDelta;
            size.x = Mathf.Lerp(progressWideW, progressCompactW, t);
            progressRect.sizeDelta = size;
        }

        if (volumeDrawerRect != null)
        {
            Vector2 size = volumeDrawerRect.sizeDelta;
            size.x = Mathf.Lerp(0f, volumeExpandedW, t);
            volumeDrawerRect.sizeDelta = size;
            volumeDrawerRect.gameObject.SetActive(t > 0.02f);
        }

        if (volumeDrawerGroup != null)
        {
            volumeDrawerGroup.alpha = t;
            volumeDrawerGroup.interactable = t > 0.5f;
            volumeDrawerGroup.blocksRaycasts = t > 0.5f;
        }

        if (volumeToggleIcon != null) volumeToggleIcon.color = _volumeOpen ? _colActive : _colIdle;
    }

    private void ApplySettingsState(bool open)
    {
        if (urlInputRow != null) urlInputRow.SetActive(!open);
        if (settingsRow != null) settingsRow.SetActive(open);
        if (settingsGearIcon != null) settingsGearIcon.color = open ? _colActive : _colIdle;
    }

    #endregion

    #region UI 交互事件

    /// <summary>切换音量滑块抽屉展开状态。</summary>
    public void ToggleVolumeDrawer()
    {
        _volumeOpen = !_volumeOpen;
        if (volumeToggleIcon != null) volumeToggleIcon.color = _volumeOpen ? _colActive : _colIdle;
    }

    /// <summary>在链接输入框与高级设置行之间切换。</summary>
    public void ToggleSettingsDrawer()
    {
        _settingsOpen = !_settingsOpen;
        _settingsExitTimer = 0f;
        ApplySettingsState(_settingsOpen);
    }

    /// <summary>指针进入设置区时重置收起计时。</summary>
    public void OnSettingsPointerEnter()
    {
        _settingsHoverCount++;
        _settingsExitTimer = 0f;
    }

    /// <summary>指针离开设置区时开始防抖倒计时。</summary>
    public void OnSettingsPointerExit()
    {
        _settingsHoverCount = Mathf.Max(0, _settingsHoverCount - 1);
    }

    /// <summary>开关右侧播放列表面板。</summary>
    public void TogglePlaylist()
    {
        if (playlistRoot != null) playlistRoot.SetActive(!playlistRoot.activeSelf);
    }

    #endregion
}
