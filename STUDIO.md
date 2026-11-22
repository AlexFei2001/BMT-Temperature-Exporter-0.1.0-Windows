# BMT Thermal Analysis Studio 0.2.1

## 中文快速上手

主窗口保留原来的打开 BMT、温度导出和 IRSoft 参考点核对。只想提取整幅温度时，选中图像后点 **Export selected**，输出仍是摄氏温度矩阵。

### Thermal ROIs · 看一张图的局部温度

1. 先打开并选中一张 BMT，点 **Thermal ROIs**。
2. 选 **Rectangle**（矩形）、**Polygon**（多边形）或 **Point**（单点），可在旁边输入名称。点 **New ROI** 开始。
3. 矩形点两个对角，单点点一次。多边形沿边界依次点击，再点 **Finish**。
4. 左边选中 ROI，底部显示像素数、均值、最低温、最高温和总体标准差。拖动黄色顶点可以调整边界，**Rename** 和 **Delete** 用于改名和删除。
5. **Use ROIs** 保留本次打开应用期间的设置。需要下次接着做时，用 **Save ROIs** 保存 JSON，再在同一张 BMT 上用 **Load ROIs** 读取。

### RGB registration · 对齐热图与可见光图

适合需要确认「热图上的异常在 RGB 照片的哪个位置」的情况。仅看热图温度或做固定相机监测时，可以跳过。

1. 点 **RGB registration**。窗口左边是热图，右边是 BMT 的内嵌 RGB。如果没有内嵌图，点 **Open external RGB**。
2. 鼠标停在要看的位置滚动滚轮，即可在该处放大或缩小。按住左键拖动平移。轻点左键才会加点。热图、RGB 和结果叠加图都能独立缩放。点 **Fit views** 恢复全图。
3. 左边点一个热图特征，右边点同一个实际位置，组成一对。优先选两张图都看得清的窗角、石材接缝等特征。在画面不同位置选至少四对，建议多于四对。
4. 点 **Compute homography**。在 **Overlay** 同一画面看两张图，调整 **Overlay opacity**。右栏列出每对点的重投影误差，还显示内点数、内点平均误差、最大误差和 RMSE。
5. 点右栏的某一对可放大并定位。RGB 和叠加图里的圆点是你点的位置，叉号是热图点经单应矩阵投过来的位置，连线表示它们的差距。颜色沿用旧工具的 3 和 8 像素分段，RANSAC 内点或外点另在列表标明。
6. 如果配错了，选中该对，点 **Delete selected pair**，重加再计算。**Save fit report** 保存配对坐标、矩阵和误差。误差单位是原始 RGB 像素，能帮助检查拟合点，独立配准精度仍要用未参与拟合的点核对。

Studio 保留论文 `registration.py` 的计算过程，并参考 `register_rgb_ir.py` 重做 WPF 点选与误差复查。叠加图只用于显示，原始温度数据保持在热图自己的网格中。

### Monitoring · 看固定相机拍摄的一组图

1. 点 **Monitoring → Open sequence**，选同一机位、同一次实验的 BMT。**Edit first-frame ROIs** 打开首帧，按上面的方式画区域或测温点。
2. 如果要比较异常区与背景，在 **Anomaly ROI** 和 **Reference ROI** 选两个不同区域，点 **Analyze**。图中 ΔT 是「异常区均温减参考区均温」。正值表示异常区更暖，负值表示更冷。
3. 看 **Frame checks**。若出现设置不一致、对齐无法确认或时间警告，先对照实验记录。时间优先用 BMT 内相机时钟，时区未经核实；缺失或重复时使用文件名顺序及帧序号。
4. **Export CSV + PNG** 保存逐帧温度、差值和图。**Save session** 保存 BMT 路径和 ROI，之后用 **Load session** 重开。原 BMT 应留在原路径。

The Windows x64 installer and portable archive include a private registration worker. Extract the whole ZIP before opening BmtExporter.exe. End users do not need to install or configure Python. The existing thermal extraction and IRSoft reference workflow remains available from the main window.

## Thermal ROIs

Select a BMT and open **Thermal ROIs**. Choose Polygon, Rectangle, or Point and enter a name. Click **New ROI** and then click the thermal pixels. A rectangle takes two opposite corners. A point takes one click. For a polygon, click the vertices in boundary order, then click **Finish**. Select an existing ROI and drag one of its handles to change its geometry. Use **Rename**, **Delete**, and **Save ROIs** as needed. **Use ROIs** applies the edits in the current app window. **Load ROIs** checks the original BMT's hash and dimensions.

Coordinates are zero-based native pixel centers, with the origin at the upper left. Boundary pixels are included. SD is the population standard deviation in Celsius. The preview may scale to fit the window, while sampling always uses original matrix values.

## RGB registration

Open **RGB registration**, use the embedded RGB or open an external image, then alternate thermal and RGB clicks for each matched point. Add at least four well-distributed pairs and click **Compute homography**. The result maps thermal pixel centers to RGB pixel centers. Review the overlay, individual RGB-pixel residuals and RANSAC inliers. A low fit residual describes the chosen correspondences and is not an independent registration accuracy estimate. The overlay is warped for display only. Exporting the fit report records the transform and correspondences.

Use the wheel to zoom at the cursor and left-drag to pan. Select a pair in the error list to inspect its location. Circles are manually clicked RGB points and crosses are projections of their thermal matches. Reset the panels with **Fit views**.

The worker calls `ThermalRegistrar(method="manual")` from the local thesis `legacy_crack_pipeline/modules/registration.py` unchanged, together with its existing `modules._types` dependency. The source used during development had SHA-256 `585964b44b3d4d675a75b4c655c488830fef1774c9c128d63ebf51677e3bd90d`. The worker converts previews and serializes the result. It does not fit a separate homography.

## Fixed-camera monitoring

Select BMT files from one acquisition session or open them from **Monitoring**. When all camera timestamps are plausible and distinct, the application sorts by the embedded clock and shows acquisition time. The clock's time zone is unknown. Files without usable timestamps or with duplicates in their timestamps use filename order and a frame-index axis, with a warning. Date patterns in filenames serve as a cross-check, not a timestamp substitute.

Open **Edit first-frame ROIs** to define masks and individual points, then **Analyze**. The same native indices are used for all frames. A dimensions or camera-model mismatch stops analysis. Settings fingerprints and approximate stationary thermal edges are compared to the first frame. The alignment score is cosine similarity of gradients at unchanged sample locations. A value below 0.65 or insufficient edge energy warns that alignment is unverified. Real changes in heat patterns can affect the score. Inspect the **Frame checks** tab and retain only truly fixed-camera sequences. The software never estimates a temporal transform or resamples the frames.

Select different anomaly and reference regions to plot their mean temperatures and ΔT, defined as anomaly mean minus reference mean. Each frame's count, mean, min, max, population SD and check notes are included for every ROI in the CSV. The PNG contains the plots, and the session JSON stores original source paths, hashes and native ROI definitions. Keep the BMT files in place for reopening.

## Developer build

Use .NET 8 SDK, Inno Setup and standard x64 CPython (3.13 tested) with `registration-build-requirements.txt` in a build environment. Pass its Python executable as `-Python` to `build.ps1`. The GitHub source includes the unchanged registration module and its dependencies under `registration_source`. In the thesis workspace, the script can use the original local copy, or another checkout supplied via `-RegistrationSource`. Build output is under `dist`.
