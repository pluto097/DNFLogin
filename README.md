# DNFLogin - Dungeon & Fighter Launcher

基于 [onlyGuo/DNFLogin](https://github.com/onlyGuo/DNFLogin) 重构，仅保留 **下载 + 更新 + 启动游戏** 功能，已移除登录/注册流程。

## 功能概述

- 首次运行自动检测基础资源（如 `Script.pvf`），缺失时下载完整资源包
- 启动时从云端拉取 `update-manifest.json`，自动执行增量版本更新
- 内置 `aria2c` 下载引擎：支持断点续传、多线程并发下载（普通线路 16 线程，API 线路 8 线程）
- 内置 `7za` 解压引擎：无需额外安装任何解压工具
- 支持多下载线路（`downloadRoutes`），线路故障或速度异常时自动切换备用线路
- 支持分卷压缩包下载（`downloadUrls` 数组），自动按顺序下载并合并解压
- 下载速度实时监控：当速度 <= 2 MiB/s 持续 30 秒时，自动切换到下一条线路
- 更新完成后自动启动指定游戏 EXE，启动器随即退出

## 技术架构

| 组件 | 说明 |
|---|---|
| **WPF + AduSkin** | UI 框架，使用 [AduSkin](https://github.com/aduskin/AduSkin) v1.1.1.9 提供无边框窗口和现代化界面 |
| **aria2c** | 高性能下载引擎，作为嵌入资源打包在程序内，运行时自动释放到 `.tools` 目录 |
| **7za (7-Zip)** | 压缩/解压工具，同样作为嵌入资源打包，支持 `.7z` 及分卷格式 |
| **.NET 10** | 目标框架为 `net10.0-windows7.0`，支持发布为自包含单文件 |

### 项目文件结构

```
DNFLogin/
├── App.xaml / App.xaml.cs        # WPF 应用入口
├── MainWindow.xaml               # 主窗口 UI 布局（进度条、状态文本、线路信息）
├── MainWindow.xaml.cs            # 核心更新逻辑（下载、解压、线路切换、进度跟踪）
├── DNFLogin/
│   └── LauncherConfig.cs         # 配置模型、清单解析、版本比较
├── Assets/
│   ├── DNF.ico                   # 应用图标
│   └── default-bg-img.png        # 默认背景图片
├── aria2c.exe                    # 嵌入资源：aria2 下载引擎
├── 7za.exe                       # 嵌入资源：7-Zip 命令行工具
└── DNFLogin.csproj               # 项目配置
```

## 更新流程

程序启动后执行以下流程：

```
初始化 → 释放内嵌工具 → 读取本地配置 → 拉取云端清单
  → 检查基础资源文件是否存在
    → 不存在：下载并解压完整资源包（fullPackage）
    → 存在：跳过
  → 解析待应用的增量更新（版本号 > 当前版本）
    → 按版本号从低到高依次下载并解压增量包
    → 每个增量包完成后立即写回 currentVersion
  → 全部完成后自动启动游戏 EXE
```

### 多线路下载与自动切换

每个下载包支持配置多条下载线路（`downloadRoutes`），程序按配置顺序逐条尝试：

1. 使用当前线路的 URL 调用 aria2c 下载
2. 下载过程中实时监控速度，若速度 <= 2 MiB/s 持续 30 秒，终止当前下载并切换下一条线路
3. 若下载或解压过程中发生错误，同样自动切换到下一条线路
4. 最后一条线路不触发速度异常切换（避免无限循环）
5. 切换线路时清理当前线路的缓存文件（不同线路 URL 不同，不能交叉续传）
6. 所有线路均失败时，抛出异常并显示错误信息

### 断点续传

- aria2c 启用 `--continue=true`，支持中断后继续下载
- 用户关闭程序时，保留最后一条线路的 `.aria2` 控制文件，下次启动时可断点续传
- 下载完成并解压成功后，自动清理缓存文件

## 配置文件

程序涉及两个配置文件：一个放在本地程序目录，一个部署在云端。

### 1) `launcher-config.json`（本地配置）

放在程序 EXE 同级目录下。首次启动时自动生成默认文件，之后可手动编辑。

```json
{
  "gameExePath": "DNF.exe",
  "baseResourceCheckFile": "Script.pvf",
  "updateManifestUrl": "https://example.com/update-manifest.json",
  "currentVersion": "0.0.0"
}
```

| 字段 | 说明 |
|---|---|
| `gameExePath` | 更新完成后启动的游戏 EXE 路径（支持相对路径或绝对路径） |
| `baseResourceCheckFile` | 用于判断是否需要下载完整包的本地文件，该文件不存在时触发完整包下载 |
| `updateManifestUrl` | 云端 `update-manifest.json` 的 HTTP(S) 直链地址 |
| `currentVersion` | 当前本地版本号，程序更新成功后会自动写回。**请勿手动修改**，除非需要强制重新更新 |

> 程序还会自动迁移旧版 `launcher-state.json` 中的 `currentVersion` 字段到 `launcher-config.json`，迁移后删除旧文件。

### 2) `update-manifest.json`（云端清单）

部署在 Web 服务器上，由 `updateManifestUrl` 指向。程序启动时自动拉取此文件来决定下载/更新行为。

```json
{
  "fullPackage": {
    "version": "1.0.0",
    "downloadUrl": "https://cdn.example.com/full-1.0.0.7z",
    "description": "首次安装完整包"
  },
  "incrementalUpdates": [
    {
      "version": "1.0.1",
      "downloadUrl": "https://cdn.example.com/patch-1.0.1.7z",
      "description": "1.0.1 补丁说明"
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `fullPackage` | 基础资源缺失时下载的完整包 |
| `incrementalUpdates` | 增量补丁数组，按版本号从小到大排列，程序会自动跳过已安装的版本 |

#### 每个包（`fullPackage` / `incrementalUpdates` 元素）支持的下载地址字段

三种格式可混用，优先级从高到低：

| 字段 | 说明 |
|---|---|
| `downloadRoutes` | **多线路下载**（推荐）：配置多条镜像线路，某条失败后自动切换下一条 |
| `downloadUrls` | 单线路分卷下载：一个 URL 数组，按顺序下载多个分卷文件 |
| `downloadUrl` | 单线路单文件下载：一个 URL 字符串 |
| `apiDownloadUrl` | API 线路下载：使用特殊请求头的单文件下载，并发线程数降为 8 |

#### `downloadRoutes` 格式

```json
{
  "downloadRoutes": [
    {
      "name": "主线路",
      "downloadUrl": "https://cdn1.example.com/patch.7z"
    },
    {
      "name": "备用线路",
      "downloadUrl": "https://cdn2.example.com/patch.7z"
    }
  ]
}
```

每条线路内同样支持 `downloadUrl`（单文件）、`downloadUrls`（分卷）和 `apiDownloadUrl`（API 线路）。

### 写法范本

#### 范本 A：仅有完整包，无增量补丁

```json
{
  "fullPackage": {
    "version": "1.0.0",
    "downloadUrl": "https://cdn.example.com/full-1.0.0.7z",
    "description": "首次安装完整包"
  },
  "incrementalUpdates": []
}
```

> `incrementalUpdates` 为空数组即可。客户端若已是 `1.0.0`，会判定"已是最新版本"并直接启动游戏。

#### 范本 B：完整包 + 多个增量补丁

```json
{
  "fullPackage": {
    "version": "1.0.0",
    "downloadUrl": "https://cdn.example.com/full-1.0.0.7z",
    "description": "首次安装完整包"
  },
  "incrementalUpdates": [
    {
      "version": "1.0.1",
      "downloadUrl": "https://cdn.example.com/patch-1.0.1.7z",
      "description": "1.0.1 热更新"
    },
    {
      "version": "1.0.2",
      "downloadUrl": "https://cdn.example.com/patch-1.0.2.7z",
      "description": "1.0.2 内容修复"
    },
    {
      "version": "1.0.3",
      "downloadUrl": "https://cdn.example.com/patch-1.0.3.7z",
      "description": "1.0.3 平衡性调整"
    }
  ]
}
```

> 补丁按版本号从低到高排列。客户端当前版本为 `1.0.0` 时依次应用三个补丁；当前版本为 `1.0.2` 时只应用 `1.0.3`。

#### 范本 C：使用分卷压缩包

```json
{
  "fullPackage": {
    "version": "1.0.0",
    "downloadUrls": [
      "https://cdn.example.com/full-1.0.0.7z.001",
      "https://cdn.example.com/full-1.0.0.7z.002",
      "https://cdn.example.com/full-1.0.0.7z.003"
    ],
    "description": "完整包（分卷）"
  },
  "incrementalUpdates": []
}
```

> 分卷文件按顺序下载，下载完成后自动合并解压。

#### 范本 D：多线路 + 分卷

```json
{
  "fullPackage": {
    "version": "1.0.0",
    "downloadRoutes": [
      {
        "name": "电信线路",
        "downloadUrls": [
          "https://ct.cdn.example.com/full.7z.001",
          "https://ct.cdn.example.com/full.7z.002"
        ]
      },
      {
        "name": "联通线路",
        "downloadUrls": [
          "https://cu.cdn.example.com/full.7z.001",
          "https://cu.cdn.example.com/full.7z.002"
        ]
      }
    ],
    "description": "完整包（多线路分卷）"
  },
  "incrementalUpdates": []
}
```

> 程序会先尝试"电信线路"，若失败或速度过慢则自动切换到"联通线路"。

## 错误处理机制

| 场景 | 处理方式 |
|---|---|
| 配置文件缺失 | 自动生成默认 `launcher-config.json` |
| 配置文件字段缺失 | 缺少 `gameExePath` 或 `updateManifestUrl` 时抛出明确异常 |
| 云端清单解析失败 | 显示 HTTP 状态码或 JSON 解析错误信息 |
| 无下载地址 | 抛出异常提示具体版本未配置下载地址 |
| 下载文件为空 | 解压前验证文件大小，为空时提示检查下载地址 |
| 单条线路下载失败 | 自动切换到下一条备用线路 |
| 下载速度持续过慢 | 速度 <= 2 MiB/s 持续 30 秒后自动切换线路 |
| 所有线路均失败 | 显示最后一个错误信息，保留缓存文件以支持下次断点续传 |
| 7z 解压失败 | 显示 7z 退出码和错误输出 |
| 游戏 EXE 不存在 | 抛出 `FileNotFoundException` 并显示配置的路径 |
| 用户关闭程序 | 弹出确认对话框，确认后终止 aria2c 进程，保留缓存文件 |

## 使用方法

1. 在云端 Web 服务器上部署 `update-manifest.json`，配置完整包和补丁的下载地址
2. 编辑程序目录下的 `launcher-config.json`，将 `updateManifestUrl` 指向云端清单地址，`gameExePath` 指向游戏启动文件
3. 启动程序，程序会自动完成以下流程：
   - 读取本地配置 -> 拉取云端清单 -> 检查基础资源 -> 下载完整包（如需要）-> 逐个应用增量补丁 -> 启动游戏

无需手动安装 `aria2c` 或 `7z`，这两个工具已内嵌在程序中，运行时自动释放到 `.tools` 目录。

## 编译方法

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（与项目 `TargetFramework: net10.0-windows7.0` 匹配）
- Windows x64 系统

### 构建步骤

1. 克隆仓库：

```bash
git clone https://github.com/<your-username>/DNFLogin.git
cd DNFLogin
```

2. 本仓库不包含 aria2 和 7z 可执行文件，请自行下载 `aria2c.exe` 和 `7za.exe` 放入项目根目录（与 `.csproj` 同级），它们会作为嵌入资源打包进最终产物。

3. 发布为单文件可执行程序：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

4. 产物位于：

```
bin\Release\net10.0-windows7.0\win-x64\publish\DNFLogin.exe
```

生成的 `DNFLogin.exe` 为自包含单文件，内嵌 .NET 运行时、`aria2c.exe` 和 `7za.exe`，可直接在任意 Windows x64 机器上运行，无需安装 .NET Runtime。

### 调试运行

```bash
dotnet run
```

> 调试模式下需确保 `aria2c.exe` 和 `7za.exe` 位于项目根目录。

## 版本兼容性

- 目标框架：`.NET 10`（`net10.0-windows7.0`）
- 运行平台：Windows 7 及以上（x64）
- UI 框架：AduSkin 1.1.1.9
- 版本号格式：使用 `System.Version` 进行语义化版本比较，支持标准的 `Major.Minor.Build.Revision` 格式
- 配置兼容：自动迁移旧版 `launcher-state.json` 中的版本信息到 `launcher-config.json`

## 致谢

- [onlyGuo/DNFLogin](https://github.com/onlyGuo/DNFLogin) - 原始项目
- [aria2](https://aria2.github.io/) - 高性能下载引擎
- [7-Zip](https://www.7-zip.org/) - 压缩/解压工具
- [AduSkin](https://github.com/aduskin/AduSkin) - WPF UI 框架
