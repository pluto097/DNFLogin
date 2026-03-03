# DOF 下载更新器（无登录版）

基于 [onlyGuo/DNFLogin](https://github.com/onlyGuo/DNFLogin) 重构，仅保留 **下载 + 更新 + 启动游戏** 功能，已移除登录/注册流程。

## 功能

- 首次运行自动检查基础资源（如 `Script.pvf`），缺失时下载完整包
- 启动时从云端拉取 `update-manifest.json` 执行增量更新
- 内置 `aria2c` 下载引擎，支持断点续传、16 线程高速下载
- 内置 `7za` 解压引擎，无需额外安装任何工具
- 支持多下载线路（`downloadRoutes`），某条线路失败后自动切换备用线路
- 支持分卷压缩包下载（`downloadUrls` 数组）
- 更新完成后自动启动指定游戏 EXE

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

每个包（`fullPackage` / `incrementalUpdates` 中的元素）支持以下下载地址格式，三种可混用，优先级从高到低：

| 字段 | 说明 |
|---|---|
| `downloadRoutes` | **多线路下载**（推荐）：配置多条镜像线路，某条失败后自动切换下一条 |
| `downloadUrls` | 单线路分卷下载：一个 URL 数组，按顺序下载多个分卷文件 |
| `downloadUrl` | 单线路单文件下载：一个 URL 字符串 |

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

每条线路内同样支持 `downloadUrl`（单文件）和 `downloadUrls`（分卷）。

---

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

> 程序会先尝试"电信线路"，若失败则自动切换到"联通线路"。

## 使用方法

1. 在云端 Web 服务器上部署 `update-manifest.json`，配置完整包和补丁的下载地址
2. 编辑程序目录下的 `launcher-config.json`，将 `updateManifestUrl` 指向云端清单地址，`gameExePath` 指向游戏启动文件
3. 启动程序，程序会自动完成以下流程：
   - 读取本地配置 → 拉取云端清单 → 检查基础资源 → 下载完整包（如需要）→ 逐个应用增量补丁 → 启动游戏

无需手动安装 `aria2c` 或 `7z`，这两个工具已内嵌在程序中，运行时自动释放。

## 编译方法

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（或与 `TargetFramework` 匹配的版本）
- Windows x64 系统

### 构建步骤

1. 克隆仓库：

```bash
git clone https://github.com/<your-username>/DNFLogin.git
cd DNFLogin
```

2. 本仓库不包含aria2和7z，请自行下载`aria2c.exe`和`7za.exe`放入DNFLogin文件夹根目录。

3. 发布为单文件可执行程序：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

4. 产物位于：

```
bin\Release\net10.0-windows7.0\win-x64\publish\DNFLogin.exe
```

生成的 `DNFLogin.exe` 为自包含单文件，内嵌 .NET 运行时、`aria2c.exe` 和 `7za.exe`，可直接在任意 Windows x64 机器上运行，无需安装 .NET Runtime。

## 致谢

- [onlyGuo/DNFLogin](https://github.com/onlyGuo/DNFLogin) - 原始项目
- [aria2](https://aria2.github.io/) - 高性能下载引擎
- [7-Zip](https://www.7-zip.org/) - 压缩/解压工具
- [AduSkin](https://github.com/aduskin/AduSkin) - WPF UI 框架
