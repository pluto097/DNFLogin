# DNFLogin

基于 [onlyGuo/DNFLogin](https://github.com/onlyGuo/DNFLogin) 重构的 Dungeon & Fighter 启动器，仅保留**下载 + 更新 + 启动游戏**功能。

## 功能

- 首次运行自动下载完整资源包，后续自动增量更新
- 独立 PVF 更新通道，与常规增量更新互不干扰
- 支持云端推送 `configUpdateUrl`，实现清单地址无感迁移
- 内置 `aria2c`（多线程下载、断点续传）和 `7za`（解压），无需额外安装
- 多线路下载（`downloadRoutes`），故障或速度过慢时自动切换备用线路
- 支持分卷压缩包（`downloadUrls` 数组）
- 速度监控：云端可配置慢速阈值和持续时间，触发自动切换线路
- 支持自定义 aria2 下载参数（`downloadArgs`），可按包或按线路独立配置

## 技术栈

| 组件 | 说明 |
|---|---|
| **WPF + [AduSkin](https://github.com/aduskin/AduSkin)** | UI 框架 |
| **aria2c / 7za** | 下载与解压引擎，作为嵌入资源打包 |
| **.NET 10** | `net10.0-windows7.0`，支持自包含单文件发布 |

## 更新流程

```
初始化 → 释放工具 → 读取本地配置 → 拉取云端清单
  → 同步 configUpdateUrl（如有变更则更新并重新拉取）
  → 检查基础资源 → 缺失则下载完整包（fullPackage）
  → PVF 更新（按版本号依次应用）
  → 常规增量更新（按版本号依次应用）
  → 启动游戏
```

**进度分配**：基础资源包 30% → PVF 更新 20% → 增量更新 50%

## 配置文件

### `launcher-config.json`（本地）

程序 EXE 同级目录，首次启动自动生成。

```json
{
  "gameExePath": "DNF.exe",
  "baseResourceCheckFile": "Script.pvf",
  "updateManifestUrl": "https://example.com/update-manifest.json",
  "currentVersion": "0.0.0",
  "pvfVersion": "0"
}
```

| 字段 | 说明 |
|---|---|
| `gameExePath` | 游戏 EXE 路径 |
| `baseResourceCheckFile` | 检测文件，不存在时触发完整包下载 |
| `updateManifestUrl` | 云端清单地址 |
| `currentVersion` | 当前版本号（自动维护，勿手动改） |
| `pvfVersion` | PVF 版本号（自动维护，勿手动改） |

### `update-manifest.json`（云端）

```json
{
  "slowSpeedConfig": {
    "thresholdMiBps": 2.0,
    "durationSeconds": 30
  },
  "configUpdateUrl": "https://new-cdn.example.com/update-manifest.json",
  "fullPackage": {
    "version": "1.0.0",
    "downloadUrl": "https://cdn.example.com/full-1.0.0.7z"
  },
  "incrementalUpdates": [
    { "version": "1.0.1", "downloadUrl": "https://cdn.example.com/patch-1.0.1.7z" }
  ],
  "pvfUpdates": [
    { "version": "1", "downloadUrl": "https://cdn.example.com/pvf-1.7z" }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `slowSpeedConfig` | （可选）慢速切换配置，`thresholdMiBps` 为速度阈值（MiB/s，默认 2.0），`durationSeconds` 为持续时间（秒，默认 30） |
| `configUpdateUrl` | （可选）清单地址迁移，值不同时自动更新本地配置 |
| `fullPackage` | 完整资源包 |
| `incrementalUpdates` | 增量补丁数组（按版本号升序） |
| `pvfUpdates` | PVF 更新数组（独立版本管理） |

### 下载地址格式

每个包支持三种格式（优先级从高到低）：

| 字段 | 说明 |
|---|---|
| `downloadRoutes` | 多线路（推荐），故障自动切换 |
| `downloadUrls` | 单线路分卷 |
| `downloadUrl` | 单线路单文件 |

每个包和每条线路均可设置 `downloadArgs` 自定义 aria2 参数（线路级优先于包级）。未配置时默认使用 16 线程并发下载。

`downloadRoutes` + `downloadArgs` 示例：

```json
{
  "version": "1.0.1",
  "downloadArgs": "-x 8 -s 8 --min-split-size=4M -k4M",
  "downloadRoutes": [
    {
      "name": "主线路",
      "downloadUrl": "https://cdn1.example.com/patch.7z",
      "downloadArgs": "-x 4 -s 4 --min-split-size=4M -k4M --user-agent=netdisk --referer=https://pan.abc.com/"
    },
    {
      "name": "备用线路",
      "downloadUrl": "https://cdn2.example.com/patch.7z"
    }
  ]
}
```

- 每条线路内同样支持 `downloadUrl` 和 `downloadUrls`
- `downloadArgs` 为自定义 aria2 参数，线路级优先于包级；上例中"主线路"使用 4 线程，"备用线路"继承包级的 8 线程
- 未配置 `downloadArgs` 时，默认使用 `-x 16 -s 16 --min-split-size=4M -k4M`

## 使用方法

1. 在云端部署 `update-manifest.json`
2. 编辑本地 `launcher-config.json`，配置 `updateManifestUrl` 和 `gameExePath`
3. 启动程序，自动完成下载、更新、启动游戏

## 编译

**环境**：[.NET 10 SDK](https://dotnet.microsoft.com/download) + Windows x64

```bash
git clone https://github.com/<your-username>/DNFLogin.git
cd DNFLogin
```

> 需自行下载 `aria2c.exe` 和 `7za.exe` 放入项目根目录。

**发布**：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物：`bin\Release\net10.0-windows7.0\win-x64\publish\DNFLogin.exe`

**调试**：`dotnet run`

## 致谢

- [onlyGuo/DNFLogin](https://github.com/onlyGuo/DNFLogin) — 原始项目
- [aria2](https://aria2.github.io/) — 下载引擎
- [7-Zip](https://www.7-zip.org/) — 解压工具
- [AduSkin](https://github.com/aduskin/AduSkin) — WPF UI 框架
