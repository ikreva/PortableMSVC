# UPX

Place `upx.exe` in this directory to enable automatic publish-time compression:

```text
tools\upx\upx.exe
```

`PortableMSVC.csproj` runs it after publish with:

```powershell
upx.exe --best PortableMSVC.exe
```

The binary is intentionally not downloaded by the build.
