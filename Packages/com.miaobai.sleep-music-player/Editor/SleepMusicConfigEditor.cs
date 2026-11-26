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

        // 将 Proxy 字段同步写入底层 UdonBehaviour（同步前自动清理无效跨预制体 Proxy 引用，防止 UdonSharpBehaviourSerializer 空引用）
        SafeCopyProxyToUdon(ctrl);
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

        // 1) 在目标播放器子树中查找核心 UdonSharpBehaviour，并直接绑定其底层 UdonBehaviour（避免跨预制体 Proxy 序列化报错）
        UdonSharpBehaviour foundCtrl = FindBestPlayerControllerInHierarchy(targetRoot, ctrl);
        if (foundCtrl != null)
        {
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(foundCtrl);
            if (backing != null)
            {
                ctrl.rawUdonPlayer = backing;
                ctrl.playerController = null;
            }
            else
            {
                ctrl.playerController = foundCtrl;
                ctrl.rawUdonPlayer = null;
            }
        }
        else
        {
            ctrl.playerController = null;
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

        // 2) 收集目标播放器子树内全部底层 BaseVRCVideoPlayer（AVPro / Unity）
        BaseVRCVideoPlayer[] vps = targetRoot.GetComponentsInChildren<BaseVRCVideoPlayer>(true);
        ctrl.videoPlayers = vps;

        // 3) 自动绑定目标播放器内的 VRCUrlInputField 与提交事件，并自动填充默认直链前缀（若当前为空）
        VRCUrlInputField foundInput = targetRoot.GetComponentInChildren<VRCUrlInputField>(true);
        if (foundInput == null) foundInput = Object.FindObjectOfType<VRCUrlInputField>(true);
        if (foundInput != null)
        {
            ctrl.urlInputField = foundInput;
            TryExtractSubmitEvent(foundInput, ctrl, out UdonBehaviour submitTarget, out string submitEvent);
            ctrl.playerSubmitTarget = submitTarget;
            ctrl.playerSubmitEvent = submitEvent;
            EnsureInputPrefixInitialized(ctrl);
        }

        // 4) 纯舞台模式下自动适配目标播放器的屏幕尺寸与正面朝向（防镜像/防背面遮挡）
        if (ctrl.stageOnlyMode)
        {
            AutoFitToTargetScreen(ctrl, targetRoot);
        }

        SafeCopyProxyToUdon(ctrl);
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
        float detectedCurveDeg;
        string screenName;

        if (!TryDetectScreenGeometry(targetRoot, ctrl.gameObject, out center, out frontNormal, out screenUp, out worldW, out worldH, out detectedCurveDeg, out screenName))
        {
            Debug.LogWarning("[眠境音乐配置] 未在目标对象「" + targetRoot.name + "」中找到可适配的屏幕（RawImage / MeshRenderer），请手动调整舞台位置与大小。");
            return false;
        }

        Undo.RecordObject(stageRootT, "自动适配目标播放器屏幕");
        Undo.RecordObject(ctrl, "自动适配目标播放器屏幕与曲率");

        // 按真实屏幕宽高比动态调整 Canvas 画布逻辑尺寸（保持 1:1 等比缩放，避免 21:9 / 32:9 宽屏把正方形封面拉扁）
        float aspect = worldW / Mathf.Max(0.05f, worldH);
        float targetCanvasH = 1080f;
        float targetCanvasW = Mathf.Clamp(Mathf.Round(targetCanvasH * aspect), 1200f, 3840f);
        if (stageRect != null)
        {
            stageRect.sizeDelta = new Vector2(targetCanvasW, targetCanvasH);
            refW = targetCanvasW;
            refH = targetCanvasH;
        }
        // 确保 musicStageRoot（若为 Canvas 子节点）完全撑满 Canvas，不残留固定 1600x900 尺寸
        if (ctrl.musicStageRoot != null && ctrl.musicStageRoot.transform != stageRootT)
        {
            RectTransform mRt = ctrl.musicStageRoot.GetComponent<RectTransform>();
            if (mRt != null)
            {
                Undo.RecordObject(mRt, "撑满自适应舞台容器");
                mRt.anchorMin = Vector2.zero;
                mRt.anchorMax = Vector2.one;
                mRt.pivot = new Vector2(0.5f, 0.5f);
                mRt.anchoredPosition = Vector2.zero;
                mRt.sizeDelta = Vector2.zero;
            }
        }

        // 将音乐舞台严格放在视频画面正前方 2.0mm（曲面屏自动留出 4.0mm 安全间隙防穿模）
        float stageForwardOffset = Mathf.Abs(detectedCurveDeg) > 1f ? 0.004f : 0.002f;
        stageRootT.position = center + frontNormal * stageForwardOffset;
        // Unity UI Canvas 正面朝向 -transform.forward，因此 LookRotation(-frontNormal, screenUp) 确保正面永远朝向观众
        stageRootT.rotation = Quaternion.LookRotation(-frontNormal, screenUp);

        // 精确按各轴世界缩放换算 localScale（即使父节点存在非等比缩放或旋转也能 100% 严丝合缝贴合曲面弧长与高度）
        float desiredWorldX = worldW / refW;
        float desiredWorldY = worldH / refH;
        float desiredWorldZ = desiredWorldY;
        if (stageRootT.parent != null)
        {
            Transform p = stageRootT.parent;
            Vector3 pRight = p.InverseTransformVector(stageRootT.right);
            Vector3 pUp = p.InverseTransformVector(stageRootT.up);
            Vector3 pFwd = p.InverseTransformVector(stageRootT.forward);
            float sx = desiredWorldX * pRight.magnitude;
            float sy = desiredWorldY * pUp.magnitude;
            float sz = desiredWorldZ * pFwd.magnitude;
            // 若父节点含奇数次负缩放导致镜像，通过 X 轴符号修正
            Vector3 pl = p.lossyScale;
            if ((pl.x * pl.y * pl.z) < 0f) sx = -sx;
            stageRootT.localScale = new Vector3(sx, sy, sz);
        }
        else
        {
            stageRootT.localScale = new Vector3(desiredWorldX, desiredWorldY, desiredWorldZ);
        }

        // 自动识别场景内播放器的控制主线（开始/暂停/进度条/输入框 Canvas），自动前移至舞台前方 +5mm 并提高层级
        int shiftedControls = EnsureControlsInFrontOfStage(stageCanvas, targetRoot, ctrl.gameObject, center, frontNormal, stageForwardOffset);

        // 同步识别到的曲面屏弧度，并在确定 stageCanvas.sortingOrder 后刷新排版与曲面细分网格排序
        ctrl.screenCurveDegrees = Mathf.Clamp(Mathf.Round(detectedCurveDeg * 10f) * 0.1f, -90f, 90f);
        EnsureCurvedAssetsAndLayout(ctrl);

        EditorUtility.SetDirty(stageRootT);
        EditorUtility.SetDirty(ctrl);
        Debug.Log("[眠境音乐配置] 屏幕自动适配完成 | 命中屏幕: " + screenName +
                  " | 物理尺寸: " + worldW.ToString("F2") + "m x " + worldH.ToString("F2") + "m" +
                  " | 画布: " + targetCanvasW.ToString("F0") + "x" + targetCanvasH.ToString("F0") +
                  " | 曲率: " + ctrl.screenCurveDegrees.ToString("F1") + "°" +
                  " | 已自动前移控制栏 Canvas: " + shiftedControls + " 个");
        return true;
    }

    /// <summary>
    /// 确保曲面文字材质与 32 列细分曲面网格已就绪，并立即刷新全屏自适应排版与曲率预览。
    /// </summary>
    public static void EnsureCurvedAssetsAndLayout(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;

        // 1) 自动绑定曲面文字材质（副歌词 / 歌名 / 歌手 / 翻译 / 中线）
        if (ctrl.curvedTextMat == null)
        {
            const string pkgMatPath = "Packages/com.miaobai.sleep-music-player/Materials/Mat_CurvedUIText.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(pkgMatPath);
            if (mat == null)
            {
                Shader sh = Shader.Find("SleepSanctuary/GPU/CurvedUIText");
                if (sh != null)
                {
                    mat = new Material(sh);
                    string saveDir = AssetDatabase.IsValidFolder("Packages/com.miaobai.sleep-music-player/Materials")
                        ? "Packages/com.miaobai.sleep-music-player/Materials"
                        : "Assets/SleepMusicCurved";
                    if (saveDir == "Assets/SleepMusicCurved" && !AssetDatabase.IsValidFolder(saveDir))
                        AssetDatabase.CreateFolder("Assets", "SleepMusicCurved");
                    string savePath = saveDir + "/Mat_CurvedUIText.mat";
                    try
                    {
                        AssetDatabase.CreateAsset(mat, savePath);
                        AssetDatabase.SaveAssets();
                        mat = AssetDatabase.LoadAssetAtPath<Material>(savePath);
                    }
                    catch
                    {
                        if (!AssetDatabase.IsValidFolder("Assets/SleepMusicCurved"))
                            AssetDatabase.CreateFolder("Assets", "SleepMusicCurved");
                        AssetDatabase.CreateAsset(mat, "Assets/SleepMusicCurved/Mat_CurvedUIText.mat");
                        AssetDatabase.SaveAssets();
                        mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/SleepMusicCurved/Mat_CurvedUIText.mat");
                    }
                }
            }
            if (mat != null) ctrl.curvedTextMat = mat;
        }

        // 2) 当启用曲面屏时，自动为背景与封面准备 32 列细分网格（替代 4 顶点 RawImage，避免曲面折平）
        if (ctrl.musicStageRoot != null && (Mathf.Abs(ctrl.screenCurveDegrees) > 0.25f || ctrl.curvedBackdropRenderer != null || ctrl.curvedCoverRenderer != null))
        {
            Mesh subdivMesh = GetOrCreateSubdividedQuadMesh();
            if (subdivMesh != null)
            {
                Transform rootT = ctrl.musicStageRoot.transform;
                if (ctrl.flatBackdropImage == null)
                {
                    Transform bgT = rootT.Find("GPU_AmbientBackdrop");
                    if (bgT != null) ctrl.flatBackdropImage = bgT.GetComponent<RawImage>();
                }
                if (ctrl.flatCoverImage == null && ctrl.coverCardRect != null)
                {
                    ctrl.flatCoverImage = ctrl.coverCardRect.GetComponent<RawImage>();
                }
                if (ctrl.curvedBackdropRenderer == null)
                {
                    ctrl.curvedBackdropRenderer = CreateOrFindCurvedMeshChild(rootT, "GPU_CurvedBackdropMesh", subdivMesh, ctrl.ambientBackdropMat, 0);
                }
                if (ctrl.curvedCoverRenderer == null)
                {
                    ctrl.curvedCoverRenderer = CreateOrFindCurvedMeshChild(rootT, "GPU_CurvedCoverMesh", subdivMesh, ctrl.albumCardMat, 1);
                }
            }
        }

        Canvas.ForceUpdateCanvases();
        ctrl.ApplyResponsiveLayout();
        if (ctrl.musicStageRoot != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(ctrl.musicStageRoot.GetComponent<RectTransform>());
        }
        Canvas.ForceUpdateCanvases();
        EditorUtility.SetDirty(ctrl);
        SceneView.RepaintAll();
    }

    private static MeshRenderer CreateOrFindCurvedMeshChild(Transform parent, string childName, Mesh mesh, Material mat, int siblingIndex)
    {
        Transform existing = parent.Find(childName);
        GameObject go = existing != null ? existing.gameObject : new GameObject(childName);
        if (existing == null)
        {
            Undo.RegisterCreatedObjectUndo(go, "创建曲面屏细分网格 " + childName);
            go.transform.SetParent(parent, false);
            go.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parent.childCount - 1));
        }
        go.layer = parent.gameObject.layer;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf == null) mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr == null) mr = go.AddComponent<MeshRenderer>();
        if (mat != null) mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return mr;
    }

    private static Mesh GetOrCreateSubdividedQuadMesh()
    {
        const string pkgMeshPath = "Packages/com.miaobai.sleep-music-player/Materials/QuadSubdiv32.asset";
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(pkgMeshPath);
        if (existing != null) return existing;

        const string fallbackPath = "Assets/SleepMusicCurved/QuadSubdiv32.asset";
        existing = AssetDatabase.LoadAssetAtPath<Mesh>(fallbackPath);
        if (existing != null) return existing;

        const int cols = 32;
        Vector3[] verts = new Vector3[(cols + 1) * 2];
        Vector2[] uvs = new Vector2[(cols + 1) * 2];
        int[] tris = new int[cols * 6];

        for (int i = 0; i <= cols; i++)
        {
            float u = (float)i / cols;
            float x = u - 0.5f;
            verts[i * 2] = new Vector3(x, -0.5f, 0f);
            verts[i * 2 + 1] = new Vector3(x, 0.5f, 0f);
            uvs[i * 2] = new Vector2(u, 0f);
            uvs[i * 2 + 1] = new Vector2(u, 1f);
        }
        for (int i = 0; i < cols; i++)
        {
            int v = i * 2;
            int t = i * 6;
            tris[t] = v;
            tris[t + 1] = v + 1;
            tris[t + 2] = v + 2;
            tris[t + 3] = v + 2;
            tris[t + 4] = v + 1;
            tris[t + 5] = v + 3;
        }

        Mesh mesh = new Mesh { name = "QuadSubdiv32" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        // 扩大包围盒，确保 GPU 顶点弯曲偏移后绝不被视锥剔除
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(4500f, 2200f, 2200f));

        try
        {
            if (AssetDatabase.IsValidFolder("Packages/com.miaobai.sleep-music-player/Materials"))
            {
                AssetDatabase.CreateAsset(mesh, pkgMeshPath);
                AssetDatabase.SaveAssets();
                return AssetDatabase.LoadAssetAtPath<Mesh>(pkgMeshPath);
            }
        }
        catch { }

        if (!AssetDatabase.IsValidFolder("Assets/SleepMusicCurved"))
            AssetDatabase.CreateFolder("Assets", "SleepMusicCurved");
        AssetDatabase.CreateAsset(mesh, fallbackPath);
        AssetDatabase.SaveAssets();
        return AssetDatabase.LoadAssetAtPath<Mesh>(fallbackPath);
    }

    // 自动识别播放器控制栏 Canvas（如 VVMW 的 MainUI / Control Panel Root），若离屏幕太近（<4.5mm）则自动前移至 +5.0mm，
    // 确保渲染顺序严格为：视频画面 (0mm) < 音乐封面歌词舞台 (+2mm) < 开始/暂停/输入框控制栏 (+5mm)。
    private static int EnsureControlsInFrontOfStage(Canvas stageCanvas, GameObject targetRoot, GameObject ignoreSubtree, Vector3 screenCenter, Vector3 frontNormal, float stageOffset)
    {
        if (targetRoot == null) return 0;

        HashSet<Canvas> controlCanvases = new HashSet<Canvas>();
        Selectable[] controls = targetRoot.GetComponentsInChildren<Selectable>(true);
        for (int i = 0; i < controls.Length; i++)
        {
            Selectable s = controls[i];
            if (s == null || IsChildOf(s.transform, ignoreSubtree.transform)) continue;
            Canvas c = s.GetComponentInParent<Canvas>();
            if (c != null && c != stageCanvas && !IsChildOf(c.transform, ignoreSubtree.transform))
            {
                // 取包含控件的最外层独立 WorldSpace Canvas（如 VVMW 的 MainUI）
                Canvas rootCanvas = c.rootCanvas != null ? c.rootCanvas : c;
                if (rootCanvas != stageCanvas && IsChildOf(rootCanvas.transform, targetRoot.transform))
                {
                    controlCanvases.Add(rootCanvas);
                }
                else
                {
                    controlCanvases.Add(c);
                }
            }
        }

        int minControlOrder = int.MaxValue;
        int shiftedCount = 0;
        const float targetControlOffset = 0.005f;

        foreach (Canvas cc in controlCanvases)
        {
            if (cc.sortingOrder < minControlOrder) minControlOrder = cc.sortingOrder;

            // 若控制栏 Canvas 紧贴屏幕（例如 VVMW MainUI 默认在 +1mm，会落在舞台 +2mm 后面），自动将其沿正面法线前移至 +5mm
            float curDist = Vector3.Dot(cc.transform.position - screenCenter, frontNormal);
            if (curDist >= -0.02f && curDist < targetControlOffset - 0.0005f)
            {
                Undo.RecordObject(cc.transform, "自动前移播放器控制栏至音乐舞台前方");
                cc.transform.position += frontNormal * (targetControlOffset - curDist);
                EditorUtility.SetDirty(cc.transform);
                shiftedCount++;
            }
        }

        if (stageCanvas != null)
        {
            Undo.RecordObject(stageCanvas, "调整音乐舞台层级位于控制栏之后");
            stageCanvas.overrideSorting = true;
            stageCanvas.sortingOrder = controlCanvases.Count > 0 ? (minControlOrder - 1) : -1;
            EditorUtility.SetDirty(stageCanvas);
        }
        return shiftedCount;
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
        out float worldW, out float worldH, out float detectedCurveDegrees, out string screenName)
    {
        center = Vector3.zero;
        frontNormal = -Vector3.forward;
        screenUp = Vector3.up;
        worldW = 0f;
        worldH = 0f;
        detectedCurveDegrees = 0f;
        screenName = "";

        float bestScore = -1f;
        Vector3[] corners = new Vector3[4];

        // 1) 查找面积最大的 RawImage 屏幕（VizVid / YamaPlayer / ProTV Canvas 屏幕）
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
                detectedCurveDegrees = 0f;
                screenName = img.name + " (RawImage)";
            }
        }

        // 2) 查找 3D 屏幕 MeshRenderer（支持平面 Quad/Plane 以及任意弧度的曲面屏网格）
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

            // 判断最薄轴为法线轴（支持带弧度的曲面屏，其厚度可达宽度的 42%）
            float w, h, thickness;
            Vector3 localRight, localUp, localNormal;
            if (sz <= sx && sz <= sy)
            {
                w = sx; h = sy; thickness = sz;
                localRight = Vector3.right;
                localUp = Vector3.up;
                localNormal = -Vector3.forward;
            }
            else if (sy <= sx && sy <= sz)
            {
                w = sx; h = sz; thickness = sy;
                localRight = Vector3.right;
                localUp = Vector3.forward;
                localNormal = Vector3.up;
            }
            else
            {
                w = sz; h = sy; thickness = sx;
                localRight = Vector3.forward;
                localUp = Vector3.up;
                localNormal = Vector3.right;
            }

            if (w < 0.25f || h < 0.15f) continue;

            bool flippedHandedness = (lossy.x * lossy.y * lossy.z) < 0f;
            Vector3 worldNormal = mr.transform.TransformDirection(localNormal).normalized;
            if (flippedHandedness) worldNormal = -worldNormal;
            Vector3 worldRight = mr.transform.TransformDirection(localRight).normalized;
            Vector3 worldUp = mr.transform.TransformDirection(localUp).normalized;

            float curveDeg = 0f;
            float arcWidth = w;
            Vector3 candCenter = mr.transform.TransformPoint(localB.center);

            // 若网格有一定厚度，检测其顶点是否构成对称水平圆弧（曲面屏自动识别）
            if (thickness > 0.015f)
            {
                if (thickness > w * 0.45f) continue;
                bool isCurvedScreen = TryAnalyzeCurvedMesh(
                    mf.sharedMesh, mr.transform, worldRight, worldNormal, w,
                    out curveDeg, out arcWidth, out candCenter);
                if (!isCurvedScreen && thickness > 0.08f) continue;
            }

            float aspect = arcWidth / Mathf.Max(0.01f, h);
            float aspectWeight = (aspect >= 1.15f && aspect <= 3.8f) ? 1.8f : 0.7f;
            float shaderWeight = 1.0f;
            if (mr.sharedMaterial != null && mr.sharedMaterial.shader != null)
            {
                string sh = mr.sharedMaterial.shader.name.ToLowerInvariant();
                if (sh.Contains("video") || sh.Contains("screen") || sh.Contains("vvmw") || sh.Contains("tv") || sh.Contains("avpro"))
                    shaderWeight = 3.0f;
            }
            float score = arcWidth * h * NameScreenWeight(mr.name) * aspectWeight * shaderWeight;
            if (score > bestScore)
            {
                bestScore = score;
                center = candCenter;
                frontNormal = worldNormal;
                screenUp = worldUp;
                worldW = arcWidth;
                worldH = h;
                detectedCurveDegrees = curveDeg;
                screenName = mr.name + (Mathf.Abs(curveDeg) > 1f ? " (曲面屏 " + curveDeg.ToString("F0") + "°)" : " (MeshRenderer)");
            }
        }

        return bestScore > 0f;
    }

    // 采样网格顶点判断是否为水平圆弧曲面屏，精确反推弧度 θ = 4*atan(2s/c)、真实弧长与曲面顶点中心
    private static bool TryAnalyzeCurvedMesh(
        Mesh mesh, Transform t, Vector3 worldRight, Vector3 worldNormal, float chordHint,
        out float curveDegrees, out float arcWidth, out Vector3 apexCenter)
    {
        curveDegrees = 0f;
        arcWidth = chordHint;
        apexCenter = t.TransformPoint(mesh.bounds.center);

        if (mesh == null || !mesh.isReadable || mesh.vertexCount < 6) return false;
        Vector3[] verts = mesh.vertices;
        Vector3 origin = t.TransformPoint(mesh.bounds.center);

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float[] px = new float[verts.Length];
        float[] pz = new float[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 wp = t.TransformPoint(verts[i]) - origin;
            float x = Vector3.Dot(wp, worldRight);
            float z = Vector3.Dot(wp, worldNormal);
            px[i] = x;
            pz[i] = z;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
        }

        float chord = maxX - minX;
        if (chord < 0.2f) return false;
        float midX = (minX + maxX) * 0.5f;
        // 仅采样最左/最右端点（2.5% 带宽）与正中顶点（3.5% 带宽），避免大带宽平均导致矢高被低估、弧度偏小
        float edgeBand = chord * 0.025f;
        float midBand = chord * 0.04f;

        float sumLeft = 0f, sumMid = 0f, sumRight = 0f;
        int cntLeft = 0, cntMid = 0, cntRight = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            if (px[i] <= minX + edgeBand) { sumLeft += pz[i]; cntLeft++; }
            else if (px[i] >= maxX - edgeBand) { sumRight += pz[i]; cntRight++; }
            if (Mathf.Abs(px[i] - midX) <= midBand) { sumMid += pz[i]; cntMid++; }
        }
        // 若细分较少导致中点带宽未命中，退化为取最接近 midX 的前若干顶点
        if (cntMid == 0)
        {
            float bestDist = float.MaxValue;
            for (int i = 0; i < verts.Length; i++)
            {
                float d = Mathf.Abs(px[i] - midX);
                if (d < bestDist) { bestDist = d; sumMid = pz[i]; cntMid = 1; }
            }
        }
        if (cntLeft == 0 || cntMid == 0 || cntRight == 0) return false;

        float zLeft = sumLeft / cntLeft;
        float zRight = sumRight / cntRight;
        float zMid = sumMid / cntMid;
        float zEdge = (zLeft + zRight) * 0.5f;
        // signedSagitta > 0 表示两侧比中心更靠近观众（内凹环绕曲面屏）
        float signedSagitta = zEdge - zMid;
        float absSagitta = Mathf.Abs(signedSagitta);

        if (absSagitta < chord * 0.015f) return false;
        if (Mathf.Abs(zLeft - zRight) > Mathf.Max(0.03f, absSagitta * 0.35f)) return false;

        // 精确圆弧公式：θ = 4 * atan(2 * s / c)
        float thetaRad = 4f * Mathf.Atan((2f * absSagitta) / chord);
        float deg = thetaRad * Mathf.Rad2Deg;
        if (deg < 3f || deg > 120f) return false;

        float halfTheta = thetaRad * 0.5f;
        float radius = (chord * 0.5f) / Mathf.Max(0.001f, Mathf.Sin(halfTheta));
        arcWidth = radius * thetaRad;
        curveDegrees = signedSagitta >= 0f ? deg : -deg;
        // 将舞台锚点精准放在曲面中心顶点表面上
        apexCenter = origin + worldNormal * zMid;
        return true;
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

        // 1) 自动发现主流或自定义 UdonSharp 播放器核心脚本，并直接绑定其底层 UdonBehaviour（彻底避免跨预制体 Proxy 序列化报错）
        UdonSharpBehaviour detectedCtrl = ctrl.playerController != null ? ctrl.playerController : FindBestPlayerController(ctrl);
        if (detectedCtrl != null)
        {
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(detectedCtrl);
            if (backing != null)
            {
                ctrl.rawUdonPlayer = backing;
                ctrl.playerController = null;
            }
        }

        // 2) 自动收集底层 BaseVRCVideoPlayer（优先从已绑定控制器子树查找，否则全场景收集）
        List<BaseVRCVideoPlayer> foundPlayers = new List<BaseVRCVideoPlayer>();
        GameObject playerRoot = null;
        Component anchorComp = (Component)ctrl.rawUdonPlayer ?? (Component)ctrl.playerController;
        if (anchorComp != null)
        {
            playerRoot = anchorComp.transform.root != null ? anchorComp.transform.root.gameObject : anchorComp.gameObject;
            foundPlayers.AddRange(anchorComp.GetComponentsInChildren<BaseVRCVideoPlayer>(true));
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

        // 3) 无论是否纯舞台模式，均自动绑定播放器输入框与提交事件，并初始化默认直链前缀
        if (ctrl.urlInputField == null)
        {
            if (playerRoot != null) ctrl.urlInputField = playerRoot.GetComponentInChildren<VRCUrlInputField>(true);
            if (ctrl.urlInputField == null) ctrl.urlInputField = Object.FindObjectOfType<VRCUrlInputField>(true);
        }
        if (ctrl.urlInputField != null)
        {
            if (ctrl.playerSubmitTarget == null || string.IsNullOrEmpty(ctrl.playerSubmitEvent))
            {
                TryExtractSubmitEvent(ctrl.urlInputField, ctrl, out UdonBehaviour submitTarget, out string submitEvent);
                if (submitTarget != null && !string.IsNullOrEmpty(submitEvent))
                {
                    ctrl.playerSubmitTarget = submitTarget;
                    ctrl.playerSubmitEvent = submitEvent;
                }
            }
            EnsureInputPrefixInitialized(ctrl);
        }
        if (ctrl.stageOnlyMode)
        {
            // 纯舞台模式：优先适配播放器子树内的平面/曲面屏幕；若屏幕独立于播放器外则自动全场景识别最大屏幕
            bool fitted = playerRoot != null && AutoFitToTargetScreen(ctrl, playerRoot);
            if (!fitted)
            {
                AutoFitToBestSceneScreen(ctrl);
            }
        }
        else
        {
            EnsureCurvedAssetsAndLayout(ctrl);
        }

        SafeCopyProxyToUdon(ctrl);
        EditorUtility.SetDirty(ctrl);
        if (ctrl.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(ctrl.gameObject.scene);

        string ctrlName = ctrl.playerController != null ? ctrl.playerController.GetType().Name + " (" + ctrl.playerController.name + ")" : (ctrl.rawUdonPlayer != null ? "UdonBehaviour (" + ctrl.rawUdonPlayer.name + ")" : "纯底层 VRCVideoPlayer 模式");
        Debug.Log("[眠境音乐配置] 自动绑定播放器完成 | 控制器: " + ctrlName +
                  " | 底层 VideoPlayer: " + (ctrl.videoPlayers != null ? ctrl.videoPlayers.Length : 0) + " 个" +
                  " | 输入框: " + (ctrl.urlInputField != null ? ctrl.urlInputField.name : "无") +
                  " | 切歌事件: " + (string.IsNullOrEmpty(ctrl.playerSubmitEvent) ? "直调 PlayURL" : ctrl.playerSubmitEvent));
    }

    /// <summary>在全场景根物体中自动寻找最像视频屏幕（含曲面屏）的对象并一键适配。</summary>
    public static bool AutoFitToBestSceneScreen(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null || !ctrl.gameObject.scene.IsValid()) return false;
        GameObject[] roots = ctrl.gameObject.scene.GetRootGameObjects();
        float bestArea = -1f;
        GameObject bestRoot = null;
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject r = roots[i];
            if (r == null || r == ctrl.gameObject || IsChildOf(r.transform, ctrl.transform)) continue;
            if (TryDetectScreenGeometry(r, ctrl.gameObject, out _, out _, out _, out float w, out float h, out float deg, out _))
            {
                float score = w * h * (Mathf.Abs(deg) > 1f ? 1.5f : 1.0f);
                if (score > bestArea)
                {
                    bestArea = score;
                    bestRoot = r;
                }
            }
        }
        return bestRoot != null && AutoFitToTargetScreen(ctrl, bestRoot);
    }

    /// <summary>安全将 Proxy 同步至底层 UdonBehaviour：同步前自动将跨预制体 UdonSharpBehaviour 迁移为直接 UdonBehaviour 引用，防止 UdonSharpBehaviourSerializer 空引用。</summary>
    public static void SafeCopyProxyToUdon(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null) return;
        if (ctrl.playerController != null)
        {
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(ctrl.playerController);
            if (backing != null)
            {
                ctrl.rawUdonPlayer = backing;
                ctrl.playerController = null;
            }
            else
            {
                // 无法解析底层 UdonBehaviour 的悬空 Proxy 必须清空，否则 CopyProxyToUdon 会抛出 NullReferenceException
                ctrl.playerController = null;
            }
        }
        UdonSharpEditorUtility.CopyProxyToUdon(ctrl);
    }

    private static void EnsureInputPrefixInitialized(SleepMusicLyricsController ctrl)
    {
        if (ctrl == null || ctrl.urlInputField == null) return;
        if (VRCUrl.IsNullOrEmpty(ctrl.fixedInputPrefix) || string.IsNullOrEmpty(ctrl.fixedInputPrefix.Get()))
        {
            string directUrl = BuildUrl(ctrl.apiDomain, ctrl.directPathTemplate, ctrl.worldId, ctrl.poolName, 0);
            ctrl.fixedInputPrefix = new VRCUrl(directUrl);
        }
        VRCUrl cur = ctrl.urlInputField.GetUrl();
        if (VRCUrl.IsNullOrEmpty(cur) || string.IsNullOrEmpty(cur.Get()))
        {
            Undo.RecordObject(ctrl.urlInputField, "初始化音乐输入框默认直链前缀");
            ctrl.urlInputField.SetUrl(ctrl.fixedInputPrefix);
            EditorUtility.SetDirty(ctrl.urlInputField);
        }
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
            GameObject targetGo = null;
            if (ctrl.rawUdonPlayer != null) targetGo = ctrl.rawUdonPlayer.transform.root.gameObject;
            else if (ctrl.playerController != null) targetGo = ctrl.playerController.transform.root.gameObject;
            else if (ctrl.videoPlayers != null && ctrl.videoPlayers.Length > 0 && ctrl.videoPlayers[0] != null) targetGo = ctrl.videoPlayers[0].transform.root.gameObject;
            else if (ctrl.transform.parent != null) targetGo = ctrl.transform.parent.gameObject;

            if (targetGo != null)
            {
                SleepMusicConfigUtility.AutoFitToTargetScreen(ctrl, targetGo);
                SleepMusicConfigUtility.SafeCopyProxyToUdon(ctrl);
            }
            else EditorUtility.DisplayDialog("未指定目标播放器", "请先将目标播放器拖入上方绑定区，或将本舞台放到播放器物体下。", "确定");
        }
        if (GUILayout.Button("翻转 180°", GUILayout.Width(76), GUILayout.Height(26)))
        {
            SleepMusicConfigUtility.FlipStageFacing180(ctrl);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);

        // 实时屏幕自适应与曲面屏调节区（拖动滑条即时在 Scene 视图预览铺满与弯曲效果）
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("屏幕自适应与曲面屏调节（实时预览）", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        float newScale = EditorGUILayout.Slider("整体内容大小", ctrl.contentScale, 0.5f, 1.8f);
        float newMargin = EditorGUILayout.Slider("左右留白比例", ctrl.horizontalMarginRatio, 0.02f, 0.20f);
        float newCurve = EditorGUILayout.Slider("曲面屏弧度 (°)", ctrl.screenCurveDegrees, -90f, 90f);
        bool layoutChanged = EditorGUI.EndChangeCheck();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("平面 0°", GUILayout.Height(22))) { newCurve = 0f; layoutChanged = true; }
        if (GUILayout.Button("轻曲 25°", GUILayout.Height(22))) { newCurve = 25f; layoutChanged = true; }
        if (GUILayout.Button("环绕 45°", GUILayout.Height(22))) { newCurve = 45f; layoutChanged = true; }
        if (GUILayout.Button("大曲 65°", GUILayout.Height(22))) { newCurve = 65f; layoutChanged = true; }
        if (GUILayout.Button("刷新排版", GUILayout.Width(72), GUILayout.Height(22))) { layoutChanged = true; }
        EditorGUILayout.EndHorizontal();

        if (layoutChanged)
        {
            Undo.RecordObject(ctrl, "调整自适应排版与曲面弧度");
            ctrl.contentScale = newScale;
            ctrl.horizontalMarginRatio = newMargin;
            ctrl.screenCurveDegrees = newCurve;
            SleepMusicConfigUtility.EnsureCurvedAssetsAndLayout(ctrl);
            SleepMusicConfigUtility.SafeCopyProxyToUdon(ctrl);
            if (ctrl.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(ctrl.gameObject.scene);
        }
        EditorGUILayout.EndVertical();
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
