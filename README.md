# 校园浏览器 · 服务端（Campus Browser Server）

ASP.NET Core 8 Minimal API 服务端：Web 图形化后台生成配置，向安卓平板下发带 RSA 签名的配置，管理设备与版本。数据存于本地 SQLite，Windows Server 优先、兼容 Linux。

当前版本：**v0.8.0**（日志 / 内置 HTML 页 / 首页实时图表）

## 功能

- **分级菜单后台**：首页 / 配置管理 / 设备管理 / HTML 页 / 操作日志 / 版本历史 / 系统设置，切换带淡入淡出过渡动画
- **首页实时概览（v0.8.0）**：当前在线设备数、设备总数、离线数、**在线设备占比环形图**，以及**今日 00:00 起的在线设备折线图**（服务端每 5 秒采样一次，页面 1 秒刷新一次；采样点保留 7 天）
- **操作日志（v0.8.0）**：记录登录 IP/时间/结果（成功、失败、被限频）、管理操作（发布/回滚配置、改安全设置、查看照片、导出 CSV、增删 HTML 页）与设备注册/心跳（同类事件 60 秒合并计数）；后台可按事件/时间/关键词筛选、分页、导出 CSV；保留 90 天，写入走内存队列异步落库，不影响接口响应
- **内置 HTML 页（v0.8.0）**：后台在线编辑静态 HTML，服务端以 `http://服务器IP:端口/h/页面片段` 公开托管；把该地址写进快捷书签即可在受管平板上打开；HTML 正文不下发到配置（配置只带名称与地址），并自动补 `X-Content-Type-Options` / `Referrer-Policy` 响应头
- Web 图形化后台：首页、白/黑名单、教师快捷书签、轮询间隔、6 位平板管理密码、隐藏入口开关、截屏限制、可唤醒应用白名单、启动拍照开关
- **规则支持端口精确匹配**：`host` 匹配任意端口、`host:8080` 匹配指定端口；匹配不区分 http/https
- 配置版本管理：发布、历史版本、一键回滚（回滚生成新版本号，终端必收到）
- 设备管理：显示每台设备当前 IP（自动更新）、在线/离线状态、最近启动拍照、最近输错密码拍照（最近 10 分钟内输错的设备行标红告警）
- **设备状态筛选**：按 全部/在线/离线 过滤 + 按 ID/名称/IP 关键词搜索
- **设备列表自动刷新**：默认 **1 秒/次**，可选关闭 / 1 秒 / 5 秒，选择会被记住（v0.8.0 起默认 1 秒）
- **设备列表 CSV 导出**：可勾选导出列、可按最后上线时间段筛选、可导出全部设备
- **PIN 服务端校验**：平板把密码 MD5 哈希发到服务端校验（不再本地比对），接口每设备每分钟限 5 次
- **登录安全加固（v0.7.1）**：登录接口每 IP 每分钟限 5 次（429）；用户名不存在与密码错误返回完全一致的提示，杜绝用户名枚举
- **PIN 哈希不再下发（v0.7.1）**：下发给 APP 的配置已剥离 `admin_pin_hash`（服务端版本库保留供校验），消除哈希被未授权获取后离线穷举的风险
- **登录令牌有效期（v0.7.1）**：Token 6 小时有效、每次请求滑动续期；过期令牌自动清理并要求重新登录
- **安装器修复（v0.7.2）**：取消勾选"开机自启动"时安装完也会**立即启动服务**（此前服务停在"已停止"，后台和快捷方式都打不开）；管理后台快捷方式改为真正的 `.url` Internet 快捷方式（此前用 `CreateShortcut` 把网址写进 `.lnk`，双击报「Windows 找不到文件」）；卸载选"保留数据"时若临时目录被占用会中止卸载而不是误删数据库
- **UA 安全校验（可选）**：系统设置中可开启，强制仅允许 OkHttp 客户端访问配置/注册/照片接口，非法客户端 403；默认关闭，待全部终端升级后开启
- **启动/输错前置拍照**：服务端开关控制，拍照失败自动缓存补传；照片按天清理保留 7 天
- 配置 RSA-2048 签名，防止篡改；私钥仅存服务端
- 基于 NSIS 的 Windows 一键安装包：安装时可选端口/目录/开机自启/桌面快捷方式，注册为 Windows 服务；**安装完成即启动服务并等待端口就绪**（"开机自启动"只影响下次开机）
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
│   ├── Database.cs         # SQLite：设置/版本/设备/令牌/日志/HTML页/在线采样（全参数化查询）
│   ├── AuditLog.cs         # 操作日志：内存队列 + 后台落库 + 高频事件节流合并
│   ├── OnlineSampler.cs    # 在线采样后台服务（每 5 秒一次，供首页全天曲线）
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
makensis -DAPP_VERSION=0.8.0 installer/campus-browser-server.nsi
# 输出 installer/CampusBrowserServer-setup.exe
# 若在中文 Windows 上编译，追加 -INPUTCHARSET UTF8，避免无 BOM 脚本按 ACP 解析导致中文乱码
```

安装器特性：端口占用检测、升级从注册表回填上次目录、卸载询问是否保留数据（默认保留，不会误删数据库）、自动注册服务并配置失败自动重启、放行防火墙、**安装完成即刻启动服务并等待端口就绪**（"开机自启动"只影响下次开机）、桌面/开始菜单为 `.url` 管理后台快捷方式（未勾选桌面快捷方式时不创建，并清理旧版残留）。

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
| POST | `/api/v1/photo` | 照片上报（type=startup 启动拍照 / wrong_pin 输错密码拍照）；v0.7.3 起不再按设备限频 |
| POST | `/api/v1/verify-pin` | PIN 哈希服务端校验（每分钟限 5 次，429） |
| GET | `/api/v1/public-key` | 获取签名公钥 |
| GET | `/h/{slug}` | 内置 HTML 页（v0.8.0，公开只读，可直接加进书签） |

**管理端（需 `Authorization: Bearer <token>`）**

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/login` / `/api/logout` | 登录 / 登出 |
| GET/POST | `/api/admin/config`、`/api/admin/config/publish` | 查看 / 发布配置 |
| GET/POST | `/api/admin/config/versions`、`/api/admin/config/rollback?version=x` | 版本列表 / 回滚 |
| GET | `/api/admin/devices` | 设备列表（含 IP、在线状态、拍照时间） |
| GET | `/api/admin/devices/export.csv` | 设备列表 CSV 导出（可选列、可选时间段） |
| GET | `/api/admin/photo?deviceId=&file=&type=` | 查看设备照片（type=startup/wrong_pin） |
| GET | `/api/admin/security` | 读取 / 更新安全设置（UA 强制校验开关） |
| GET | `/api/admin/public-key` | 查看公钥（内置到 APP） |
| GET | `/api/admin/stats/overview` | 首页概览（v0.8.0）：在线/离线/总数/占比 + 今日 00:00 起在线采样点 |
| GET | `/api/admin/html-pages` | 内置 HTML 页列表（含访问地址与体积） |
| POST | `/api/admin/html-pages` | 新建 / 更新 HTML 页（id=0 为新建，slug 冲突即更新） |
| DELETE | `/api/admin/html-pages/{id}` | 删除 HTML 页 |
| GET | `/api/admin/logs` | 操作日志查询（v0.8.0：event/from/to/q/limit/offset，from/to 支持 Unix 秒或 ISO） |
| GET | `/api/admin/logs/export.csv` | 操作日志 CSV 导出（UTF-8 BOM） |

## 配置字段

| 字段 | 说明 |
| --- | --- |
| `home_url` / `fallback_url` | 首页 / 备用网址 |
| `mode` | `whitelist` 或 `blacklist` |
| `rules` | 规则数组，支持 `*.example.edu.cn`、`host:8080` 端口、`https://host/*` 路径、精确网址；不区分协议 |
| `bookmarks` | 教师快捷书签 `[{title,url}]` |
| `allowed_apps` | 可唤醒应用白名单 `[{package,label,scheme}]` |
| `hidden_entry_enabled` | 是否允许 APP 隐藏入口（关闭后仅服务器可重开） |
| `block_screenshot` | 是否全局禁止截屏/录屏 |
| `update_interval_seconds` | APP 轮询间隔（30~86400） |
| `require_startup_photo` | 是否要求平板启动时拍前置照片上传（默认 false） |
| `html_pages` | 内置 HTML 页 `[{slug,title}]`（v0.8.0，正文不下发，页面由 `/h/{slug}` 提供） |
| `admin_pin_hash` | 平板 6 位管理密码哈希（v0.7.1 起不下发给 APP，仅服务端校验用） |

## 安全说明

- 后台登录密码 PBKDF2-SHA256（10 万次迭代 + 随机盐 + 定长比较）
- **登录限频**：每 IP 每分钟 5 次（v0.7.1，.NET 8 内置滑动窗口限频器）
- **登录防枚举**：用户名不存在与密码错误返回完全一致（v0.7.1）
- 登录令牌 192 位 CSPRNG；**6 小时 TTL + 滑动续期**，过期自动清理（v0.7.1）
- 全部 SQL 查询参数化，无字符串拼接注入面
- 管理后台所有动态输出经 HTML 转义（v0.4.0 修复存储型 XSS）
- 响应不暴露 Server 头
- 配置 RSA-2048 签名，APP 验签失败拒绝使用；下发前剥离 PIN 哈希并重签（v0.7.1）
- **操作日志不记录敏感内容（v0.8.0）**：只记 IP/时间/事件/结果，绝不记录密码、PIN 哈希与令牌；日志接口与导出均需登录
- 内置 HTML 页的 slug 走白名单正则（`^[a-z0-9][a-z0-9-]{0,63}$`）杜绝路径穿越，响应带 `X-Content-Type-Options: nosniff` 与 `Referrer-Policy: no-referrer`
- 内网默认 HTTP，建议生产环境通过 HTTPS 反向代理或启用 TLS

## 开源协议

GPL-3.0
