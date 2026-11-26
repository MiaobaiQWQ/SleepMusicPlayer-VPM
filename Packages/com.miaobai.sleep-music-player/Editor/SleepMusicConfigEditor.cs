using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
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
    /// 从用户拖入的任意播放器对象（GameObject / Component）自动识别并绑定全部组件。
    /// </summary>
    public static void BindFromTargetObject(SleepMusicLyricsController ctrl, GameObject targetRoot)
    {
        if (ctrl == null || targetRoot == null) return;
        Undo.RecordObject(ctrl, "拖拽绑定指定播放器");

        // 1) 在目标播放器子树中查找核心 UdonSharpBehaviour 或纯 UdonBehaviour
        UdonSharpBehaviour foundCtrl = FindBestPlayerControllerInHierarchy(targetRoot, ctrl);
        ctrl.playerController = foundCtrl;

        if (foundCtrl == null)
        {
            UdonBehaviour selfUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(ctrl);
            UdonBehaviour[] udons = targetRoot.GetComponentsInChildren<UdonBehaviour>(true);
            ctrl.rawUdonPlayer = null;
            for (int i = 0; i < udons.Length; i++)
            {
                if (udons[i] != null && udons[i] != selfUdon)
                {
                    ctrl.rawUdonPlayer = udons[i];
                    break;
                }
            }
        }
        else
        {
            ctrl.rawUdonPlayer = null;
        }

        // 2) 收集目标播放器子树内全部底层 BaseVRCVideoPlayer（AVPro / Unity）
        BaseVRCVideoPlayer[] vps = targetRoot.GetComponentsInChildren<BaseVRCVideoPlayer>(true);
        ctrl.videoPlayers = vps;

        // 3) 非纯舞台模式时，绑定目标播放器内的输入框与 onEndEdit 提交事件（纯舞台模式不接管原播放器控件）
        if (!ctrl.stageOnlyMode)
        {
            VRCUrlInputField foundInput = targetRoot.GetComponentInChildren<VRCUrlInputField>(true);
            if (foundInput != null)
            {
                ctrl.urlInputField = foundInput;
                TryExtractSubmitEvent(foundInput, ctrl, out UdonBehaviour submitTarget, out string submitEvent);
                ctrl.playerSubmitTarget = submitTarget;
                ctrl.playerSubmitEvent = submitEvent;
            }
        }

        // 4) 纯舞台模式下自动适配目标播放器的屏幕尺寸与正面朝向（防镜像/防背面遮挡）
        if (ctrl.stageOnlyMode)
        {
            AutoFitToTargetScreen(ctrl, targetRoot);
        }

        UdonSharpEditorUtility.CopyProxyToUdon(ctrl);
        EditorUtility.SetDirty(ctrl);
        if (ctrl.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(ctrl.gameObject.scene);

        string ctrlName = ctrl.playerController != null
            ? ctrl.playerController.GetType().Name + " (" + ctrl.playerController.name + ")"
            : (ctrl.rawUdonPlayer != null ? "UdonBehaviour (" + ctrl.rawUdonPlayer.name + ")" : "纯底层 VRCVideoPlayer 模式");
        Debug.Log("[眠境音乐配置] 拖拽绑定完成 | 目标对象: " + targetRoot.name +
                  " | 控制器: " + ctrlName +
                  " | 底层 VideoPlayer: " + (vps != null ? vps.Length : 0) + " 个" +
                  " | 输入框: " + (ctrl.urlInputField != null ? ctrl.urlInputField.name : "无") +
                  " | 切歌事件: " + (string.IsNullOrEmpty(ctrl.playerSubmitEvent) ? "直调 PlayURL" : ctrl.playerSubmitEvent));
    }

    /// <summary>
    /// 自动探测目标播放器屏幕（RawImage 或 3D Quad/Plane MeshRenderer），精确对齐中心、尺寸与正面法线，并抵消父级负缩放防止画面反转。
    /// </summary>
    public static bool AutoFitToTargetScreen(SleepMusicLyricsController ctrl, GameObject targetRoot)
    {
        if (ctrl == null || targetRoot == null) return false;

        Transform stageRootT = ctrl.transform;
        Canvas stageCanvas = ctrl.GetComponent<Canvas>();
        if (stageCanvas == null && ctrl.musicStageRoot != null)
        {
            stageCanvas = ctrl.musicStageRoot.GetComponentInParent<Canvas>();
            if (stageCanvas != null) stageRootT = stageCanvas.transform;
        }
        RectTransform stageRect = stageRootT as RectTransform;
        float refW = (stageRect != null && stageRect.sizeDelta.x > 10f) ? stageRect.sizeDelta.x : 1920f;
        float refH = (stageRect != null && stageRect.sizeDelta.y > 10f) ? stageRect.sizeDelta.y : 1080f;

        Vector3 center;
        Vector3 frontNormal;
        Vector3 screenUp;
        float worldW;
        float worldH;
        string screenName;

        if (!TryDetectScreenGeometry(targetRoot, ctrl.gameObject, out center, out frontNormal, out screenUp, out worldW, out worldH, out screenName))
        {
            Debug.LogWarning("[眠境音乐配置] 未在目标对象「" + targetRoot.name + "」中找到可适配的屏幕（RawImage / MeshRenderer），请手动调整舞台位置与大小。");
            return false;
        }

        Undo.RecordObject(stageRootT, "自动适配目标播放器屏幕");
        // 向前偏移 1.5mm（紧贴视频画面前方，同时确保位于播放/暂停/输入框等控制 UI 之后）
        float forwardOffset = ComputeSafeForwardOffset(targetRoot, ctrl.gameObject, center, frontNormal);
        stageRootT.position = center + frontNormal * forwardOffset;
        // Unity UI Canvas 正面朝向 -transform.forward，因此 LookRotation(-frontNormal, screenUp) 确保正面永远朝向观众
        stageRootT.rotation = Quaternion.LookRotation(-frontNormal, screenUp);

        // 计算目标世界缩放并抵消父级缩放（含负缩放），确保绝不镜像反转
        Vector3 parentLossy = stageRootT.parent != null ? stageRootT.parent.lossyScale : Vector3.one;
        float sx = (worldW / refW) / (Mathf.Abs(parentLossy.x) > 1e-5f ? parentLossy.x : 1f);
        float sy = (worldH / refH) / (Mathf.Abs(parentLossy.y) > 1e-5f ? parentLossy.y : 1f);
        float sz = 0.001f / (Mathf.Abs(parentLossy.z) > 1e-5f ? parentLossy.z : 1f);
        stageRootT.localScale = new Vector3(sx, sy, sz);

        // 确保音乐封面与歌词图层始终渲染在「开始 / 暂停 / 进度条 / 输入框」等控制主线之后、视频画面之前
        EnsureBehindPlayerControls(stageCanvas, targetRoot, ctrl.gameObject);

        EditorUtility.SetDirty(stageRootT);
        Debug.Log("[眠境音乐配置] 屏幕自动适配完成 | 命中屏幕: " + screenName +
                  " | 尺寸: " + worldW.ToString("F2") + "m x " + worldH.ToString("F2") + "m" +
                  " | 前移: " + (forwardOffset * 1000f).ToString("F1") + "mm（已置于控制栏之后）");
        return true;
    }

    // 计算安全的屏幕前移距离：紧贴视频画面前方，若前方存在按钮/滑块/输入框控制平面则严格插在画面与控制平面之间
    private static float ComputeSafeForwardOffset(GameObject targetRoot, GameObject ignoreSubtree, Vector3 screenCenter, Vector3 frontNormal)
    {
        float minControlDist = float.MaxValue;
        Selectable[] controls = targetRoot.GetComponentsInChildren<Selectable>(true);
        for (int i = 0; i < controls.Length; i++)
        {
            Selectable s = controls[i];
            if (s == null || IsChildOf(s.transform, ignoreSubtree.transform)) continue;
            float d = Vector3.Dot(s.transform.position - screenCenter, frontNormal);
            if (d > 0.0005f && d < 0.15f && d < minControlDist)
            {
                minControlDist = d;
            }
        }
        if (minControlDist < 0.01f) return Mathf.Clamp(minControlDist * 0.4f, 0.0008f, 0.002f);
        return 0.0015f;
    }

    // 调整渲染层级，保证音乐舞台永远在播放/暂停/输入框等交互控件之后渲染，绝不遮挡操作
    private static void EnsureBehindPlayerControls(Canvas stageCanvas, GameObject targetRoot, GameObject ignoreSubtree)
    {
        if (stageCanvas == null || targetRoot == null) return;

        int minControlOrder = int.MaxValue;
        bool hasControlCanvas = false;
        Selectable[] controls = targetRoot.GetComponentsInChildren<Selectable>(true);
        for (int i = 0; i < controls.Length; i++)
        {
            Selectable s = controls[i];
            if (s == null || IsChildOf(s.transform, ignoreSubtree.transform)) continue;
            Canvas c = s.GetComponentInParent<Canvas>();
            if (c != null && c != stageCanvas)
            {
                hasControlCanvas = true;
                if (c.sortingOrder < minControlOrder) minControlOrder = c.sortingOrder;
            }
        }

        Undo.RecordObject(stageCanvas, "调整音乐舞台层级位于控制栏之后");
        if (hasControlCanvas)
        {
            stageCanvas.overrideSorting = true;
            stageCanvas.sortingOrder = minControlOrder - 1;
        }
        else
        {
            stageCanvas.overrideSorting = true;
            stageCanvas.sortingOrder = -1;
        }
        EditorUtility.SetDirty(stageCanvas);
    }

    /// <summary>一键将舞台朝向水平翻转 180°（用于个别自定义 Mesh 法线反向的播放器屏幕）。</summary>
    public static void FlipStageFacing180(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;
        Transform t = ctrl.transform;
        Undo.RecordObject(t, "翻转歌词舞台朝向 180°");
        // 将 4mm 偏移对称翻到另一侧并绕自身 Up 旋转 180°
        t.position += t.forward * 0.008f;
        t.Rotate(Vector3.up, 180f, Space.Self);
        EditorUtility.SetDirty(t);
        if (ctrl.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(ctrl.gameObject.scene);
        Debug.Log("[眠境音乐配置] 已将歌词舞台朝向翻转 180°。");
    }

    private static bool TryDetectScreenGeometry(
        GameObject targetRoot, GameObject ignoreSubtree,
        out Vector3 center, out Vector3 frontNormal, out Vector3 screenUp,
        out float worldW, out float worldH, out string screenName)
    {
        center = Vector3.zero;
        frontNormal = -Vector3.forward;
        screenUp = Vector3.up;
        worldW = 0f;
        worldH = 0f;
        screenName = "";

        float bestScore = -1f;
        Vector3[] corners = new Vector3[4];

        // 1) 优先查找面积最大的 RawImage 屏幕（VizVid / YamaPlayer / ProTV Canvas 屏幕）
        RawImage[] rawImages = targetRoot.GetComponentsInChildren<RawImage>(true);
        for (int i = 0; i < rawImages.Length; i++)
        {
            RawImage img = rawImages[i];
            if (img == null || IsChildOf(img.transform, ignoreSubtree.transform)) continue;
            img.rectTransform.GetWorldCorners(corners);
            Vector3 rightVec = corners[3] - corners[0];
            Vector3 upVec = corners[1] - corners[0];
            float w = rightVec.magnitude;
            float h = upVec.magnitude;
            if (w < 0.15f || h < 0.1f) continue;

            float score = w * h * NameScreenWeight(img.name);
            if (score > bestScore)
            {
                bestScore = score;
                center = (corners[0] + corners[2]) * 0.5f;
                frontNormal = -Vector3.Cross(rightVec, upVec).normalized;
                screenUp = upVec.normalized;
                worldW = w;
                worldH = h;
                screenName = img.name + " (RawImage)";
            }
        }

        // 2) 查找 3D 屏幕 MeshRenderer（Quad / Plane / 自定义显示器网格）
        MeshRenderer[] renderers = targetRoot.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            MeshRenderer mr = renderers[i];
            if (mr == null || IsChildOf(mr.transform, ignoreSubtree.transform)) continue;
            MeshFilter mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            Bounds localB = mf.sharedMesh.bounds;
            Vector3 lossy = mr.transform.lossyScale;
            float sx = Mathf.Abs(localB.size.x * lossy.x);
            float sy = Mathf.Abs(localB.size.y * lossy.y);
            float sz = Mathf.Abs(localB.size.z * lossy.z);

            // 判断最薄的轴作为屏幕法线轴（Quad 为 Z 轴最薄，Plane 为 Y 轴最薄）
            float w, h;
            Vector3 localNormal, localUp;
            if (sz <= sx && sz <= sy)
            {
                w = sx; h = sy;
                localNormal = -Vector3.forward;
                localUp = Vector3.up;
            }
            else if (sy <= sx && sy <= sz)
            {
                w = sx; h = sz;
                localNormal = Vector3.up;
                localUp = Vector3.forward;
            }
            else
            {
                w = sz; h = sy;
                localNormal = Vector3.right;
                localUp = Vector3.up;
            }

            if (w < 0.15f || h < 0.1f) continue;
            float score = w * h * NameScreenWeight(mr.name);
            if (score > bestScore)
            {
                bestScore = score;
                center = mr.transform.TransformPoint(localB.center);
                bool flippedHandedness = (lossy.x * lossy.y * lossy.z) < 0f;
                Vector3 n = mr.transform.TransformDirection(localNormal).normalized;
                if (flippedHandedness) n = -n;
                frontNormal = n;
                screenUp = mr.transform.TransformDirection(localUp).normalized;
                worldW = w;
                worldH = h;
                screenName = mr.name + " (MeshRenderer)";
            }
        }

        return bestScore > 0f;
    }

    private static float NameScreenWeight(string name)
    {
        if (string.IsNullOrEmpty(name)) return 1f;
        string low = name.ToLowerInvariant();
        if (low.Contains("screen") || low.Contains("display") || low.Contains("video") || low.Contains("monitor") || low.Contains("quad"))
            return 2.5f;
        if (low.Contains("icon") || low.Contains("btn") || low.Contains("button") || low.Contains("thumb"))
            return 0.1f;
        return 1f;
    }

    private static bool IsChildOf(Transform child, Transform parent)
    {
        if (child == null || parent == null) return false;
        Transform cur = child;
        while (cur != null)
        {
            if (cur == parent) return true;
            cur = cur.parent;
        }
        return false;
    }

    /// <summary>排查常见适配与播放问题（反面/负缩放、未绑定播放器、音频静音、材质缺失等）。</summary>
    public static string RunDiagnostics(SleepMusicLyricsController ctrl, out MessageType level)
    {
        level = MessageType.Info;
        if (ctrl == null) { level = MessageType.Error; return "控制器为空"; }

        StringBuilder sb = new StringBuilder();
        int warnings = 0;

        // 1) 检查底层播放器绑定
        int validVp = 0;
        if (ctrl.videoPlayers != null)
        {
            for (int i = 0; i < ctrl.videoPlayers.Length; i++)
                if (ctrl.videoPlayers[i] != null) validVp++;
        }
        if (validVp == 0)
        {
            sb.AppendLine("• [严重] 未绑定底层 BaseVRCVideoPlayer，歌词将无法同步播放进度！请拖拽目标播放器到上方绑定区。");
            warnings++;
            level = MessageType.Error;
        }

        // 2) 检查控制器 URL 读取源
        if (ctrl.playerController == null && ctrl.rawUdonPlayer == null && ctrl.urlInputField == null)
        {
            sb.AppendLine("• [提示] 未绑定播放器核心脚本或输入框，需由外部脚本调用 SetExternalUrl(VRCUrl) 推送歌曲链接。");
            warnings++;
            if (level == MessageType.Info) level = MessageType.Warning;
        }

        // 3) 检查负缩放导致的镜像/反面问题
        Vector3 lossy = ctrl.transform.lossyScale;
        if (lossy.x < 0f || lossy.y < 0f || lossy.z < 0f)
        {
            sb.AppendLine("• [警告] 检测到舞台存在负缩放 (" + lossy.ToString("F3") + ")，文字与封面将呈镜像或背面不可见！请点击「自动适配屏幕」或「翻转朝向 180°」。");
            warnings++;
            if (level == MessageType.Info) level = MessageType.Warning;
        }

        // 4) 检查目标播放器 AudioSource 静音/禁用
        if (ctrl.playerController != null)
        {
            AudioSource[] audios = ctrl.playerController.transform.root.GetComponentsInChildren<AudioSource>(true);
            if (audios.Length > 0)
            {
                bool allMuted = true;
                for (int i = 0; i < audios.Length; i++)
                {
                    if (audios[i] != null && audios[i].enabled && !audios[i].mute && audios[i].volume > 0.01f)
                    {
                        allMuted = false;
                        break;
                    }
                }
                if (allMuted)
                {
                    sb.AppendLine("• [警告] 目标播放器所有 AudioSource 均处于静音/关闭状态，可能导致播放无声。");
                    warnings++;
                    if (level == MessageType.Info) level = MessageType.Warning;
                }
            }
        }

        // 5) 检查 GPU 材质与 RenderTexture
        if (ctrl.bc1DecodeMaterial == null || ctrl.coverRenderTarget == null || ctrl.ambientBackdropMat == null || ctrl.albumCardMat == null)
        {
            sb.AppendLine("• [警告] 缺少 GPU 封面解码材质或 RT_MusicCover_512，封面与光晕背景将无法渲染。");
            warnings++;
            if (level == MessageType.Info) level = MessageType.Warning;
        }

        // 6) 检查音乐池烘焙
        if (ctrl.musicSlotUrls == null || ctrl.musicSlotUrls.Length == 0)
        {
            sb.AppendLine("• [提示] 音乐池槽位尚未烘焙，请点击「一键替换域名并同步音乐池」。");
            warnings++;
            if (level == MessageType.Info) level = MessageType.Warning;
        }

        if (warnings == 0)
        {
            return "诊断通过：播放器绑定、屏幕朝向、GPU 材质与音乐池均正常（" + (ctrl.stageOnlyMode ? "纯歌词封面舞台模式" : "完整控制栏模式") + "）。";
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>在 Inspector / 设置窗口绘制「拖拽任意播放器到此处自动绑定」交互区。</summary>
    public static void DrawPlayerDropArea(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;
        Rect dropRect = GUILayoutUtility.GetRect(0f, 44f, GUILayout.ExpandWidth(true));
        GUI.Box(dropRect, "将场景中任意播放器物体拖到此处，松手即自动绑定\n（支持 VizVid / ProTV / YamaPlayer / IWsync / USharpVideo / 原生 VRCVideoPlayer）", EditorStyles.helpBox);

        Event evt = Event.current;
        if (evt == null || !dropRect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            GameObject candidate = ExtractDraggedGameObject();
            if (candidate != null)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    BindFromTargetObject(ctrl, candidate);
                }
                evt.Use();
            }
        }
    }

    private static GameObject ExtractDraggedGameObject()
    {
        Object[] refs = DragAndDrop.objectReferences;
        if (refs == null || refs.Length == 0) return null;
        for (int i = 0; i < refs.Length; i++)
        {
            if (refs[i] is GameObject go && go.scene.IsValid()) return go;
            if (refs[i] is Component comp && comp.gameObject.scene.IsValid()) return comp.gameObject;
        }
        return null;
    }

    /// <summary>
    /// 兼容 UdonSharp 1.x：同时扫描已解包 Proxy 与尚未展开 Proxy 的 UdonBehaviour（解决 VVMW 等新拖入预制体搜不到的问题）。
    /// </summary>
    public static List<UdonSharpBehaviour> CollectUdonSharpBehaviours(GameObject rootOrNull)
    {
        List<UdonSharpBehaviour> list = new List<UdonSharpBehaviour>();
        HashSet<int> seen = new HashSet<int>();

        UdonSharpBehaviour[] direct = rootOrNull != null
            ? rootOrNull.GetComponentsInChildren<UdonSharpBehaviour>(true)
            : Object.FindObjectsOfType<UdonSharpBehaviour>(true);
        for (int i = 0; i < direct.Length; i++)
        {
            if (direct[i] != null && seen.Add(direct[i].GetInstanceID())) list.Add(direct[i]);
        }

        UdonBehaviour[] udons = rootOrNull != null
            ? rootOrNull.GetComponentsInChildren<UdonBehaviour>(true)
            : Object.FindObjectsOfType<UdonBehaviour>(true);
        for (int i = 0; i < udons.Length; i++)
        {
            if (udons[i] == null) continue;
            UdonSharpBehaviour proxy = UdonSharpEditorUtility.GetProxyBehaviour(udons[i]);
            if (proxy != null && seen.Add(proxy.GetInstanceID())) list.Add(proxy);
        }
        return list;
    }

    /// <summary>在场景中可靠查找 SleepMusicLyricsController（兼容未解包 UdonBehaviour 预制体）。</summary>
    public static SleepMusicLyricsController FindSceneController()
    {
        SleepMusicLyricsController direct = Object.FindObjectOfType<SleepMusicLyricsController>(true);
        if (direct != null) return direct;

        List<UdonSharpBehaviour> all = CollectUdonSharpBehaviours(null);
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] is SleepMusicLyricsController c) return c;
        }
        return null;
    }

    /// <summary>在场景中可靠查找全部 SleepMusicLyricsController。</summary>
    public static List<SleepMusicLyricsController> FindAllSceneControllers()
    {
        List<SleepMusicLyricsController> result = new List<SleepMusicLyricsController>();
        List<UdonSharpBehaviour> all = CollectUdonSharpBehaviours(null);
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] is SleepMusicLyricsController c) result.Add(c);
        }
        return result;
    }

    private static UdonSharpBehaviour FindBestPlayerControllerInHierarchy(GameObject root, SleepMusicLyricsController self)
    {
        string[] preferredNames = { "Core", "TVManager", "YamaPlayerController", "YamaPlayer", "USharpVideoPlayer", "VideoCore", "SyncVideoPlayer" };
        List<UdonSharpBehaviour> all = CollectUdonSharpBehaviours(root);
        UdonSharpBehaviour fallback = null;

        for (int i = 0; i < all.Count; i++)
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

    /// <summary>
    /// 一键自动扫描并绑定场景中任意 VRChat 视频播放器（VizVid / VVMW / ProTV / YamaPlayer / IWsync / USharpVideo / 原生 VRCVideoPlayer）。
    /// </summary>
    public static void AutoBindAnyPlayer(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;
        Undo.RecordObject(ctrl, "一键自动绑定任意视频播放器");

        // 1) 自动发现主流或自定义 UdonSharp 播放器核心脚本（兼容 VVMW 等未展开 Proxy 的预制体）
        if (ctrl.playerController == null)
        {
            ctrl.playerController = FindBestPlayerController(ctrl);
        }

        // 2) 自动收集底层 BaseVRCVideoPlayer（优先从已绑定控制器子树查找，否则全场景收集）
        List<BaseVRCVideoPlayer> foundPlayers = new List<BaseVRCVideoPlayer>();
        GameObject playerRoot = null;
        if (ctrl.playerController != null)
        {
            playerRoot = ctrl.playerController.transform.root != null ? ctrl.playerController.transform.root.gameObject : ctrl.playerController.gameObject;
            foundPlayers.AddRange(ctrl.playerController.GetComponentsInChildren<BaseVRCVideoPlayer>(true));
            if (foundPlayers.Count == 0 && playerRoot != null)
            {
                foundPlayers.AddRange(playerRoot.GetComponentsInChildren<BaseVRCVideoPlayer>(true));
            }
        }
        if (foundPlayers.Count == 0)
        {
            foundPlayers.AddRange(Object.FindObjectsOfType<BaseVRCVideoPlayer>(true));
            if (playerRoot == null && foundPlayers.Count > 0 && foundPlayers[0] != null)
            {
                playerRoot = foundPlayers[0].transform.root.gameObject;
            }
        }
        ctrl.videoPlayers = foundPlayers.ToArray();

        // 3) 非纯舞台模式时自动绑定输入框与提交事件
        if (!ctrl.stageOnlyMode)
        {
            if (ctrl.urlInputField == null)
            {
                if (playerRoot != null) ctrl.urlInputField = playerRoot.GetComponentInChildren<VRCUrlInputField>(true);
                if (ctrl.urlInputField == null) ctrl.urlInputField = Object.FindObjectOfType<VRCUrlInputField>(true);
            }
            if (ctrl.urlInputField != null && (ctrl.playerSubmitTarget == null || string.IsNullOrEmpty(ctrl.playerSubmitEvent)))
            {
                TryExtractSubmitEvent(ctrl.urlInputField, ctrl, out UdonBehaviour submitTarget, out string submitEvent);
                if (submitTarget != null && !string.IsNullOrEmpty(submitEvent))
                {
                    ctrl.playerSubmitTarget = submitTarget;
                    ctrl.playerSubmitEvent = submitEvent;
                }
            }
        }
        else if (playerRoot != null)
        {
            // 纯舞台模式：自动适配目标播放器屏幕尺寸并将音乐图层置于控制栏之后
            AutoFitToTargetScreen(ctrl, playerRoot);
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
        List<UdonSharpBehaviour> all = CollectUdonSharpBehaviours(null);
        UdonSharpBehaviour fallback = null;

        for (int i = 0; i < all.Count; i++)
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

        MessageType diagType;
        string diagMsg = SleepMusicConfigUtility.RunDiagnostics(ctrl, out diagType);
        EditorGUILayout.HelpBox(diagMsg, diagType);

        SleepMusicConfigUtility.DrawPlayerDropArea(ctrl);
        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("一键自动绑定场景任意播放器", GUILayout.Height(26)))
        {
            SleepMusicConfigUtility.AutoBindAnyPlayer(ctrl);
        }
        if (GUILayout.Button("自动适配屏幕大小", GUILayout.Width(118), GUILayout.Height(26)))
        {
            GameObject targetGo = ctrl.playerController != null ? ctrl.playerController.transform.root.gameObject
                : (ctrl.transform.parent != null ? ctrl.transform.parent.gameObject : null);
            if (targetGo != null) SleepMusicConfigUtility.AutoFitToTargetScreen(ctrl, targetGo);
            else EditorUtility.DisplayDialog("未指定目标播放器", "请先将目标播放器拖入上方绑定区，或将本舞台放到播放器物体下。", "确定");
        }
        if (GUILayout.Button("翻转 180°", GUILayout.Width(76), GUILayout.Height(26)))
        {
            SleepMusicConfigUtility.FlipStageFacing180(ctrl);
        }
        EditorGUILayout.EndHorizontal();
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
        SleepMusicLyricsController ctrl = SleepMusicConfigUtility.FindSceneController();
        if (ctrl == null)
        {
            // 若场景中尚未创建纯舞台预制体，自动实例化并立即完成绑定与屏幕适配
            SleepMusicPrefabMenu.CreateStageOnlyInScene();
            ctrl = SleepMusicConfigUtility.FindSceneController();
            if (ctrl == null)
            {
                EditorUtility.DisplayDialog("未找到控制器", "无法创建或找到 SleepMusicLyricsController，请先从菜单创建纯歌词与封面舞台。", "确定");
                return;
            }
        }
        SleepMusicConfigUtility.AutoBindAnyPlayer(ctrl);
        Selection.activeGameObject = ctrl.gameObject;
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
        if (_target == null) _target = SleepMusicConfigUtility.FindSceneController();
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
        SleepMusicConfigUtility.DrawPlayerDropArea(_target);
        EditorGUILayout.Space(2);
        if (GUILayout.Button("一键自动绑定场景内任意播放器（VizVid / ProTV / Yama / 原生等）", GUILayout.Height(28)))
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


/// <summary>快捷菜单：在场景层级中一键生成纯歌词封面舞台或完整播放器。</summary>
public static class SleepMusicPrefabMenu
{
    private const string StageOnlyPrefabPath = "Packages/com.miaobai.sleep-music-player/Prefabs/SleepMusicStage (Lyrics & Cover Only).prefab";
    private const string PackagePrefabPath = "Packages/com.miaobai.sleep-music-player/Prefabs/SleepMusicPlayer (All-in-One).prefab";

    [MenuItem("GameObject/眠境音乐播放器/创建纯歌词与封面舞台（无控件栏，适配任意播放器）", false, 9)]
    [MenuItem("Tools/眠境音乐播放器/创建纯歌词与封面舞台（无控件栏）", false, 0)]
    public static void CreateStageOnlyInScene()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(StageOnlyPrefabPath);
        if (prefab == null)
        {
            EditorUtility.DisplayDialog("未找到预制体", "请确认包内纯舞台预制体存在：" + StageOnlyPrefabPath, "确定");
            return;
        }
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(inst, "创建眠境纯歌词与封面舞台");
        Selection.activeGameObject = inst;
        Debug.Log("[眠境音乐播放器] 已创建纯歌词与封面舞台（无底部控件栏）。直接拖到任意播放器物体下成为子物体，或在 Inspector 拖拽目标播放器即可自动绑定！");
    }

    [MenuItem("GameObject/眠境音乐播放器/创建完整版播放器（含双排胶囊控件栏）", false, 10)]
    [MenuItem("Tools/眠境音乐播放器/在场景中创建完整版播放器", false, 1)]
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

/// <summary>
/// 层级拖拽自动绑定：当用户将眠境播放器拖到场景中任意播放器物体下成为其子物体时，自动识别并绑定该播放器。
/// </summary>
[InitializeOnLoad]
public static class SleepMusicHierarchyAutoBinder
{
    private static readonly Dictionary<int, int> LastParentMap = new Dictionary<int, int>();

    static SleepMusicHierarchyAutoBinder()
    {
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }

    private static void OnHierarchyChanged()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        List<SleepMusicLyricsController> controllers = SleepMusicConfigUtility.FindAllSceneControllers();
        for (int i = 0; i < controllers.Count; i++)
        {
            SleepMusicLyricsController ctrl = controllers[i];
            if (ctrl == null || !ctrl.gameObject.scene.IsValid()) continue;

            int id = ctrl.GetInstanceID();
            int parentId = ctrl.transform.parent != null ? ctrl.transform.parent.GetInstanceID() : 0;

            if (!LastParentMap.TryGetValue(id, out int prevParentId))
            {
                LastParentMap[id] = parentId;
                continue;
            }

            if (parentId == prevParentId) continue;
            LastParentMap[id] = parentId;

            if (ctrl.transform.parent == null) continue;
            GameObject parentGo = ctrl.transform.parent.gameObject;
            if (parentGo.GetComponentInChildren<BaseVRCVideoPlayer>(true) != null)
            {
                SleepMusicConfigUtility.BindFromTargetObject(ctrl, parentGo);
            }
        }
    }
}
