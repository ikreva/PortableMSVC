# PortableMSVC

PortableMSVC 是一个 .NET 10 / C# Native AOT 单文件工具，用来生成最小化、可便携的 MSVC BuildTools 工具链目录。目标是让 `cl.exe`、`link.exe`、`lib.exe` 以及常见探测链在不安装完整 Visual Studio BuildTools 的情况下可用。

它会从 Visual Studio 官方 manifest 选择并下载所需包，解包 MSVC tools、CRT/ATL/PGO/ASAN/redist、DIA SDK 和 Windows SDK，并生成一套 portable layout、批处理入口和 fake `vswhere.exe` 兼容层。

## 功能特性

- 生成可携带的 MSVC Build Tools 子集，默认输出到 `MSVC\`
- 支持 VS `latest` / `2026` / `2022` / `2019`
- 支持选择 MSVC、Windows SDK、redist 版本
- 支持一个 host 架构和多个 target 架构
- 支持官方 `vcvars*.bat` / `VsDevCmd.bat`，并 patch Windows SDK 查找逻辑优先使用便携路径
- 支持 fake `vswhere.exe`，兼容 .NET Native AOT、CMake、Qt Creator 等常见查询
- 可选 `--setup` / `--clean` 注册和回滚系统 `vswhere` junction 与 Windows SDK 注册表
- 可选下载官方 VC runtime / debug runtime 安装包
- Native AOT 单文件发布，适合放进工具目录或 CI 缓存

## 系统要求

- Windows
- .NET 10 SDK，用于构建本项目
- 网络连接，用于下载 Visual Studio channel manifest 和 payload

开发和离线验证可使用仓库内 `Cache\manifests\` manifest 缓存。

## 构建

```powershell
dotnet publish PortableMSVC.csproj -c Release -r win-x64
```

发布后的可执行文件位于类似路径：

```text
bin\Release\net10.0\win-x64\publish\PortableMSVC.exe
```

发布后会自动尝试使用 UPX 压缩最终 EXE。`win-arm64` 和 `win-arm` 发布会自动跳过 UPX 压缩。推荐把 `upx.exe` 放在：

```text
tools\upx\upx.exe
```

如果没有这个文件，发布仍会继续，只输出一条跳过压缩的 warning。需要临时关闭压缩时：

```powershell
dotnet publish PortableMSVC.csproj -c Release -r win-x64 -p:PortableMSVCCompressWithUpx=false
```

如果想使用其他 UPX 路径：

```powershell
dotnet publish PortableMSVC.csproj -c Release -r win-x64 -p:PortableMSVCUpxPath=D:\Tools\upx.exe
```

## 快速开始

列出可用版本：

```powershell
PortableMSVC.exe list --vs 2022
```

先生成安装计划，不下载：

```powershell
PortableMSVC.exe plan --vs 2022 --vc 14.44 --sdk 26100 --target x64 x86
```

安装 x64/x86 目标工具链：

```powershell
PortableMSVC.exe install --vs 2022 --vc 14.44 --sdk 26100 --target x64 x86
```

安装完成后，默认目录为：

```text
PortableMSVC.exe 所在目录\
  MSVC\
    BuildTools\
    Windows Kits\
    Scripts\
    VisualStudio\
    Setup.bat
    Clean.bat
```

进入 MSVC 环境：

```bat
call MSVC\BuildTools\VC\Auxiliary\Build\vcvars64.bat
cl /?
```

用于 .NET Native AOT：

```bat
call MSVC\BuildTools\VC\Auxiliary\Build\vcvars64.bat
set IlcUseEnvironmentalTools=true
dotnet publish -p:PublishAot=true
```

## 命令

```text
Portable MSVC 工具链提取器

用法：
  PortableMSVC <命令> [参数...]

命令：
  list     列出可用的 MSVC / SDK 版本
  plan     生成安装计划（不下载，输出 JSON）
  install  下载并安装工具链
  cache    管理本地 manifest 缓存
```

常用参数：

```text
--vs <版本>           Visual Studio 版本：latest（默认）| 2026 | 2022 | 2019
--vc <版本>           MSVC 工具版本，如 14.44、14.50（默认：最新）
--sdk <版本>          Windows SDK 版本，如 26100、22621（默认：最新）
--redist <版本>       MSVC redist 版本（默认：跟随 --vc）
--host <架构>         编译器 host 架构：x64（默认）| x86 | arm64
--target <架构>       编译目标架构：x64（默认）| x86 | arm | arm64
--output <目录>       安装输出目录（默认：exe 同目录下的 MSVC\）
--cache <目录>        指定本地 manifest 缓存目录，跳过联网检查
--copy-runtime-dlls   复制运行/调试 DLL 到编译器 bin 目录
--with-runtime        下载 VC runtime / debug runtime 官方安装包
--dry-run             仅生成安装计划，不执行下载和安装
```

`--target` 可以重复写，也可以用逗号分隔：

```powershell
PortableMSVC.exe install --vs 2022 --target x64 x86
PortableMSVC.exe install --vs 2022 --target x64,x86
```

## 架构支持

| VS 版本 | host | target |
| --- | --- | --- |
| 2019 | x86, x64 | x86, x64, arm, arm64 |
| 2022 | x86, x64, arm64 | x86, x64, arm, arm64 |
| 2026/latest | x86, x64, arm64 | x86, x64, arm64 |

`arm` 表示 ARM32，只能作为 target，不能作为 host。Windows SDK 26100 已移除 ARM32 Desktop Libs，因此 `--sdk 26100 --target arm` 会失败。

## 输出目录

默认输出结构：

```text
MSVC\
  BuildTools\
    VC\
    Common7\
  Runtime\
    x64\
    x86\
    arm64\
  Scripts\
  Windows Kits\
    10\
  VisualStudio\
    Installer\
      vswhere.exe
      vswhere.bat
    Packages\
      state.json
    Setup\
      status.json
  Setup.bat
  Clean.bat
```

其中：

- `BuildTools\` 是便携的 VS Build Tools 根目录
- `Windows Kits\10\` 是便携 Windows SDK 根目录
- `VisualStudio\Installer\vswhere.exe` 是 fake vswhere
- `VisualStudio\Packages\state.json` 是 fake vswhere 使用的 portable metadata
- `Setup.bat` / `Clean.bat` 用于注册和回滚系统兼容层

## fake vswhere

安装时会把当前 Native AOT 单文件复制为：

```text
MSVC\VisualStudio\Installer\vswhere.exe
```

该 fake `vswhere.exe` 不依赖原版 Visual Studio Installer、COM 或注册表，只读取 portable `state.json`，并支持常见工具查询：

```bat
vswhere.exe -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
vswhere.exe -products * -format json -utf8
vswhere.exe -products * -format json -utf8 -find **\clang-cl.exe
vswhere.exe -format json
```

查看 fake vswhere 帮助：

```bat
MSVC\VisualStudio\Installer\vswhere.exe help
```

## setup / clean 兼容层

某些工具只会从系统固定位置查找 `vswhere.exe` 或读取 Windows SDK 注册表。可以用：

```bat
MSVC\Setup.bat
```

它会尝试创建系统 Installer junction：

```text
%ProgramFiles(x86)%\Microsoft Visual Studio\Installer -> <portable>\VisualStudio\Installer
```

并写入 Windows SDK 注册表：

```text
HKLM\SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0 / InstallationFolder
HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots / KitsRoot10
```

回滚：

```bat
MSVC\Clean.bat
```

状态保存在：

```text
MSVC\VisualStudio\Setup\status.json
```

`Clean.bat` 只会回滚仍指向当前 portable 目录的 junction 和注册表值。

## Runtime 安装包

默认安装只解包工具链所需文件，不自动安装系统 VC runtime。

如需额外下载官方 runtime 安装器：

```powershell
PortableMSVC.exe install --vs 2022 --target x64 x86 --with-runtime
```

输出示例：

```text
MSVC\Runtime\x64\
  VC_redist.x64.exe
  vc_RuntimeDebug.msi
  cab1.cab
  Install_vc_RuntimeDebug.bat
```

`--with-runtime` 只下载 payload，不自动安装。debug runtime 的 MSI/CAB 同时存在时，会生成 `Install_vc_RuntimeDebug.bat` 方便手动安装。

如果只想把运行/调试 DLL 复制到编译器 bin 目录：

```powershell
PortableMSVC.exe install --vs 2022 --target x64 --copy-runtime-dlls
```

已有目标文件不会被覆盖。

## Manifest 缓存

默认使用 Visual Studio 官方 stable/release channel：

| VS 别名 | channel |
| --- | --- |
| latest | `https://aka.ms/vs/stable/channel` |
| 2026 | `https://aka.ms/vs/18/stable/channel` |
| 2022 | `https://aka.ms/vs/17/release/channel` |
| 2019 | `https://aka.ms/vs/16/release/channel` |

查看缓存状态：

```powershell
PortableMSVC.exe cache status
```

刷新缓存：

```powershell
PortableMSVC.exe cache refresh
PortableMSVC.exe cache refresh --vs 2022 --force
```

离线使用本地 manifest：

```powershell
PortableMSVC.exe list --vs 2022 --cache Cache\manifests
PortableMSVC.exe plan --vs 2022 --vc 14.44 --sdk 26100 --host x64 --target x64 x86 --cache Cache\manifests
```

指定 `--cache` 后会直接读取本地 `{vs}.vsman.json`，不触发联网检查。

## 开发

运行测试：

```powershell
dotnet test
```

常用离线验证：

```powershell
dotnet run --project PortableMSVC.csproj -- list --vs 2022 --cache Cache\manifests
dotnet run --project PortableMSVC.csproj -- plan --vs 2022 --vc 14.44 --sdk 26100 --host x64 --target x64 x86 --cache Cache\manifests
dotnet run --project PortableMSVC.csproj -- plan --vs 2019 --vc 14.29.16.10 --sdk 22621 --host x64 --target x64 --cache Cache\manifests
```

Native AOT / trimming 注意事项：

- JSON 序列化使用 `System.Text.Json` 源生成上下文
- 新增需要序列化或反序列化的类型时，请同步更新 `JsonSourceGenerationContext.cs`
- 不要依赖运行时反射序列化，否则 Native AOT publish 后可能被裁剪

## 注意事项

- PortableMSVC 下载和解包的是 Microsoft 官方 Visual Studio / Windows SDK payload；使用这些文件时仍需遵守对应 Microsoft 许可条款。
- 不要把 DLL 复制到系统目录，也不要注册 COM。
- Windows SDK 注册表只应通过 `Setup.bat` / `Clean.bat` 管理，并保留回滚状态。
- 修改包选择或清理逻辑时，应补充对应矩阵测试，避免破坏 `vcvars*.bat`、CMake、Qt Creator 或 Native AOT 探测链。
