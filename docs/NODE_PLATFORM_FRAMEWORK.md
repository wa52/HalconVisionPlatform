# 节点式平台与文件模块实施规格（框架优先）

状态：架构与验收基线。本文不是已实现功能清单。

## 范围
先建设节点画布、流程数据模型、通用执行引擎、文件资源模块和可扩展节点注册机制。此轮不新增 HALCON 算法节点，不修改已有 Blob 检测行为，不触碰原 VisionInspection 仓库。

## 工作台 UI
WPF + MVVM；左侧项目文件树和节点库，中间多标签节点画布，下方图像预览/日志/执行记录，右侧选中节点属性面板。提供画布平移缩放、框选、拖拽、对齐、连线、删除、复制粘贴、撤销重做、快捷键、未保存提示、错误定位。布局可停靠与恢复；空项目和异常状态有清晰提示。先实现功能可用性，不能仅用静态视觉稿代替。

## 领域模型
Project(id,name,version,root,createdAt,updatedAt)
Flow(id,name,schemaVersion,nodes,edges,variables)
NodeInstance(id,typeId,typeVersion,displayName,position,enabled,parameters)
PortDefinition(id,direction,dataType,required,multiple)
Edge(id,fromNode,fromPort,toNode,toPort)
NodeDefinition(typeId,version,category,ports,parameterSchema,executorKey)
ExecutionRun(id,flowId,startedAt,endedAt,status,nodeResults,errors)

所有 ID 稳定，连线按端口 ID 存储。输入输出类型与参数有版本化契约，禁止把 HALCON 原生对象直接序列化进流程文件。

## 节点扩展协议
INodeDefinitionProvider 提供节点元数据；INodeExecutor 执行节点；INodeRegistry 注册、查询和版本匹配；INodeValidator 校验端口、参数和连线。先内置通用测试节点（常量、透传、日志），用于验证编排，不代表 HALCON 算法交付。缺失插件时保留未知节点数据、明确提示且禁止错误执行。后续 HALCON 节点只通过适配层接入。

## 流程执行
先支持有向无环图：检查悬空边、端口类型、重复连接、缺少必填输入与环；拓扑排序；顺序执行；节点级状态（Idle/Running/Success/Failed/Skipped）；取消请求、错误传播、运行日志和耗时。不能把失败节点输出当作上一次成功结果。第一版不做并行调度、无限循环和跨机器执行。

## 文件模块
工作区可新建/打开/关闭项目；创建文件夹、导入图像、重命名、移动、删除（含确认）、搜索/过滤、最近项目、资源引用检查。项目目录建议：
project.json
flows/*.flow.json
assets/images/
assets/models/
recipes/
runs/
项目资源用相对路径，防止路径穿越；资源移动/重命名需同步引用或阻止操作。保存采用临时文件+原子替换；导入前验证 schema、路径、文件大小及资源完整性；项目打开失败不覆盖当前项目。流程文件支持另存、复制、导入导出和版本迁移。图片原件与检测运行产物分离。

## 实施顺序
P1 项目/文件服务和 JSON schema，纯逻辑单测；
P2 节点定义/注册/端口/边及校验，测试节点；
P3 可交互 WPF 节点画布、属性编辑和撤销重做；
P4 顺序 DAG 执行器、节点状态、日志和错误定位；
P5 流程保存/重开、文件资源引用、导入导出；
P6 端到端 UI 回归、异常测试、独立审核和 GitHub 提交。

## 必须通过的验收
1. 新建两个项目，各建立两个流程；重启应用后节点位置、参数、连线及文件引用完全一致。
2. 至少 30 节点/50 连线画布可拖拽、缩放、复制、撤销重做；不同 DPI 下坐标不漂移。
3. 无效端口、类型不匹配、环、缺失节点定义、损坏 JSON 都有可定位错误，不崩溃、不错误执行。
4. 测试节点组成分支 DAG，执行顺序、节点输入输出、失败传播和运行记录可复现。
5. 文件新建/导入/移动/重命名/删除后的资源引用正确；拒绝路径穿越；取消操作不破坏数据。
6. 未保存关闭有提示；写入失败保持原文件；坏项目不会破坏已打开项目。
7. 原 Blob 工作台现有能力保持可用，HALCON 适配层不被节点 UI 直接依赖。
8. 自动化单测、WPF 实机操作录屏、失败用例、回归报告及独立评审均留证；未实测标 BLOCKED，不能标 PASS。

## Loop
每轮按目标→竞品对标→实现→自动化测试→真实 UI 操作→独立审核→失败修复→回归→更新进度→GitHub 提交。最终目标为可配置、可维护、可部署的商业化视觉平台，不以编译成功代替交付验收。
