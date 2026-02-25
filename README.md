# DOF 下载更新器（无登录版）

该版本仅保留 **下载 + 更新 + 启动游戏** 功能，已移除登录/注册流程。

## 功能
- 首次运行自动检查基础资源（如 `Script.pvf`），缺失时下载完整包。
- 启动时从云端直链拉取 `update-manifest.json` 执行增量更新。
- 下载器改为 `aria2c`，支持断点续传/高性能下载。
- 更新完成（或无更新）后自动启动指定 EXE。

## 配置文件
程序目录下会自动生成以下文件：

### 1) `launcher-config.json`
```json
{
  "aria2Path": "aria2c",
  "gameExePath": "DNF.exe",
  "baseResourceCheckFile": "Script.pvf",
  "updateManifestUrl": "https://example.com/update-manifest.json"
}
```

- `aria2Path`: aria2 可执行文件路径（可填绝对路径）。
- `gameExePath`: 更新完成后启动的 EXE（相对/绝对路径都可）。
- `baseResourceCheckFile`: 用于判断是否需要完整包的本地文件。
- `updateManifestUrl`: 云端 `update-manifest.json` 的直链地址，程序启动时自动请求。

### 2) 云端 `update-manifest.json`（由 `updateManifestUrl` 指向）
```json
{
  "fullPackage": {
    "version": "1.0.0",
    "downloadUrl": "https://example.com/full-package.zip",
    "description": "首次安装完整包"
  },
  "incrementalUpdates": [
    {
      "version": "1.0.1",
      "downloadUrl": "https://example.com/patch-1.0.1.zip",
      "description": "示例补丁"
    }
  ]
}
```

- `fullPackage`: 基础资源缺失时下载。
- `incrementalUpdates`: 按版本号从小到大执行更新。

### 3) `launcher-state.json`
```json
{
  "currentVersion": "1.0.0"
}
```

记录当前本地版本，更新成功后自动写入。

## 使用说明
1. 安装并确保 `aria2c` 可执行（或在 `launcher-config.json` 填绝对路径）。
2. 在云端维护 `update-manifest.json`（配置完整包和补丁地址），并把直链填入 `launcher-config.json` 的 `updateManifestUrl`。
3. 启动程序，程序会自动拉取云端清单并完成下载/更新，最终启动 `gameExePath`。
