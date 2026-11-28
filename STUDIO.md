# BMT Thermal Analysis Studio 0.4.0

## 中文快速上手

主窗口保留原来的打开 BMT、温度导出和 IRSoft 参考点核对。只想提取整幅温度时，选中图像后点 **Export selected**，输出仍是摄氏温度矩阵。

### FLIR 摄氏温度提取

1. 点 **FLIR settings**，选择 ExifTool 的 `exiftool.exe`。Windows 版应和解压时附带的 `exiftool_files` 文件夹放在一起。程序会记住此路径和字节序。
2. 选择字节序。本项目测试过的四张 FLIR T1020 文件用 `swap16`，其他相机和文件需与 FLIR 软件核对后再选。批量操作使用当前所选字节序，按字节序分批处理不同来源。
3. 点 **Open thermal files** 或拖入原始 FLIR 辐射 JPEG。**Thermal** 显示从温度阵列生成的灰度图。**Source JPEG** 是原始文件的显示画面，可能包含色条、文字或融合图。温度和 ROI 请在 Thermal 页查看。
4. 点 **Export selected**。FLIR 的 `temperature.npy` 为 float64 摄氏矩阵，CSV 往返保留同样的值。BMT NPY 继续使用原来的 float32。可混批导出，普通 JPG 或缺少必要标定参数的文件会在 `summary.json` 中列出失败原因。

本版沿用原 FLIR 读数和环境补偿代码，使用照片保存的测温参数。 `metadata.json` 记录字节序、ExifTool 版本和换算参数。应用自带 Python 运行环境，ExifTool 需单独取得。新增的 FLIR 支持覆盖单图查看、ROI 和批量导出。Folder Sync、Monitoring 和 IRSoft 参考流程仍用于 Testo BMT。FLIR 数值仍需与厂商软件独立核对。

### Thermal ROIs · 看一张图的局部温度

1. 先打开并选中一张 BMT，点 **Thermal ROIs**。
2. 选 **Rectangle**（矩形）、**Polygon**（多边形）或 **Point**（单点），可在旁边输入名称。点 **New ROI** 开始。
3. 矩形点两个对角，单点点一次。多边形沿边界依次点击，再点 **Finish**。
4. 左边选中 ROI，底部显示像素数、均值、中位数、最低温、最高温和总体标准差。拖黄色顶点调整边界；按住 Shift 拖动可移动整个 ROI。滚轮围绕指针缩放，中键拖动平移。可用 **100%** 按屏幕物理像素查看，或用 **Focus ROI** 聚焦选区。
5. **Use ROIs** 保留本次打开应用期间的设置。需要下次接着做时，用 **Save ROIs** 保存 JSON，再在同一张 BMT 上用 **Load ROIs** 读取。

### RGB registration · 对齐热图与可见光图

适合需要确认「热图上的异常在 RGB 照片的哪个位置」的情况。仅看热图温度或做固定相机监测时，可以跳过。

1. 点 **RGB registration**。窗口左边是热图，右边是 BMT 的内嵌 RGB。如果没有内嵌图，点 **Open external RGB**。
2. 鼠标停在要看的位置滚动滚轮，即可在该处放大或缩小。按住左键拖动平移。轻点左键才会加点。热图、RGB 和结果叠加图都能独立缩放。点 **Fit views** 恢复全图。
3. 左边点一个热图特征，右边点同一个实际位置，组成一对。优先选两张图都看得清的窗角、石材接缝等特征。在画面不同位置选至少四对，建议多于四对。
4. 两侧都可以拖动已有控制点。点击点后用方向键每次移动一个原生像素，或者选择图像、填 X/Y、点 **Apply pixel**。配对编号和 ID 保留。放开鼠标后自动重算；**Undo** 和 **Redo** 用于恢复编辑。
5. 点 **Focus pair** 定位对应点。RGB 窗格的圆点是手动点选位置，叉号是拟合后的投影位置。右栏区分 RANSAC 内外点，显示逐点残差和内点统计。要测独立配准精度，请用未参与拟合的点另行验证。
6. **Delete point** 只删除当前图像上的点，点击空白位置补回对应点。**Delete pair** 则删除整对。切换到 **Registered preview**，可查看融合、棋盘格和配准热图。调整配色、温区和网格后点 **Apply display**，透明度可直接用滑块调节。
7. 点 **Export registration** 输出 RGB、配准热图、融合图、棋盘格、有效像素掩膜、摄氏温度 NPY/CSV、矩阵和控制点。在导出前勾选色条、点位和编号会将相应标注叠加到原生 RGB 分辨率的可视化图里。

Studio 保留论文 `registration.py` 的单应拟合。配准温度网格从原始热矩阵反向映射到 RGB 像素中心，用最近邻取样一次。无效像素在 NPY 中为 NaN、CSV 中为空、掩膜中为 0。融合图、棋盘格和配色热图仅供目视检查，监测统计仍以原始热图网格为准。

### Monitoring · 看固定相机拍摄的一组图

1. 点 **Monitoring → Open sequence**，选同一机位、同一次实验的 BMT。**Edit first-frame ROIs** 打开首帧，按上面的方式画区域或测温点。
2. 点 **Analyze** 后，**Chart metric** 默认显示所有可用 ROI 的均温。**Relative · Initial change** 从每个 ROI 自身的基线读数计算变化量。比较异常和参考区时，在 **Reference settings** 选定两者，再切换到 **Contrast · Anomaly pair**。
3. 在 **ROI visibility and status** 中选中 ROI，可切换单条曲线的显示状态、批量显示或隐藏、改名和设定角色。同名 ROI 用编号和 ID 区分。**Reference** 与 **Material** 可以同时设定。角色标记和实际用于计算的参考选择分别保存。
4. 在 **Analysis settings** 分别设时间原点、温度基线、事件、导数单位和回归窗口。实际时间戳可用时优先使用。时间不全时可在文件名顺序下保留有可信时间的帧，也可明确填写采样间隔，重建的横轴会标注来源。
5. 环境温度可在 **Reference settings** 输入常量，或在 **Analysis settings → Ambient series** 输入或导入 `elapsed_seconds,temperature_c`。时间从有效时间轴的零点算起，跟随原始时间轴而非手动设置的实验原点。序列仅在相邻观测点间线性插值。**ROI visibility and status** 可为每个 ROI 单独指定局部背景。
6. **Export all curves** 或 **Export visible curves** 分别导出当前阶段中的全部图线或仅可见图线。两种方式都保存完整 CSV、会话、分析参数和动态指标。原始 BMT 应留在原路径，以便 **Load session** 重新打开。

The Windows x64 installer and portable archive include a private registration worker. Extract the whole ZIP before opening BmtExporter.exe. End users do not need to install or configure Python. The existing thermal extraction and IRSoft reference workflow remains available from the main window.

## Thermal ROIs

Select a BMT and open **Thermal ROIs**. Choose Polygon, Rectangle, or Point and enter a name. Click **New ROI** and then click the thermal pixels. A rectangle takes two opposite corners. A point takes one click. For a polygon, click the vertices in boundary order, then click **Finish**. Select an existing ROI and drag one of its handles to change its geometry. Use **Rename**, **Delete**, and **Save ROIs** as needed. **Use ROIs** applies the edits in the current app window. **Load ROIs** checks the original BMT's hash and dimensions.

Coordinates are zero-based native pixel centers, with the origin at the upper left. Boundary pixels are included. SD is the population standard deviation in Celsius. Zoom and pan are view-only. Shift-drag translates the selected ROI inside the original grid. Dragging a handle edits that native vertex.

## RGB registration

Open **RGB registration**, use the embedded RGB or an external image, and place at least four well-distributed pairs. The fit refreshes after a completed edit. Click a point to select both sides of its pair. Drag or use the arrow keys, or apply integer X/Y values for the selected side. The residual panel reports fit residuals in RGB pixels and identifies RANSAC inliers. Independent checkpoint accuracy is unmeasured by this workflow.

Use the wheel to zoom at the cursor and drag empty space or middle-drag to pan. Select a pair and use **Focus pair** to locate it. In the RGB pane, circles are clicked coordinates and crosses are transformed thermal coordinates. **Export registration** saves the RGB grid products and fit metadata. The visualization range and colors affect the PNG/TIFF products only. The registered temperature matrix is sampled once from the original matrix with inverse-map nearest neighbor. Areas outside coverage are transparent in the registered thermal visualization; fusion/checkerboard show the underlying RGB there.

The worker calls `ThermalRegistrar(method="manual")` from the local thesis `legacy_crack_pipeline/modules/registration.py` unchanged, together with its existing `modules._types` dependency. The source used during development had SHA-256 `585964b44b3d4d675a75b4c655c488830fef1774c9c128d63ebf51677e3bd90d`. The worker converts previews and serializes the result. It does not fit a separate homography.

## Fixed-camera monitoring

Select BMT files from one acquisition session or open them from **Monitoring**. Complete unique timestamps determine sequence order. For incomplete clocks, files stay in filename order. Usable unique timestamps can form a partial axis if they increase in that order. A user-entered interval can reconstruct an incomplete axis in filename order. Without usable timing, charts show frame indices. The original timestamps are always exported when present.

Open **Edit first-frame ROIs** to define masks and individual points, then **Analyze**. The same native indices are used for all frames. A dimensions or camera-model mismatch stops analysis. Settings fingerprints and approximate stationary thermal edges are compared to the first frame. The alignment score is cosine similarity of gradients at unchanged sample locations. A value below 0.65 or insufficient edge energy warns that alignment is unverified. Real changes in heat patterns can affect the score. Inspect the **Frame checks** tab and retain only truly fixed-camera sequences. The software never estimates a temporal transform or resamples the frames.

The default mean chart includes every eligible visible ROI. Initial change subtracts the same ROI's signal at the selected baseline frame. Local contrast subtracts the explicitly mapped background in each frame, using the session default when there is no override. Ambient contrast uses genuine external air measurements. Enter a constant or a time series on the unshifted elapsed axis. The program linearly interpolates within coverage. Material contrast subtracts the selected material reference from every eligible material, and the material pair chart isolates a chosen comparison. Mean, median, min and max are available as the underlying signal.

Finite differences use adjacent valid frames and are placed at the right-hand frame. Configure a maximum time gap when the experiment has known disallowed interruptions. The optional trailing linear regression uses at least three contiguous observations and appears alongside the raw rate. Internal rate units are °C/s. Chart and additional CSV columns can show °C/s, °C/min or °C/h independently of the time-axis units. Phase indicators use raw differences within each event interval.

Both exports include all defined ROI/frame combinations in CSV, even when a chart uses a phase or hidden curves. PNG scope follows the selected export button. Review ROI completeness and **Frame checks** for missing data. Session JSON preserves inputs, including events, references and visibility. Keep original BMT files in place for reopening.

## Developer build

Use .NET 8 SDK, Inno Setup and standard x64 CPython (3.13 tested) with `registration-build-requirements.txt` in a build environment. Pass its Python executable as `-Python` to `build.ps1`. In the thesis workspace, the build locates the original registration module and the FLIR exporter. A separate source checkout can provide those modules via `-RegistrationSource` and `-FlirSource`. Build output is under `dist`.
