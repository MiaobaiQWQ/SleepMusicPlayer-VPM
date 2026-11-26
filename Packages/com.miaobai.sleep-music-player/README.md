# SleepMusic Player · 眠境音乐歌词播放器 (VPM)

基于 **VizVid (`idv.jlchntoz.vvmw`)** 的 VRChat 开箱即用毛玻璃音乐与视频播放器 VPM 扩展包。

## 核心特性
- **2 行毛玻璃智能胶囊 UI**：音量点击展开抽屉、齿轮设置防抖自动收起、播放时自动隐藏输入框文字避免与时间戳重叠。
- **GPU BC1 封面解码 & 无重影流体光晕背景**：单 Pass GPU 解码 `wimbc1png1` 数据图集，开启硬件自动 MipMap + 36 点黄金角圆盘流体模糊，彻底消除带文字封面的叠字重影，同时左侧封面卡片保持 Mip 0 100% 锐利。
- **GPU 逐字暖金扫光歌词**：按每行实际字宽动态计算扫光进度，支持双语翻译与音频缓冲时间补偿。
- **VRC 中文性能与错误码日志**：实时输出网络拉取耗时、GPU 封面解码耗时、切行渲染耗时、逐字上色耗时以及 `402 / 404 / 502` 等 HTTP 状态码中文说明。
- **一键替换域名与音乐池设置工具**：支持在 Inspector 或菜单 `Tools -> 眠境音乐播放器 -> 接口与音乐池一键配置` 一键更换域名、世界 ID、路径模板与池槽位数量（`1 ~ 500`）。

## 快速使用（VCC / ALCOM）
1. 在 VCC 或 ALCOM 中添加 VizVid 源与本仓库的 VPM Listing 地址：
   - 本仓库 VPM 地址：`https://xiaoxnmv.github.io/SleepMusicPlayer-VPM/index.json`
   - 依赖：`com.vrchat.worlds >= 3.7.0`、`idv.jlchntoz.vvmw >= 1.8.0`
2. 在项目里安装 **SleepMusic Player (眠境音乐歌词播放器)**。
3. 在 Unity 场景中右键 **`GameObject -> 眠境音乐播放器 -> 创建开箱即用播放器`**（或直接拖入 `Packages/com.miaobai.sleep-music-player/Prefabs/SleepMusicPlayer (All-in-One).prefab`）。
4. 打开菜单 **`Tools -> 眠境音乐播放器 -> 接口与音乐池一键配置`**，填入你的接口域名、世界 ID 与槽位数量，点击 **「一键替换并同步到场景」** 即可上传测试。

## 自动发版与 VPM 镜像同步（GitHub Actions）
- 修改 `Packages/com.miaobai.sleep-music-player/package.json` 中的 `version`（例如改为 `1.0.1`），提交后推送对应 Tag：
  ```bash
  git tag v1.0.1
  git push origin main --tags
  ```
- GitHub Actions（`.github/workflows/release-vpm.yml`）会自动：
  1. 打包带完整 `.meta` 文件的 `com.miaobai.sleep-music-player-<version>.zip`；
  2. 计算 `zipSHA256` 并发布 GitHub Release；
  3. 自动更新 `docs/index.json`（VPM Listing）与 `docs/index.html`（一键添加 VCC 页面）并同步到 GitHub Pages。
- 也可本地运行 `python scripts/build_vpm_release.py` 立即生成 `dist/*.zip` 与 `docs/index.json`。
