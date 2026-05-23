# 打包发布命令

以下命令均在仓库根目录执行。

## 仅生成发布包

```powershell
..\.dotnet\dotnet.exe publish .\CelesteAutoCut\CelesteAutoCut.csproj -c Release
```

该命令会构建内置 OBS helper payload，并生成：

- `CelesteAutoCut.zip`
- `artifacts/release/CelesteAutoCut.zip`
- `artifacts/publish/`

## 常用验证命令

```powershell
..\.dotnet\dotnet.exe run --project .\ObsClipSidecar\ObsClipSidecar.csproj -- self-test
..\.dotnet\dotnet.exe build .\ObsClipPanel\ObsClipPanel.csproj -c Release
..\.dotnet\dotnet.exe publish .\CelesteAutoCut\CelesteAutoCut.csproj -c Release
```

## 安装到本机 Celeste Mods 目录

```powershell
Copy-Item -LiteralPath .\CelesteAutoCut.zip -Destination 'D:\Steam\steamapps\common\Celeste\Mods\CelesteAutoCut.zip' -Force
```

## 校验 Mods 目录中的 zip 是否一致

```powershell
$local = Get-FileHash -Algorithm SHA256 -LiteralPath .\CelesteAutoCut.zip
$installed = Get-FileHash -Algorithm SHA256 -LiteralPath 'D:\Steam\steamapps\common\Celeste\Mods\CelesteAutoCut.zip'
$local.Hash -eq $installed.Hash
```
