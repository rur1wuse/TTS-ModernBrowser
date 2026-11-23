# TTS Modern Browser

Tabletop Simulator 的平板只能跑内置的 CEF 74，很多现代网站直接白屏或脚本报错。这个 BepInEx 插件把平板的屏幕画面换成 **WebView2（新版 Chromium/Edge）** 渲染：平板还是原来那块平板，位置、碰撞、光照都不变，但页面由本机的 WebView2 打开，并且换页操作会随对象状态同步给所有客户端。

插件版本 `0.1.0`，BepInEx GUID `com.tts.modernbrowser`。

## 速查

```lua
MB_spawn("https://www.bilibili.com/")          -- 生成一块现代浏览器平板
MB_mark("a1b2c3", "https://example.com/")      -- 把已有平板转成现代浏览器
MB_setURL("a1b2c3", "https://www.google.com/") -- 换页（多人同步）
MB_unmark("a1b2c3")                            -- 恢复原版 CEF 平板
```

> 以上是写进脚本里的调用方式。若在游戏内控制台执行，要在语句最前面加 `lua` 前缀，例如 `lua MB_spawn("https://www.bilibili.com/")`——这里的 `lua` 是控制台命令，不属于函数名。

```
安装：退出游戏 -> 把 Release 里的 zip 解压到游戏根目录（源码安装见下文 install.ps1）
构建：dotnet build src\TtsModernBrowser\TtsModernBrowser.csproj -c Release
配置：<游戏>\BepInEx\config\com.tts.modernbrowser.cfg
日志：<游戏>\BepInEx\LogOutput.log（插件行以 [MB] 开头）
```

---

## 工作原理

### 1. URL 标记：不改对象，只改两条状态

Lua 端不调用任何插件接口，只写平板自身的状态：

| 通道 | 写入内容 | 作用 |
| --- | --- | --- |
| `Browser.url` | `about:blank#mb=<百分号编码的 URL>` | 旧 CEF 只会加载空白页；标记随平板状态同步 |
| `GMNotes` | `MB1:<原始 URL>` | 纯对象状态，CEF 把上面的 URL 规范化成 `about:blank` 后仍能兜底 |

客户端插件先解 `Browser.url` 里的标记，没有再看 `GMNotes`。没有标记的平板完全不处理，保持原版 CEF 行为——所以没装插件的客户端只是看到一块空白平板，不会报错。

### 2. 扫描与生命周期

`BrowserManager` 每 0.5 秒扫描场景里的 `TabletObject`：

- 发现新标记 → 为这块平板创建一个 WebView2 实例，同一平板只创建一次；
- 标记短暂消失 → 宽限 10 次扫描（约 5 秒），因为 CEF 提交页面后会把 URL 报回 `about:blank`；
- 标记持续消失或平板被删除 → 销毁实例并还原原始材质；
- 同时存在的实例数受 `General.MaxBrowsers` 限制（默认 4），超出的平板打一条日志后继续用原版 CEF。

### 3. 画面：替换屏幕材质，而不是叠一层几何体

插件找到显示 CEF 纹理的那块屏幕 Renderer，把它的 per-object 材质 `mainTexture` 换成 WebView 纹理。平板的网格、缩放、深度、光照全部沿用游戏自己的，因此不会出现错位或 z-fighting；取消标记时换回原来的共享材质和纹理。

抓帧有两条路径：

- **Graphics Capture**（默认）：WebViewToolkit 原生插件把 WebView2 内容写进 Unity 纹理，约 30 fps 更新；
- **Window Capture**（兜底）：用 `PrintWindow(PW_RENDERFULLCONTENT)` 把同进程的 WebView2 窗口拷进纹理，固定约 15 fps。

`CaptureMode=Auto` 会在导航完成 2 秒后采样一次画面，检测到全黑就自动切到窗口抓帧；显式指定 `GraphicsCapture` 则不回退。

### 4. 输入：屏幕前挂一个仅供本插件使用的碰撞体

每块被接管的平板会在屏幕位置生成一个 `BoxCollider`，放在 Unity 的 `Ignore Raycast` 层，TTS 自己的射线不会命中它，插件用自己的层掩码拾取。指针坐标优先用屏幕网格自身的 UV 拟合结果（`ScreenUv`），所以即使平板换了贴图布局，点击位置仍与画面一致；拟合失败时退回平面坐标换算。

页面侧还注入了一段 JS：

- 接管 `window.open` / `target=_blank` / 表单 `target`，所有导航留在平板内，不弹独立窗口；
- 模拟 `requestFullscreen` 系列 API：把播放器铺满 WebView 视口（也就是平板屏幕），并隐藏页面导航栏、直播礼物栏等兄弟节点；
- 提供文本插入与视口尺寸上报（Windows 缩放不是 100% 时，CSS 视口会比渲染尺寸小，页面内点击必须用 CSS 像素）。

`InputGuard` 用 Harmony 给 `Pointer.Grab`、`Pointer.StartInteract`、`UserDefinedHotkeyManager.DoHotkey` 加前缀：指针在页面上时禁止抓起平板，页面持有键盘焦点时屏蔽 TTS 热键。它故意不碰 `Pointer.Update`，否则游戏指针会冻结或消失。

---

## 环境要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11 |
| 游戏 | Tabletop Simulator（64 位、Mono 后端、D3D11 或 D3D12） |
| 加载器 | BepInEx 5.4.23.6（win_x64） |
| 运行时 | Microsoft Edge WebView2 Runtime（Win10/11 通常已自带） |
| 构建 | .NET SDK（目标框架 net472） |

多人游戏要求**每个客户端都装同一个插件**。只装一部分客户端时，没装的客户端看到的是原版 CEF 平板，也就是一块空白页。

---

## 安装

### 从 Release 安装（推荐）

1. 退出游戏。
2. 从本仓库的 Releases 下载 `TtsModernBrowser-<版本>-win64.zip`。
3. 把压缩包解压到游戏根目录——即包含 `Tabletop Simulator.exe` 的那一层；提示同名文件时选择覆盖。
4. 启动游戏，`<游戏>\BepInEx\LogOutput.log` 中出现 `[MB] TTS Modern Browser ready` 即安装成功。

压缩包顶层已经是 `BepInEx\plugins\TtsModernBrowser\`，解压后不需要再移动文件，也不需要运行 `install.ps1`。包里**只有插件本体，不含 BepInEx 和 WebView2 Runtime**，这两项要先按“环境要求”装好。

配置（`BepInEx\config\com.tts.modernbrowser.cfg`）和 `WebView2Data` 都不在压缩包内，覆盖安装或升级不会清掉已有配置。升级时同样先退出游戏，再用新 zip 覆盖解压。

### 从源码构建并安装

```powershell
# 1) 把 BepInEx 5.4.23.6 (win_x64) 解压到游戏根目录，启动一次游戏让它生成配置
# 2) 退出游戏
dotnet build src\TtsModernBrowser\TtsModernBrowser.csproj -c Release
powershell -ExecutionPolicy Bypass -File install.ps1
```

游戏不在默认路径时二选一：

```powershell
dotnet build src\TtsModernBrowser\TtsModernBrowser.csproj -c Release -p:TtsDir="D:\Steam\steamapps\common\Tabletop Simulator"
powershell -ExecutionPolicy Bypass -File install.ps1 -GameDir "D:\Steam\steamapps\common\Tabletop Simulator"
```

`install.ps1` 会：

1. 检查 `Tabletop Simulator.exe`、`BepInEx\core\BepInEx.dll` 和构建产物；
2. 游戏正在运行时拒绝安装（`-Force` 可跳过检查，但不建议）；
3. 把 `dist\BepInEx\plugins\TtsModernBrowser\*` 复制到插件目录；
4. 创建 `WebView2Data` 配置目录，复制本 README；
5. 打印插件、配置、日志和配置目录的路径。

### 手工安装

把 `dist\BepInEx\plugins\TtsModernBrowser\` 整个目录复制到 `<游戏>\BepInEx\plugins\`，再手动创建 `WebView2Data` 子目录即可。

### 升级

**先退出游戏**（DLL 被占用时无法覆盖），重新构建后再次运行 `install.ps1`。

### 卸载

删除 `<游戏>\BepInEx\plugins\TtsModernBrowser\` 整个目录，重启游戏。已被标记的平板会回退成原版 CEF，此时屏幕显示 `about:blank`；执行 `MB_unmark(guid)` 或手动清掉 `Browser.url` 与 `GMNotes` 就能恢复正常网址。

### 备用加载方式（UnityDoorstop）

BepInEx 预加载器无法运行时，可以用 UnityDoorstop 直接启动插件：把 `doorstop_config.ini` 的 `target_assembly` 指向 `TtsModernBrowser.dll`，`entry_point` 设为 `TtsModernBrowser.Loader::Main`，并移除 BepInEx 的 `winhttp.dll`。这条路径与 BepInEx 插件共用 `Bootstrap`，配置文件同样落在 `<游戏>\BepInEx\config\com.tts.modernbrowser.cfg`。

---

## 快速开始

1. 打开存档，进入 `游戏 -> 脚本 -> 全局`，把 `lua\Global.lua` 全文粘贴进去。
2. 生成一块平板。在 Global Script 里直接写函数调用，不需要任何前缀：

```lua
MB_spawn("https://www.bilibili.com/")
```

如果想在游戏内控制台执行（默认按 `~` 打开），要先输入 `lua` 告诉控制台「后面这段是 Lua 代码」，再紧跟同一条语句：

```text
lua MB_spawn("https://www.bilibili.com/")
```

`lua` 是控制台的执行命令，不是函数名的一部分；在控制台里输入的 `lua MB_spawn(...)`，和在脚本里写的 `MB_spawn(...)` 是同一件事。其余 `MB_*` 指令同理。

或者接管场上已有的平板（先选中它，用它的 GUID）：

```lua
MB_mark("a1b2c3", "https://www.google.com/")
```

3. 约半秒内平板屏幕就会显示网页，鼠标移到画面上即可交互。

想要在平板上直接输网址，把 `lua\Tablet.lua` 粘贴到该平板的 **Object Script**（右键平板 -> 脚本 -> 对象）。画面上方会出现输入框和 Go 按钮。

---

## Lua 指令

所有函数都定义在 Global Script 里，Object Script 也可以直接调用。在游戏控制台测试时，整条语句前加 `lua` 前缀（见上一节的两种执行入口）。

| 指令 | 返回值 | 说明 |
| --- | --- | --- |
| `MB_spawn(url, position)` | 无 | 在 `position`（默认 `{0, 3, 0}`）生成一块已标记的平板 |
| `MB_mark(guid, url)` | `bool` | 把平板改名、写标记并上锁，加载 `url`（默认 `https://example.com/`） |
| `MB_setURL(guid, url)` | `bool` | 让已标记的平板跳转，同时写两条同步通道 |
| `MB_unmark(guid)` | `bool` | 清掉标记，平板回到原版 CEF |
| `mb_encode(s)` | `string` | 内部使用的百分号编码 |

示例：

```lua
-- 直接打开一个视频
MB_spawn("https://www.bilibili.com/video/BV1xx411c7mD")

-- 场上某块平板换页（主机调用，所有客户端跟随）
MB_setURL("a1b2c3", "https://example.com/")

-- 指定位置生成
MB_spawn("https://www.google.com/", {-2, 2, 2})

-- 恢复原版平板
MB_unmark("a1b2c3")
```

读写标记时有两个坑：

- **`MB_mark` 必须先写标记再 `setLock`。** 锁定对象的状态写入可能被丢弃，顺序反了平板会永远停在空白页。
- **被标记的平板占用了 `GMNotes`。** 不要在同一块平板上保存其它备注，否则标记会被覆盖，插件会把平板当作未标记处理。

---

## 操作方式

| 操作 | 行为 |
| --- | --- |
| 鼠标移动 | 指针进入画面后转发给网页 |
| 左键 / 右键 / 中键 | 转发点击（Chromium 的右键菜单不显示） |
| 滚轮 | 页面滚动 |
| 左键点页面 | 页面取得键盘焦点，之后的键盘输入给网页 |
| 键盘 | 字符、回车、Tab、退格、Delete、方向键、Home/End、PgUp/PgDn、F5、Esc 转发给网页 |
| `Ctrl+A/C/X/Z/Y` | 全选 / 复制 / 剪切 / 撤销 / 重做 |
| `Ctrl+V` | 用系统剪贴板内容粘贴 |
| 双击页面，或 `F11`，或页面自己的全屏按钮 | 进入页面全屏（铺满平板、隐藏页面导航和直播浮层）；再次双击 / `F11` / `Esc` 退出 |

指针在页面上时，TTS 的抓取和交互动作被屏蔽，不会误抓起平板；页面持有键盘焦点且 TTS 没有输入框激活时，TTS 热键被屏蔽。

某些机器上 WebView2 会拒绝合成的原生鼠标事件，插件会自动退化为页面内 `elementFromPoint` + DOM 事件派发，点击仍然可用（日志记录一次 `rejected`）；滚轮没有页面内兜底，在这类机器上可能失效。

---

## 配置

路径：`<游戏>\BepInEx\config\com.tts.modernbrowser.cfg`，修改后重启游戏生效。

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `General.Enabled` | `true` | 总开关 |
| `General.MaxBrowsers` | `4` | 同时存在的 WebView2 上限 |
| `General.UserDataFolder` | 空 | WebView2 用户数据目录；空则用插件目录下的 `WebView2Data` |
| `Rendering.DefaultWidth` | `1280` | 渲染宽度，最小 320；高度按平板宽高比计算并限制在 480–1600 |
| `Rendering.CaptureMode` | `Auto` | `Auto`（图形抓帧，黑帧自动切窗口抓帧）/ `GraphicsCapture` / `WindowCapture` |
| `Rendering.FlipTexture` | `false` | 画面上下颠倒时置 `true`，指针映射会同步翻转 |
| `Input.SuppressHotkeys` | `true` | 页面持有键盘焦点时屏蔽 TTS 热键 |

调优：

- 想要最流畅：保持 `CaptureMode=GraphicsCapture`（或 `Auto` 且不触发回退）；
- 机器吃力或平板较多：降低 `DefaultWidth`（如 960）或 `MaxBrowsers`；
- 独占全屏下图形抓帧可能全黑：改用窗口或无边框全屏，或强制 `WindowCapture`。

---

## 日志与排错

日志在 `<游戏>\BepInEx\LogOutput.log`。插件行以 `[MB]` 开头，WebView 包装层以 `[WebViewManager]` 开头，原生层以 `[WebView Native]` 开头。`[WebView*]` 的 Unity 日志也会被镜像进同一个文件。

### 正常启动

```
[MB] Bootstrapping from ...\BepInEx\plugins\TtsModernBrowser
[MB] WebView2Loader mapped from ...\BepInEx\plugins\TtsModernBrowser
[MB] WebViewToolkit resolved through Mono (initialized=0)
[MB] WebView2 browser arguments: --disable-features=CalculateNativeWinOcclusion ...
[MB] WebView2 user data folder: ...\WebView2Data
[MB] Patched Pointer.Grab
[MB] Patched Pointer.StartInteract
[MB] Patched UserDefinedHotkeyManager.DoHotkey
[MB] TTS Modern Browser ready
```

### 创建平板

```
[MB] Marker on tablet -20326: https://www.bilibili.com/
[WebViewManager] Initializing with Direct3D11...
[WebView Native] WebViewToolkit: Initialized successfully
[MB] Screen UV map: fitted from mesh
[MB] Screen material shows 'WebViewTexture_1' (was '')
[MB] Browser created: 1280x832 for a 1.200x0.780 tablet screen (capture=graphics capture, url=...)
[MB] Navigation completed: https://www.bilibili.com/ success=True
[MB] Page engine: Mozilla/5.0 ... Chrome/157.0.0.0 ... Edg/157.0.0.0
[MB] Frame check (graphics capture): average=... brightest=... (frames present)
```

`Page engine` 里的 `Edg/xxx` 就是 WebView2 的版本，可用来确认页面没有退回旧 CEF。

### 常见故障

| 现象 | 日志判据 | 处理 |
| --- | --- | --- |
| 平板全黑，无任何日志 | 没有 `Marker on tablet` | 平板没被标记：检查 `MB_mark` / `MB_spawn`，或 `GMNotes` 是否被别的脚本覆盖 |
| 平板全黑 | `MaxBrowsers (4) reached` | 关闭其它已标记平板，或调大 `MaxBrowsers` |
| 平板全黑 | `... produced a black frame`，且没有切到窗口抓帧 | 独占全屏或驱动问题：改窗口/无边框全屏，或设 `CaptureMode=WindowCapture` |
| 平板全黑 | `WebView2 backend failed to initialize` | 游戏图形 API 不是 D3D11/D3D12，或 WebView2 Runtime 缺失 |
| 启动时报缺库 | `Missing native library` / `LoadLibrary failed` | 安装不完整，重新运行 `install.ps1` |
| 刚显示几秒后变黑 | `Marker missing on tablet ...` → `Marker cleared ... removing overlay` | 标记丢失：确认用的是本仓库最新 `lua\Global.lua`（标记写在 `setLock` 之前） |
| 有画面但点击没反应 | `Left down at (0.000, 0.000); target=none` | 该帧没拾取到平板；确认没有其它插件抢射线 |
| 点击位置偏移 | 对比 `Left down at (u,v)` 与实际元素 | 把这两行和 `Screen UV map` 那行发出来；网格 UV 很特殊时会退化到平面换算 |
| 点击后弹独立窗口 | 日志出现新窗口 | 旧版本；更新插件（`window.open` / `target=_blank` 已接管） |
| 全屏没反应 | 没有 `[MB] fullscreen: enter:` | 尝试 `F11` 或双击画面；仍无日志说明脚本注入失败 |
| 网页白屏 | `Navigation completed ... success=False` | 网址、网络或代理证书问题；换一个网址验证 |
| 键盘无反应 | 日志没有 `keyboardFocus=True` | 先在页面上点一次左键取得焦点 |
| 输入法不出候选 | —— | 已知限制，见下节 |

### 输入日志

```
[MB] input: overPage=True uiHover=False uiTyping=False keyboardFocus=False
[MB] pointer entered page at (0.501, 0.621)
[MB] Left down at (0.501, 0.621); target=page; native=skipped; mouse=(770.0, 517.0) uiHover=True
[MB] dispatched an in-page click at (641, 516) of 1280x832 (script=True)
[MB] in-page click target: 641,516:A.title
[MB] fullscreen: enter:DIV.bpx-player-container;hidden=12
```

- `target=page` 表示这一下被判定为点在页面上；`native=ok` 走原生输入，`skipped` / `rejected` 表示改走页面内点击。
- `in-page click target` 是实际命中的 DOM 元素，配合 `Left down at (u,v)` 可以判断坐标是否偏移。

---

## 已知限制

- 只支持桌面鼠标键盘，**VR 手柄射线未实现**。
- **中日韩输入法的组合输入不支持**；可以用剪贴板粘贴。
- 窗口抓帧兜底固定约 15 fps，滚动和视频会比图形抓帧更顿。
- 独占全屏下图形抓帧可能一直黑屏。
- 每个平板占一个 WebView2 实例和一份显存/内存，默认上限 4。
- Chromium 的右键菜单不显示（输入事件已转发，菜单本身由 WebView2 的独立窗口渲染，不在抓帧范围内）。
- TTS 更新若重命名 `Pointer.Grab`、`Pointer.StartInteract` 或 `UserDefinedHotkeyManager.DoHotkey`，Harmony 补丁会失败；浏览器仍可用，只是不再屏蔽抓取/热键。
- 被标记的平板占用 `GMNotes` 作为同步通道。
- 插件会把静态属性 `TabletObject.AutoconfirmURLChange` 置为 `true`（进程级），否则每次换网址都会弹「Tablet wants to open URL」确认框。

---

## 构建与项目结构

```powershell
dotnet build src\TtsModernBrowser\TtsModernBrowser.csproj -c Release
# 产物：dist\BepInEx\plugins\TtsModernBrowser\
#   TtsModernBrowser.dll / .pdb
#   WebViewToolkit.dll / WebView2Loader.dll（从 src\TtsModernBrowser\native 复制）
```

构建需要 .NET SDK 以及游戏目录下的 `Assembly-CSharp.dll`、`ZFBrowser.dll`、Unity 模块和 BepInEx 的 `BepInEx.dll`、`0Harmony.dll`。路径由 `TtsDir` 属性控制，默认 `E:\SteamLibrary\steamapps\common\Tabletop Simulator`，可用 `-p:TtsDir=...` 覆盖。

```
src\TtsModernBrowser\
  Plugin.cs            BepInEx 入口（绑定配置、调用 Bootstrap）
  Loader.cs            UnityDoorstop 入口（备用）
  Bootstrap.cs         两条入口共用的初始化：原生库、配置目录、WebView2 参数、Manager/Input 组件
  BrowserManager.cs    0.5s 扫描平板、标记解析、实例上限与销毁
  TabletBrowser.cs     单块平板：WebView 创建、材质替换、页面 JS 注入、抓帧探测与回退
  BrowserInput.cs      鼠标/滚轮/键盘转发、页面内点击兜底、TTS UI 状态检测
  InputGuard.cs        Harmony 前缀：屏蔽抓取与热键
  WindowCapture.cs     PrintWindow 窗口抓帧
  ScreenUv.cs          从网格拟合屏幕局部坐标到 UV 的仿射映射
  NativeLoader.cs      WebViewToolkit / WebView2Loader 的加载顺序
  Settings.cs          BepInEx 配置项
  JsLiteral.cs         JS 字符串转义
  Log.cs               [MB] 日志与 WebView Unity 日志镜像
  native\              WebViewToolkit.dll、WebView2Loader.dll
  WebViewToolkit\      C# 侧包装（Manager / Instance / Native 绑定）
lua\Global.lua         Global Script：标记与同步 API
lua\Tablet.lua         平板 Object Script：地址栏
install.ps1            安装脚本
dist\                  构建产物（已在 .gitignore 中忽略）
```

仓库中的 `.gitignore` 排除了 `bin/`、`obj/`、`dist/` 和 `WebView2Data/`；需要的二进制只有 `src\TtsModernBrowser\native\` 下那两个原生 DLL。
