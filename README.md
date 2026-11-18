# Local Radiance Transfer 工程文档

本工程在 Unity `2022.3.62f3` 和 URP `14.0.12` 中演示动态间接漫反射和方向性天光可见性。探针覆盖一片世界空间区域，Trunk 模式支持自动覆盖场景和手工指定的稀疏／稠密区域。Compute Shader 按需更新探针。Screen Space Gather 将探针光照写入低分辨率全屏 RT，材质按屏幕坐标读取间接光。

实现参考橙子的 Local Radiance Transfer 文章。本文中的参数、调度和存储量来自当前工程。原文标题中的 `0.5 ms` 是作者在骁龙 835 上的数据。本工程的移动端耗时待实测。 [参考文章](https://zhuanlan.zhihu.com/p/653044045)

## Demo_D_LRT 与场景烘焙

打开 [Demo_D_LRT.unity](Scenes/Demo_D_LRT.unity) 并进入 **Play**。Game HUD 可隐藏或恢复一栋建筑、开关飞船灯光、切换昼夜并查看 FPS。对比光照时，等颜色反弹显示 **Propagation idle**，天空显示 **Sky visibility ready**。选中场景中的 **Local Radiance Transfer** 并开启 Scene 窗口的 **Gizmos**，可查看 Trunk 边界，混合密度下青色表示稠密块，白色表示稀疏块。

| Trunk 演示视图 | LRT Lit 显示内容 |
| --- | --- |
| Combined | URP 直接光与材质光照，间接漫反射使用 LRT 反弹和经过遮蔽的环境光。 |
| Direct | 将 LRT 体积内的间接漫反射输入置零。 |
| Bounce only | 直接显示 Gather RT 的反弹 RGB。 |
| Sky visibility | 显示环境光颜色乘以调整后的天空可见性。 |

**Switch to Night / Switch to Day** 在当前场景中用 2 秒过渡主光、Skybox 和环境光强度。点灯在黄昏按列表顺序开启，在黎明逆序关闭。 [昼夜切换](LocalRadianceTransfer/Demo/DemoDayNightCycle.cs)

### 稀疏与稠密探针区域

在 **Lrt Trunk Scene** 中，将整体范围的 BoxCollider 赋给 **Scene Bounds**，将多个玩法区 BoxCollider 加到 **Gameplay Regions**。BoxCollider 可以禁用物理碰撞，GI 仍读取其尺寸和变换。系统将整体盒子的世界轴对齐包围盒扩到整块；和玩法区包围盒相交的块使用 **Probes Per Trunk Axis**，其余使用 **Coarse Probes Per Trunk Axis**。玩法区可以重叠，也可以位于不同高度。旋转盒子按其世界轴对齐包围盒选块。未指定 **Scene Bounds** 时沿用自动包围盒布局。

**Demo_D_LRT** 保存了两个玩法盒子，Trunk 边长为 **10 m**，稠密块每轴 **16** 个探针，稀疏块每轴 **4** 个探针，对应格距 **0.625 m** 和 **2.5 m**。**Show Trunks** 下黄色框是场景范围，绿色框是玩法区。面板显示实际粗细探针数及网格超限原因。几何遮挡仍使用整个场景盒子内的细体素缓存，所以稀疏区的峭壁仍可遮挡灯光和天空。粗细探针在共享面交换光照，Gather Pass 利用图集的一格边缘跨块插值。低密度区的反弹仍受粗探针间距限制，边缘插值是近似。修改盒子无需重烘 Color SDF。

### 探针可视化

在 **Local Radiance Transfer Demo** 组件或 Game 窗口开启 **Show Probes**，可在 Scene 和 Game 窗口看到位于探针中心的标记。颜色取自当前反弹光的平均 RGB，亮点表示反弹较强的位置，暗点表示反弹较弱的位置。标记参与深度测试，墙体会挡住其后的探针。**Probe Marker Radius** 以探针格距为单位控制标记大小，**Probe Draw Stride** 可隔点显示密集网格。

### 美术工作流

1. 在已保存的场景里编辑 Prefab、网格和材质。需要接收实时 GI 的表面使用 **Local Radiance Transfer/Lit**，并设置 Base Map 和 Base Color。
2. 在 **Lrt Trunk Scene** 组件上点击 **Bake Color SDF**。第一次可用 **Tools → Local Radiance Transfer → Bake Active Scene Color SDF** 创建控制器。此操作独立于 Unity Lighting 窗口的 Lightmap 烘焙。
3. 保存场景。生成的资产保存在 [BakedColorSdf](LocalRadianceTransfer/BakedColorSdf) 下按场景划分的目录中。相同 Prefab 的相同网格、子物体布局与材质组合共享一个资产，实例的根节点位置、旋转和缩放在运行时应用。材质或子物体布局覆盖会生成对应的变体。独立的 MeshRenderer 也可烘焙。
4. **Bake Resolution** 控制局部 SDF 最长轴的采样数。**Trunk Size Meters** 和两档探针数决定场景的探针格距。

烘焙读取活动 MeshRenderer 的最高 LOD 和各 SubMesh 三角形。每格保存量化距离及最近表面的 RGB。颜色按三角形 UV 采样 Base Map，再乘 Base Color。透明材质与蒙皮网格跳过，Alpha Clip 表面按网格轮廓参与占据。网格开口、小孔和薄片受烘焙分辨率及探针格距影响。改变材质、网格或 Prefab 子层级布局后需重烘。运行时新建的对象可用 `LrtTrunkScene.Register(root, data)` 登记，回收时调用 `Unregister(root)`。

### 独立天空遮蔽

**Sky Visibility Quality** 提供低、中、高三档，分别从每个空气探针向上半球采样 **5、9、17** 个方向，默认 **9**。遮挡射线沿途查询 Color SDF 合成的体素，并跨过空 Trunk。结果保存平均可见性与方向分量，Gather Pass 按 Depth Normal 预通道输出的表面法线求值。地面、墙边和屋檐下的遮蔽由可见方向决定。

**Sky Probes Per Frame** 控制每帧最多处理多少探针，组件默认 **16,384**，Demo_D_LRT 保存为 **4,096**。几何或质量档位变化会更新天空缓存，灯光变化只更新颜色反弹。每批天空结果计算完成后立即写入纹理。首轮尚未处理的空气探针使用全可见天空，后续轮次中尚未处理的探针沿用旧值。几何在批次中途变化时会排入下一次更新。

每次完整更新的射线数上界是探针数乘方向数，实体探针会跳过射线。天空系数复用探针纹理及传播缓冲的 Alpha 通道。颜色反弹写入时保留天空系数，天空写入时保留反弹 RGB。Gather Pass 从同一组体纹理求出反弹和天光可见性。**Probe Sample Radius** 同时控制反弹与天空的取样位置。**Sky Occlusion Strength** 从全可见（0）插值到原始可见性（1），同时用于 Trunk 的环境光合成和 **Sky visibility** 视图。

### Trunk 数据流

- **Trunk Size Meters** 为立方块的世界空间边长，默认 **10 m**。**Probes Per Trunk Axis** 为每块每轴的探针数，范围 **4–32**，默认 **8**。默认配置每块有 **512** 个探针，格距为 **1.25 m**。
- 自动覆盖模式合并已烘焙实例和运行时登记对象的世界包围盒，预留一格探针的边界后对齐到整块。物体跨出当前网格或最外侧物体移除时，系统根据新包围盒调整网格。暂时停用的对象仍计入场景范围。Inspector 的 **Active Scene Trunks** 显示各轴实际块数。每个 Trunk 维护相交的 GI Primitive 列表。跨块对象登记到所有覆盖块。移动、旋转、缩放、启停或移除时，旧包围盒与新包围盒所涉及的块会重新采样和上传。相邻块可通过网格坐标直接寻址。
- CPU 将探针位置变换到对象局部空间，查询 SDF 和反射颜色，合成 Trunk 的占据缓存。只有登记了几何的 Trunk 使用细体素占据缓存。混合密度模式按实际探针数分配注入缓存，GPU 遮挡射线跨过空块时直接跳到块边界。所有探针仍参与六邻接球谐传播，跨 Trunk 的传播直接读取相邻探针。
- 演示包含 **1,082** 个烘焙实例和 **86** 份 Color SDF。混合密度模式的球谐传播缓冲按实际探针数分配，每探针 **128 B**。粗细探针各用一张 RGBA16F 3D 图集，每块保留一格插值边缘。Gather Pass 从 RGBA8 2D 布局纹理点采样块的密度和图集槽位，再读取对应图集。Compute Shader 使用独立的布局缓冲和细体素占据缓存。原帖的 26 邻接 Local Transfer Matrix 与场景流式加载不在此实现范围内。

## 光源

- **仅直接光照**。使用 Unity 的 **Light**，实时直射由 URP 计算。
- **仅 LRT GI**。添加 **Light → LRT Light** 并设置 **Shape**、**Color**、**Intensity**、**Range** 和 **Indirect Multiplier**。点光向各方向发光。聚光沿本地 `+Z` 发光，在内外锥角之间衰减。矩形面光沿本地 `+Z` 单面发光，按 **Size** 取四个样本。当前传播路径支持一盏有效的方向光。
- **直接光照和 LRT GI**。在同一 GameObject 上添加两个组件。启用 **Use Unity Light** 后，LRT 同步 Unity Light 的形状、颜色、强度、范围、锥角和开关状态。矩形尺寸在编辑器中实时同步，并在构建时写入场景。有效反弹倍率为 Unity 的 **Bounce Intensity** 乘以 LRT 的 **Indirect Multiplier**。关闭 **Use Unity Light** 可独立配置 LRT 参数。

LRT 收集同一场景中的 LRT Light 组件。Demo_D_LRT 已为参与 GI 的点光和方向光添加组件。其他场景可为需要继续反弹的 Unity 灯补挂 **LRT Light**。 [灯光组件](LocalRadianceTransfer/Runtime/LrtLight.cs)

**在 Scene 视图中定位灯光**。开启 **Gizmos** 后，挂有 **LRT Light** 的物体即使未选中，也会显示金色灯泡。点击灯泡即可选中对应的 GameObject。Inspector 的组件标题也使用同一个图标。如果场景里看不到灯泡，可在 **Gizmos** 下拉菜单中检查对应组件的图标开关。 [灯泡图标](LocalRadianceTransfer/Editor/LrtLightIcon.png)

运行时灯光列表约每 `0.5 s` 重查一次，已发现灯光的参数随控制器更新检查。Trunk 模式在灯光或几何变化时重算注入缓存。关灯或将 **Indirect Multiplier** 设为 `0` 会停止对应注入，旧反弹在后续轮次里衰减。天空环境光继续读取 Unity Lighting 的 **Environment Lighting**，天空几何可见性独立更新。 [灯光收集与缓冲区](LocalRadianceTransfer/Runtime/LocalRadianceTransferDemo.cs)

## 更新与休眠

空气中的 RGB 传播按相邻探针中心的世界距离衰减，倍率为 `pow(Air Transmission Per Meter, distanceMeters)`。在 **Local Radiance Transfer Demo → Air Transmission Per Meter** 调整每米光照保留率，范围 `0–1`：越大衰减越弱，`1` 禁用额外空气衰减，`0` 阻断空气格的 RGB 传播。默认 `0.8751057`，与原先每 `0.625 m` 保留 `92%` 的强度一致，`2.5 m` 保留约 `71.64%`。修改后自动重新获得一批传播迭代，不重建几何或重新计算灯光注入。粗细块交界使用实际探针中心距离（包含横向错位），各方向先衰减再混合；材质反射和天空可见性不使用这项空气衰减。这只统一额外传播损耗，粗细网格的几何、方向混合及反射近似仍可能产生亮度差异。

- 网格初始化或尺寸变化时分配缓冲区并预跑最多 `16` 步。之后按更新节奏继续执行，累计至迭代上限。
- 灯光或几何数据变化时重新获得一批迭代步数。系统从现有传播缓冲继续更新。体积重建时才以清空后的缓冲开始。
- 达到上限后停止反弹传播调度，Gather Pass 复用探针纹理。CPU 继续检查场景输入。移动镜头、改采样半径或只改环境光颜色和强度，不会重新启动探针传播。
- 编辑模式在待传播时按每次最多 `8` 步更新，Play Mode 使用帧间隔和每次步数。最后一批按剩余步数截断。 [调度代码](LocalRadianceTransfer/Runtime/LocalRadianceTransferDemo.cs)

## Screen Space Gather

绘制不透明物体之前，LRT Depth Normal 预通道调用场景 LRT Lit Shader 的 `LrtDepthNormals` Pass，将世界空间法线用八面体映射编码到 RGB111110 RT 的 RG 通道，B 通道写零并预留给后续扩展。预通道和 Gather 使用相同的 Resolution Scale。预通道的深度附件用于可见性测试，结束后以 Store 保留内容，直接绑定给 Gather 采样。Gather 从 RG 解码法线，并读取深度附件的原始硬件深度重建世界位置。每个 Gather 像素将位置换算为探针坐标，沿法线偏移约一格，再按与表面法线相反的方向求值。结果写入独立的 RGBA16F RT，RGB 保存反弹光，A 保存天光可见性。 [Gather Shader](LocalRadianceTransfer/Shaders/LrtScreenSpaceGather.shader) · [探针采样](LocalRadianceTransfer/Shaders/LrtProbeSampling.hlsl)

**Probe Sample Radius** 大于 `0` 且蓝噪声纹理可用时，Gather 对朝外半球的四个分层样本求平均。偏移半径以基础网格格距计，混合密度模式使用细体素格距。抖动以 Gather RT 的像素坐标索引，一个 Gather 像素对应一个蓝噪声纹素，镜头拉近时噪声图案保持相同的屏幕尺度。 [BlueNoise 贴图](LocalRadianceTransfer/Resources/BlueNoise.png)

在 Unity 中选中 **URP-HighFidelity-Renderer**，展开 **Renderer Features → LRT Screen Space Gather**，调整 **Resolution Scale**。默认 `0.5` 将宽高各减半，例如 `1920 × 1080` 对应 `960 × 540`。可调范围为 `0.25–1`。 [Renderer 配置](Settings/URP-HighFidelity-Renderer.asset)

**LRT Lit** 的不透明 Base Pass 按屏幕坐标双线性读取 Gather RT。普通双线性会混合深度断层两侧的 GI，亮背景可能在前景物体的轮廓处形成亮边。深度感知上采样是待评估方案。 [材质采样](LocalRadianceTransfer/Shaders/LrtScreenSpaceGI.hlsl)

最终间接漫反射输入为 **探针反弹 RGB + 环境漫反射光 × 探针天光可见性**。天光可见性只乘到环境光。材质 Occlusion Map 和 URP SSAO 继续走 Lit 的合成规则。超出探针体积时，LRT Lit 使用 URP 原有的间接光输入。 [合成代码](LocalRadianceTransfer/Shaders/LrtLitForward.hlsl)

## 性能与当前边界

| 项目 | 当前成本或限制 |
| --- | --- |
| Trunk 存储 | 两份球谐传播缓冲合计每探针 `128 B`。混合密度图集按砖块边缘和实际图集尺寸分配，另计细体素及注入缓存。 |
| 球谐节省 | 与原六轴布局相比，输出体纹理和两份传播缓冲各节省约 `33%`。 |
| Gather 与材质采样 | Gather 对每个有效像素读取一次 RGB111110 法线 RT 和一次预通道深度附件。Probe 单点模式读取 4 个 3D 球谐系数，四点半球模式最多读取 16 次 3D 纹理和两次蓝噪声。混合密度模式每个 Probe 采样位置另读一次 2D 布局纹理。Base Pass 对有 LRT GI 的像素读取一次 2D Gather RT。 |
| Trunk 注入与传播 | 注入重算开销随暴露体素面、灯光数量和遮挡遍历成本增加。每盏面光对一个受光面最多发起四条遮挡测试。缓存就绪后，每轮传播开销主要取决于实际探针数。 |
| 设备与容量 | 需要 Compute Shader 和可随机写入、可线性过滤的 RGBA16F 3D 纹理。均匀布局上限为 `1,048,576` 个探针。混合密度布局将同一上限用于细体素网格，另检查图集尺寸和注入缓冲容量。 |

## Android 打包

Gather 的深度附件使用兼容的深度渲染格式、单采样和非 Memoryless 配置，预通道显式 Store 法线与深度内容。启动时检查深度 RT 和颜色格式支持，附件创建失败时跳过 Gather，并用 LRT 日志记录设备、图形 API 和格式。每个相机开始时重置 GI 就绪标志，避免读取旧帧。真机出现异常时可在 Logcat 中筛选 LRT。

[Build Settings](../ProjectSettings/EditorBuildSettings.asset) 中 **Demo_D_LRT** 是启用的构建场景。设备需要支持 Compute Shader，以及可随机写入、可线性过滤的 RGBA16F 3D 纹理。编辑器的烘焙工具使用 Inspector 中的灯光模式，Android Player 通过 `Light.bakingOutput.lightmapBakeType` 判断纯烘焙灯。**Bake Resolution** 属于编辑器工具。

Android Player 和 Editor 的 C# 编译均已验证。最近一次 Unity 日志显示 APK 构建成功，测试机小米15已跑通。

## 常见现象

| 现象 | 检查项 |
| --- | --- |
| 低分辨率 Gather 下物体轮廓有亮边 | 切换到 **Bounce only**，把 **Resolution Scale** 从 `0.5` 临时调到 `1`。亮边随之收窄时，优先检查低分辨率 RT 的双线性上采样。 |
| 粗网格下薄墙漏光 | 体素占据按中心判定，薄墙可能落在两排中心之间。检查 Trunk 的格距和 Color SDF 烘焙分辨率。 |
| 斜墙上出现暗条纹 | Color SDF 按物体朝向采样，体素仍是轴对齐格子。可对比 **Probe Sample Radius** 为 `0` 和 `0.5` 时的条纹及接触阴影。 |
| Trunk 天空遮蔽在进入 Play 后短暂变化 | 等待 **Sky visibility ready**。比较时使用相同的视图、环境光和 **Sky Occlusion Strength**。 |
| 点光或聚光改变后反弹不明显 | 检查灯光 Mode、范围、锥角和间接倍率，确认灯上有 **LRT Light**，且 Trunk 场景已烘焙相关几何。 |
| 点光小幅移动时反弹亮度跳变 | 检查灯位置是否跨过实体体素边界，以及灯是否贴近注入采样面。近处的平方反比衰减和高 **Indirect Multiplier** 会放大跳变。 |
| 面光改了方向却没照亮预期表面 | 检查 Gizmos 的发光方向以及接收面是否在 Range 内。用 **Bounce only** 查看反弹光。 |
| 切换调试视图画面没变 | Trunk 演示检查表面是否使用 **LRT Lit** 且位于探针体积内。 |

## 文件索引

| 文件 | 职责 |
| --- | --- |
| [LocalRadianceTransferDemo.cs](LocalRadianceTransfer/Runtime/LocalRadianceTransferDemo.cs) | 网格和 GPU 资源、灯光与几何变化检测、传播调度、调试界面。 |
| [LrtLight.cs](LocalRadianceTransfer/Runtime/LrtLight.cs) | GI 灯光参数、Unity Light 同步和 Scene Gizmos。 |
| [LrtLighting.hlsl](LocalRadianceTransfer/Shaders/LrtLighting.hlsl) | 光源衰减与体素遮挡接口。 |
| [LrtPropagation.compute](LocalRadianceTransfer/Shaders/LrtPropagation.compute) | 体素遮挡、局部注入、邻域传播、球谐投影。 |
| [LrtProbeSampling.hlsl](LocalRadianceTransfer/Shaders/LrtProbeSampling.hlsl) | 探针系数取样、半球滤波与屏幕空间蓝噪声。 |
| [LrtScreenSpaceGather.cs](LocalRadianceTransfer/Runtime/Rendering/LrtScreenSpaceGather.cs) · [LrtScreenSpaceGather.shader](LocalRadianceTransfer/Shaders/LrtScreenSpaceGather.shader) | URP Pass 调度、低分辨率 RT、打包深度法线预通道与探针求值。 |
| [LrtScreenSpaceGI.hlsl](LocalRadianceTransfer/Shaders/LrtScreenSpaceGI.hlsl) · [LrtIndirectLighting.hlsl](LocalRadianceTransfer/Shaders/LrtIndirectLighting.hlsl) | Base Pass 读取 Gather RT、探针体积检查与环境光求值。 |
| [LrtLit.shader](LocalRadianceTransfer/Shaders/LrtLit.shader) · [LrtLitForward.hlsl](LocalRadianceTransfer/Shaders/LrtLitForward.hlsl) | URP Lit 材质和间接光接入。 |
| [LrtColorSdf.cs](LocalRadianceTransfer/Runtime/LrtColorSdf.cs) · [LrtColorSdfBaker.cs](LocalRadianceTransfer/Editor/LrtColorSdfBaker.cs) | 逐 Prefab 的局部距离场和表面颜色数据及其烘焙。 |
| [LrtSceneBaking.cs](LocalRadianceTransfer/Editor/LrtSceneBaking.cs) | 场景烘焙按钮、Prefab 数据复用和 Demo_D_LRT 构建入口。 |
| [LrtTrunkScene.cs](LocalRadianceTransfer/Runtime/LrtTrunkScene.cs) | GI Primitive 登记、Trunk 空间索引、占据缓存与脏块上传。 |
| [LrtProbeLayout.cs](LocalRadianceTransfer/Runtime/LrtProbeLayout.cs) · [LrtProbeAtlas.hlsl](LocalRadianceTransfer/Shaders/LrtProbeAtlas.hlsl) | 粗细探针砖块布局和图集寻址。 |
| [LrtDemoHud.cs](LocalRadianceTransfer/Demo/LrtDemoHud.cs) · [DemoDayNightCycle.cs](LocalRadianceTransfer/Demo/DemoDayNightCycle.cs) | 演示 HUD、FPS 读数和昼夜过渡。 |
| [LrtProbeMarkers.shader](LocalRadianceTransfer/Resources/LrtProbeMarkers.shader) | 在探针中心绘制反弹颜色标记。 |
