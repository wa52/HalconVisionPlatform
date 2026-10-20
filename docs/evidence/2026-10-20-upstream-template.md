# 上游当前帧模板示教验证 · 2026-10-20

问题：用户期望模板示教接收前一图像节点输出；旧实现无有效运行结果时直接读取文件夹第一张，而且必须另存模板，容易混淆“当前传入图像”和“模板文件”。

## 修复及回归

- 红测试：`dotnet run --project tools/HalconTemplateInputTrial/HalconTemplateInputTrial.csproj -c Release --no-restore -p:NuGetAudit=false -- artifacts/template-input-red-20261020` 实际失败于“没有运行结果时不能擅自读取文件夹第一张冒充上游当前帧”。不是编译失败。
- 修复：只复制连接端口的当前 HImage，核对成功结果、RunId、递归上游配置签名及单帧数量。无有效帧时执行所连接生产者及其依赖，不执行未示教的匹配节点或无关分支。相机图标无另存对话框，生成项目内唯一快照和资源引用。切换待检帧不改变模板。
- 14项模板检查：`artifacts/template-input-final-20261020/checks.txt`，包括无帧拒绝、上游子图执行、第二帧像素（200，对照第一帧0）、实际按钮写快照、旧配置拒绝、重新示教不覆盖旧模板、保存恢复、上游读取失败无旧输出。检查程序最初缺少 WPF DispatcherSynchronizationContext，异步续体跨线程失败；补全真实 Dispatcher 上下文后重跑通过，没有修改生产代码去掩盖检查环境问题。
- 235项真实HALCON匹配回归：`artifacts/quick-match-template-regression-20261020`。
- 532项位置矫正回归：`artifacts/position-template-regression-20261020`；首次命令漏传输入夹而返回用法，补齐后通过。
- 布局128项、实际节点添加24项通过；Release publish成功。

## 实际窗口

独立候选程序：`artifacts/desktop-template-input-checked`，检查项目 `artifacts/template-input-native-review/native.hvproj`。未操作用户项目、原图或模型。

真实鼠标点击空模板的相机图标：自动执行读取图像并显示模板，无保存对话框；Ctrl+S保存，再单次运行，HALCON接收一个可信匹配目标，显示绿色轮廓。检查窗口正常保存关闭。

截图 [真实运行匹配结果](../screenshots/quick-match-upstream-template-20261020.png)，仅包含目标软件窗口，使用本轮生成的合成L形图；不能据此声称产线准确率。当前真实窗口约1568×933；较小尺寸由布局检查覆盖，边角拖动未实际改变窗口尺寸，不计为真人小窗验收。未重新验证多DPI、多显示器、相机硬件及长时间执行。

## 视觉审查

七项诊断各4/5：整体协调性、视觉层次、排版质量、组件品质、空间利用、交互体验、产品完成度。检查限于模板行及其上下文；沿用既有主题和五项常用参数布局，相机入口与文件入口作用不同，没有新增重复入口。中文提示沿用1500ms全局规则及既有图标样式，布局检查验证控件边界。未把自评分当作完整商业品质验收。

## 限制

模板写独立项目资产，绝对路径仍需保留；搬迁项目目录后的路径重定位尚未实现。旧模板资产保留以支持撤销，未增加自动清理。上游必须提供单帧HImage，多图集合不默认取第一张。模板特征质量诊断、参数优化和8小时稳定性仍不属于本轮成果。
