using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UdonSharp;
using UdonSharpEditor;
using VRC.SDK3.Components;
using VRC.SDK3.Video.Components.Base;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// 眠境音乐池配置核心逻辑：负责按模板一键生成并同步所有 VRCUrl 到 Udon 组件与输入框。
/// </summary>
public static class SleepMusicConfigUtility
{
    public const string DefaultDomain = "https://api.kipfel.link";
    public const string DefaultWorldId = "ktv";
    public const string DefaultPoolName = "music";
    public const int DefaultPoolCapacity = 100;
    public const string DefaultDirectPath = "/v4/vrc?world={world}&url=";
    public const string DefaultPollPath = "/poll?world={world}&pool={pool}";
    public const string DefaultSlotPath = "/s/{slot}?world={world}";
    public const string DefaultCoverPath = "/s/{slot}/cover-gpu.png?world={world}";

    /// <summary>拼接并规范化接口完整 URL。</summary>
    public static string BuildUrl(string domain, string pathTemplate, string worldId, string poolName, int slotIndex)
    {
        string cleanDomain = string.IsNullOrEmpty(domain) ? DefaultDomain : domain.Trim().TrimEnd('/');
        string path = string.IsNullOrEmpty(pathTemplate) ? "" : pathTemplate.Trim();
        if (path.Length > 0 && path[0] != '/') path = "/" + path;

        path = path
            .Replace("{world}", string.IsNullOrEmpty(worldId) ? DefaultWorldId : worldId.Trim())
            .Replace("{pool}", string.IsNullOrEmpty(poolName) ? DefaultPoolName : poolName.Trim())
            .Replace("{slot}", slotIndex.ToString());

        return cleanDomain + path;
    }

    /// <summary>一键替换控制器内的域名、直链前缀、轮询链接与全部槽位池，并同步到 Udon。</summary>
    public static void ApplyAndSync(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;

        Undo.RecordObject(ctrl, "一键替换眠境音乐接口与池配置");

        int count = Mathf.Clamp(ctrl.poolCapacity, 1, 500);
        ctrl.poolCapacity = count;

        string directUrl = BuildUrl(ctrl.apiDomain, ctrl.directPathTemplate, ctrl.worldId, ctrl.poolName, 0);
        string pollUrl = BuildUrl(ctrl.apiDomain, ctrl.pollPathTemplate, ctrl.worldId, ctrl.poolName, 0);

        ctrl.fixedInputPrefix = new VRCUrl(directUrl);
        ctrl.musicPollUrl = new VRCUrl(pollUrl);
        ctrl.musicSlotUrls = new VRCUrl[count];
        ctrl.coverGpuUrls = new VRCUrl[count];

        for (int i = 0; i < count; i++)
        {
            ctrl.musicSlotUrls[i] = new VRCUrl(BuildUrl(ctrl.apiDomain, ctrl.slotPathTemplate, ctrl.worldId, ctrl.poolName, i));
            ctrl.coverGpuUrls[i] = new VRCUrl(BuildUrl(ctrl.apiDomain, ctrl.coverPathTemplate, ctrl.worldId, ctrl.poolName, i));
        }

        // 同步更新场景输入框的默认前缀
        if (ctrl.urlInputField != null)
        {
            Undo.RecordObject(ctrl.urlInputField, "同步音乐输入框默认前缀");
            ctrl.urlInputField.SetUrl(ctrl.fixedInputPrefix);
            EditorUtility.SetDirty(ctrl.urlInputField);
        }

        // 将 Proxy 字段同步写入底层 UdonBehaviour
        UdonSharpEditorUtility.CopyProxyToUdon(ctrl);
        EditorUtility.SetDirty(ctrl);
        if (ctrl.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(ctrl.gameObject.scene);

        Debug.Log("[眠境音乐配置] 一键替换完成 | 域名: " + ctrl.apiDomain +
                  " | 世界: " + ctrl.worldId +
                  " | 池: " + ctrl.poolName +
                  " | 槽位数: " + count);
    }

    /// <summary>恢复默认 Kipfel 接口与 100 槽位音乐池配置。</summary>
    public static void ResetToDefault(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;
        Undo.RecordObject(ctrl, "恢复眠境音乐默认配置");
        ctrl.apiDomain = DefaultDomain;
        ctrl.worldId = DefaultWorldId;
        ctrl.poolName = DefaultPoolName;
        ctrl.poolCapacity = DefaultPoolCapacity;
        ctrl.directPathTemplate = DefaultDirectPath;
        ctrl.pollPathTemplate = DefaultPollPath;
        ctrl.slotPathTemplate = DefaultSlotPath;
        ctrl.coverPathTemplate = DefaultCoverPath;
        ApplyAndSync(ctrl);
    }

    /// <summary>
    /// 一键自动扫描并绑定场景中任意 VRChat 视频播放器（VizVid / ProTV / YamaPlayer / IWsync / USharpVideo / 原生 VRCVideoPlayer）。
    /// </summary>
    public static void AutoBindAnyPlayer(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;
        Undo.RecordObject(ctrl, "一键自动绑定任意视频播放器");

        // 1) 自动发现主流或自定义 UdonSharp 播放器核心脚本
        if (ctrl.playerController == null)
        {
            ctrl.playerController = FindBestPlayerController(ctrl);
        }

        // 2) 自动收集底层 BaseVRCVideoPlayer（优先从已绑定控制器子树查找，否则全场景收集）
        List<BaseVRCVideoPlayer> foundPlayers = new List<BaseVRCVideoPlayer>();
        if (ctrl.playerController != null)
        {
            foundPlayers.AddRange(ctrl.playerController.GetComponentsInChildren<BaseVRCVideoPlayer>(true));
            if (foundPlayers.Count == 0 && ctrl.playerController.transform.root != null)
            {
                foundPlayers.AddRange(ctrl.playerController.transform.root.GetComponentsInChildren<BaseVRCVideoPlayer>(true));
            }
        }
        if (foundPlayers.Count == 0)
        {
            foundPlayers.AddRange(Object.FindObjectsOfType<BaseVRCVideoPlayer>(true));
        }
        ctrl.videoPlayers = foundPlayers.ToArray();

        // 3) 自动绑定场景中的 VRCUrlInputField（若尚未绑定）
        if (ctrl.urlInputField == null)
        {
            if (ctrl.playerController != null && ctrl.playerController.transform.root != null)
            {
                ctrl.urlInputField = ctrl.playerController.transform.root.GetComponentInChildren<VRCUrlInputField>(true);
            }
            if (ctrl.urlInputField == null)
            {
                ctrl.urlInputField = Object.FindObjectOfType<VRCUrlInputField>(true);
            }
        }

        // 4) 自动从输入框的 onEndEdit 持久监听中提取原播放器的提交目标与事件名（用于音乐池自动切歌）
        if (ctrl.urlInputField != null && (ctrl.playerSubmitTarget == null || string.IsNullOrEmpty(ctrl.playerSubmitEvent)))
        {
            TryExtractSubmitEvent(ctrl.urlInputField, ctrl, out UdonBehaviour submitTarget, out string submitEvent);
            if (submitTarget != null && !string.IsNullOrEmpty(submitEvent))
            {
                ctrl.playerSubmitTarget = submitTarget;
                ctrl.playerSubmitEvent = submitEvent;
            }
        }

        UdonSharpEditorUtility.CopyProxyToUdon(ctrl);
        EditorUtility.SetDirty(ctrl);
        if (ctrl.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(ctrl.gameObject.scene);

        string ctrlName = ctrl.playerController != null ? ctrl.playerController.GetType().Name + " (" + ctrl.playerController.name + ")" : "纯底层 VRCVideoPlayer 模式";
        Debug.Log("[眠境音乐配置] 自动绑定播放器完成 | 控制器: " + ctrlName +
                  " | 底层 VideoPlayer: " + (ctrl.videoPlayers != null ? ctrl.videoPlayers.Length : 0) + " 个" +
                  " | 输入框: " + (ctrl.urlInputField != null ? ctrl.urlInputField.name : "无") +
                  " | 切歌事件: " + (string.IsNullOrEmpty(ctrl.playerSubmitEvent) ? "直调 PlayURL" : ctrl.playerSubmitEvent));
    }

    private static UdonSharpBehaviour FindBestPlayerController(SleepMusicLyricsController self)
    {
        string[] preferredNames = { "Core", "TVManager", "YamaPlayerController", "YamaPlayer", "USharpVideoPlayer", "VideoCore", "SyncVideoPlayer" };
        UdonSharpBehaviour[] all = Object.FindObjectsOfType<UdonSharpBehaviour>(true);
        UdonSharpBehaviour fallback = null;

        for (int i = 0; i < all.Length; i++)
        {
            UdonSharpBehaviour b = all[i];
            if (b == null || b == self || b is SleepMusicLyricsController || b is SleepPlayerUIController) continue;

            string typeName = b.GetType().Name;
            for (int p = 0; p < preferredNames.Length; p++)
            {
                if (typeName == preferredNames[p]) return b;
            }

            if (fallback == null && HasUrlField(b.GetType()))
            {
                fallback = b;
            }
        }
        return fallback;
    }

    private static bool HasUrlField(System.Type t)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo[] fields = t.GetFields(flags);
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].FieldType == typeof(VRCUrl)) return true;
        }
        return false;
    }

    private static void TryExtractSubmitEvent(VRCUrlInputField field, SleepMusicLyricsController self, out UdonBehaviour target, out string eventName)
    {
        target = null;
        eventName = "";
        UdonBehaviour selfUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(self);

        SerializedObject so = new SerializedObject(field);
        SerializedProperty calls = so.FindProperty("m_OnEndEdit.m_PersistentCalls.m_Calls");
        if (calls == null || !calls.isArray) return;

        for (int i = 0; i < calls.arraySize; i++)
        {
            SerializedProperty call = calls.GetArrayElementAtIndex(i);
            Object obj = call.FindPropertyRelative("m_Target")?.objectReferenceValue;
            string method = call.FindPropertyRelative("m_MethodName")?.stringValue;
            string strArg = call.FindPropertyRelative("m_Arguments.m_StringArgument")?.stringValue;

            if (obj is UdonBehaviour ub && ub != selfUdon && method == "SendCustomEvent" && !string.IsNullOrEmpty(strArg))
            {
                target = ub;
                eventName = strArg;
                return;
            }
        }
    }

    /// <summary>生成当前播放器绑定状态摘要文本。</summary>
    public static string GetBindingSummary(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return "未绑定";
        int vpCount = 0;
        if (ctrl.videoPlayers != null)
        {
            for (int i = 0; i < ctrl.videoPlayers.Length; i++)
            {
                if (ctrl.videoPlayers[i] != null) vpCount++;
            }
        }
        string ctrlText = ctrl.playerController != null
            ? ctrl.playerController.GetType().Name
            : (ctrl.rawUdonPlayer != null ? "UdonBehaviour (" + ctrl.rawUdonPlayer.name + ")" : "未指定（仅底层模式）");
        return "控制器: " + ctrlText + " | 底层播放器: " + vpCount + " 个";
    }
}

/// <summary>
/// 自定义 Inspector：在 SleepMusicLyricsController 面板顶部提供一键替换域名/池容量的快捷操作区。
/// </summary>
[CustomEditor(typeof(SleepMusicLyricsController))]
public class SleepMusicLyricsControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;

        SleepMusicLyricsController ctrl = (SleepMusicLyricsController)target;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("一键接口与音乐池配置", EditorStyles.boldLabel);

        string previewDirect = SleepMusicConfigUtility.BuildUrl(ctrl.apiDomain, ctrl.directPathTemplate, ctrl.worldId, ctrl.poolName, 0);
        string previewPoll = SleepMusicConfigUtility.BuildUrl(ctrl.apiDomain, ctrl.pollPathTemplate, ctrl.worldId, ctrl.poolName, 0);
        string previewSlot = SleepMusicConfigUtility.BuildUrl(ctrl.apiDomain, ctrl.slotPathTemplate, ctrl.worldId, ctrl.poolName, 0);
        string previewCover = SleepMusicConfigUtility.BuildUrl(ctrl.apiDomain, ctrl.coverPathTemplate, ctrl.worldId, ctrl.poolName, 0);

        EditorGUILayout.HelpBox(
            "播放器绑定: " + SleepMusicConfigUtility.GetBindingSummary(ctrl) + "\n" +
            "直链前缀: " + previewDirect + "\n" +
            "轮询接口: " + previewPoll + "\n" +
            "槽位 #0 : " + previewSlot + "\n" +
            "封面 #0 : " + previewCover + "\n" +
            "当前已烘焙槽位: " + (ctrl.musicSlotUrls != null ? ctrl.musicSlotUrls.Length : 0) + " 个",
            MessageType.Info);

        if (GUILayout.Button("一键自动绑定场景内任意播放器（VizVid / ProTV / Yama / 原生等）", GUILayout.Height(28)))
        {
            SleepMusicConfigUtility.AutoBindAnyPlayer(ctrl);
        }
        EditorGUILayout.Space(2);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("一键替换域名并同步音乐池", GUILayout.Height(30)))
        {
            SleepMusicConfigUtility.ApplyAndSync(ctrl);
        }
        if (GUILayout.Button("恢复默认", GUILayout.Width(82), GUILayout.Height(30)))
        {
            SleepMusicConfigUtility.ResetToDefault(ctrl);
        }
        if (GUILayout.Button("独立设置窗口", GUILayout.Width(100), GUILayout.Height(30)))
        {
            SleepMusicSettingsWindow.OpenWindow();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(6);
        DrawDefaultInspector();
    }
}

/// <summary>
/// 独立编辑器设置窗口：菜单路径 Tools / 眠境音乐播放器 / 接口与音乐池一键配置。
/// </summary>
public class SleepMusicSettingsWindow : EditorWindow
{
    private SleepMusicLyricsController _target;
    private Vector2 _scroll;

    [MenuItem("Tools/眠境音乐播放器/一键自动绑定场景任意播放器", false, 11)]
    public static void AutoBindCurrentScenePlayer()
    {
        SleepMusicLyricsController ctrl = Object.FindObjectOfType<SleepMusicLyricsController>(true);
        if (ctrl == null)
        {
            EditorUtility.DisplayDialog("未找到控制器", "当前场景未找到 SleepMusicLyricsController。", "确定");
            return;
        }
        SleepMusicConfigUtility.AutoBindAnyPlayer(ctrl);
    }

    [MenuItem("Tools/眠境音乐播放器/接口与音乐池一键配置", false, 10)]
    public static void OpenWindow()
    {
        SleepMusicSettingsWindow win = GetWindow<SleepMusicSettingsWindow>("眠境音乐接口设置");
        win.minSize = new Vector2(520f, 440f);
        win.FindController();
        win.Show();
    }

    private void OnEnable()
    {
        FindController();
    }

    private void FindController()
    {
        if (_target == null) _target = Object.FindObjectOfType<SleepMusicLyricsController>(true);
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("眠境音乐播放器 · 接口与音乐池设置", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        _target = (SleepMusicLyricsController)EditorGUILayout.ObjectField("目标控制器", _target, typeof(SleepMusicLyricsController), true);
        if (GUILayout.Button("自动查找", GUILayout.Width(80))) FindController();
        EditorGUILayout.EndHorizontal();

        if (_target == null)
        {
            EditorGUILayout.HelpBox("当前场景未找到 SleepMusicLyricsController，请先打开包含音乐播放器的场景。", MessageType.Warning);
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUILayout.Space(8);
        EditorGUI.BeginChangeCheck();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("1. 域名、世界标识与音乐池数量", EditorStyles.boldLabel);
        string domain = EditorGUILayout.TextField("API 基础域名", _target.apiDomain);
        string world = EditorGUILayout.TextField("世界标识 (world)", _target.worldId);
        string pool = EditorGUILayout.TextField("音乐池名称 (pool)", _target.poolName);
        int capacity = EditorGUILayout.IntSlider("音乐池槽位数量", _target.poolCapacity, 1, 500);
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("2. 接口路径模板（支持 {world} / {pool} / {slot}）", EditorStyles.boldLabel);
        string directTpl = EditorGUILayout.TextField("直链输入前缀", _target.directPathTemplate);
        string pollTpl = EditorGUILayout.TextField("轮询接口路径", _target.pollPathTemplate);
        string slotTpl = EditorGUILayout.TextField("槽位音频路径", _target.slotPathTemplate);
        string coverTpl = EditorGUILayout.TextField("GPU 封面路径", _target.coverPathTemplate);
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("3. 歌词同步、上色与 VRC 中文日志", EditorStyles.boldLabel);
        float offset = EditorGUILayout.Slider("歌词时间补偿 (秒)", _target.lyricTimeOffset, -1.5f, 1.5f);
        float sweepRatio = EditorGUILayout.Slider("金色上色时长占比", _target.sweepDurationRatio, 0.2f, 1.0f);
        bool verboseLog = EditorGUILayout.Toggle("输出 VRC 中文性能日志", _target.enableVrcDebugLog);
        EditorGUILayout.EndVertical();

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_target, "修改眠境音乐接口设置");
            _target.apiDomain = domain;
            _target.worldId = world;
            _target.poolName = pool;
            _target.poolCapacity = capacity;
            _target.directPathTemplate = directTpl;
            _target.pollPathTemplate = pollTpl;
            _target.slotPathTemplate = slotTpl;
            _target.coverPathTemplate = coverTpl;
            _target.lyricTimeOffset = offset;
            _target.sweepDurationRatio = sweepRatio;
            _target.enableVrcDebugLog = verboseLog;
            EditorUtility.SetDirty(_target);
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "生成预览：\n" +
            "• 输入前缀: " + SleepMusicConfigUtility.BuildUrl(_target.apiDomain, _target.directPathTemplate, _target.worldId, _target.poolName, 0) + "\n" +
            "• 轮询地址: " + SleepMusicConfigUtility.BuildUrl(_target.apiDomain, _target.pollPathTemplate, _target.worldId, _target.poolName, 0) + "\n" +
            "• 音频槽位: " + SleepMusicConfigUtility.BuildUrl(_target.apiDomain, _target.slotPathTemplate, _target.worldId, _target.poolName, 0) + "  (共 " + _target.poolCapacity + " 个)\n" +
            "• 封面槽位: " + SleepMusicConfigUtility.BuildUrl(_target.apiDomain, _target.coverPathTemplate, _target.worldId, _target.poolName, 0),
            MessageType.None);

        EditorGUILayout.Space(6);
        if (GUILayout.Button("一键自动绑定场景内任意播放器（VizVid / ProTV / Yama / 原生等）", GUILayout.Height(30)))
        {
            SleepMusicConfigUtility.AutoBindAnyPlayer(_target);
        }
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("一键替换并同步到场景", GUILayout.Height(34)))
        {
            SleepMusicConfigUtility.ApplyAndSync(_target);
        }
        if (GUILayout.Button("恢复默认配置", GUILayout.Width(120), GUILayout.Height(34)))
        {
            SleepMusicConfigUtility.ResetToDefault(_target);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndScrollView();
    }
}


/// <summary>快捷菜单：在场景层级中一键生成开箱即用的眠境音乐播放器。</summary>
public static class SleepMusicPrefabMenu
{
    private const string PackagePrefabPath = "Packages/com.miaobai.sleep-music-player/Prefabs/SleepMusicPlayer (All-in-One).prefab";

    [MenuItem("GameObject/眠境音乐播放器/创建开箱即用播放器", false, 10)]
    [MenuItem("Tools/眠境音乐播放器/在场景中创建播放器", false, 1)]
    public static void CreatePlayerInScene()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PackagePrefabPath);
        if (prefab == null)
        {
            EditorUtility.DisplayDialog("未找到预制体", "请确认包内预制体存在：" + PackagePrefabPath, "确定");
            return;
        }
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(inst, "创建眠境音乐播放器");
        Selection.activeGameObject = inst;
        Debug.Log("[眠境音乐播放器] 已在场景中创建开箱即用播放器，可在 Tools / 眠境音乐播放器 / 接口与音乐池一键配置 中修改域名与池大小。");
    }
}
