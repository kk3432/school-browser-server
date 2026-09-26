# 校园浏览器 · 服务端（Campus Browser Server）

ASP.NET Core 8 Minimal API 服务端：Web 图形化后台生成配置，向安卓平板下发带 RSA 签名的配置，管理设备与版本。数据存于本地 SQLite，Windows Server 优先、兼容 Linux。

当前版本：**v0.6.0**

## 功能

- Web 图形化后台：首页、白/黑名单、教师快捷书签、轮询间隔、6 位平板管理密码、隐藏入口开关、截屏限制、可唤醒应用白名单、启动拍照开关
- 配置版本管理：发布、历史版本、一键回滚（回滚生成新版本号，终端必收到）
- 设备管理：显示每台设备当前 IP（自动更新）、在线/离线状态、最近启动拍照、最近输错密码拍照（最近 10 分钟内输错的设备行标红告警）
- **设备列表 CSV 导出**：可勾选导出列（设备识别码、IP、注册/上线时间、拍照状态等）、可按最后上线时间段筛选、可导出全部设备
- **启动前置拍照**：服务端开关控制，平板启动时静默拍一张前置照片上传，无前置摄像头则上报跳过原因；照片按天清理保留 7 天
- **输错密码拍照告警**：平板管理员密码输错时自动拍一张前置照片上传，后台设备列表可查看并标红提醒
- 配置 RSA-2048 签名，防止篡改；私钥仅存服务端
- 基于 NSIS 的 Windows 一键安装包：安装时可选端口/目录/开机自启/桌面快捷方式，注册为 Windows 服务
- 端口可由程序目录下纯文本 `campus.port` 覆盖（优先级：命令行/环境变量 > campus.port > 默认 8080）

## 开发运行

```bash
dotnet run
# 默认监听 http://0.0.0.0:8080，可用 --urls "http://0.0.0.0:端口" 覆盖
```

首次运行用浏览器打开 `http://服务器IP:8080`，进入初始化向导设置管理员账号与第一份配置。

## 目录结构

```
├── Program.cs              # 全部接口与启动逻辑
├── CampusBrowser.Server.csproj
├── Models/AppConfig.cs     # 配置结构与请求模型
├── Services/
│   ├── Database.cs         # SQLite：设置/版本/设备/令牌（全参数化查询）
│   ├── Security.cs         # 后台密码 PBKDF2-SHA256、平板 PIN 哈希
│   └── Signer.cs           # RSA-2048 配置签名
├── wwwroot/                # Web 管理后台（原生 HTML/JS，无需构建）
├── installer/              # NSIS 安装脚本与图标
├── docs/                   # 架构与配置流程图
└── data/                   # 运行后生成：campus.db（已 gitignore）
```

## 发布与打包

### Windows 一键安装包（自包含，目标机无需 .NET）

```bash
# 1. 发布
dotnet publish CampusBrowser.Server.csproj -c Release -r win-x64 \
  --self-contained true -o installer/publish/win-x64

# 2. 用 NSIS 编译安装包（Linux 下 makensis 可用）
makensis -DAPP_VERSION=0.6.0 installer/campus-browser-server.nsi
# 输出 installer/CampusBrowserServer-setup.exe
```

安装器特性：端口占用检测、升级从注册表回填上次目录、卸载询问是否保留数据（默认保留，不会误删数据库）、自动注册服务并配置失败自动重启、放行防火墙。

### Linux（systemd）

```bash
dotnet publish CampusBrowser.Server.csproj -c Release -r linux-x64 \
  --self-contained true -o /opt/campus-browser-server
```

## 接口一览

**APP 端（无需登录）**

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/v1/config?device_id=xxx` | 拉当前配置；版本号在 `X-Config-Version`，签名在 `X-Signature` |
| POST | `/api/v1/devices/register` | 设备注册（上报名称、版本、IP） |
| POST | `/api/v1/devices/heartbeat` | 心跳上报 |
| POST | `/api/v1/photo` | 照片上报（type=startup 启动拍照 / wrong_pin 输错密码拍照） |
| GET | `/api/v1/public-key` | 获取签名公钥 |

**管理端（需 `Authorization: Bearer <token>`）**

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/login` / `/api/logout` | 登录 / 登出 |
| GET/POST | `/api/admin/config`、`/api/admin/config/publish` | 查看 / 发布配置 |
| GET/POST | `/api/admin/config/versions`、`/api/admin/config/rollback?version=x` | 版本列表 / 回滚 |
| GET | `/api/admin/devices` | 设备列表（含 IP、在线状态、拍照时间） |
| GET | `/api/admin/devices/export.csv` | 设备列表 CSV 导出（可选列、可选时间段） |
| GET | `/api/admin/photo?deviceId=&file=&type=` | 查看设备照片（type=startup/wrong_pin） |
| GET | `/api/admin/public-key` | 查看公钥（内置到 APP） |

## 配置字段

| 字段 | 说明 |
| --- | --- |
| `home_url` / `fallback_url` | 首页 / 备用网址 |
| `mode` | `whitelist` 或 `blacklist` |
| `rules` | 规则数组，支持 `*.example.edu.cn`、`https://host/*`、精确网址 |
| `bookmarks` | 教师快捷书签 `[{title,url}]` |
| `allowed_apps` | 可唤醒应用白名单 `[{package,label,scheme}]` |
| `hidden_entry_enabled` | 是否允许 APP 隐藏入口（关闭后仅服务器可重开） |
| `block_screenshot` | 是否全局禁止截屏/录屏 |
| `update_interval_seconds` | APP 轮询间隔（30~86400） |
| `require_startup_photo` | 是否要求平板启动时拍前置照片上传（默认 false） |
| `admin_pin_hash` | 平板 6 位管理密码哈希 |

## 安全说明

- 后台登录密码 PBKDF2-SHA256（10 万次迭代 + 随机盐 + 定长比较）
- 登录令牌 192 位 CSPRNG，登出即失效
- 全部 SQL 查询参数化，无字符串拼接注入面
- 管理后台所有动态输出经 HTML 转义（v0.4.0 修复存储型 XSS）
- 响应不暴露 Server 头
- 配置 RSA-2048 签名，APP 验签失败拒绝使用
- 内网默认 HTTP，建议生产环境通过 HTTPS 反向代理或启用 TLS

## 开源协议

GPL-3.0
