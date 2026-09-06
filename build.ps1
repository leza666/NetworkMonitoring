# 构建辅助脚本（dotnet 不在 PATH 时用）
$dotnet = "C:\Program Files\dotnet\dotnet.exe"

switch ($args[0]) {
    "build" {
        & $dotnet build src\NetworkMonitoring
    }
    "run" {
        $extra = $args[1..($args.Count - 1)]
        & $dotnet run --project src\NetworkMonitoring -- $extra
    }
    "publish" {
        & $dotnet publish src\NetworkMonitoring -c Release -o dist -r win-x64 --self-contained false
        Write-Host "已发布到 dist\（框架依赖，需本机已装 .NET 10 运行时）"
    }
    default {
        Write-Host "用法:"
        Write-Host "  .\build.ps1 build             编译 Debug"
        Write-Host "  .\build.ps1 run collect       前台采集 + 看板"
        Write-Host "  .\build.ps1 run report [日期|--all]"
        Write-Host "  .\build.ps1 run install       注册服务（需管理员）"
        Write-Host "  .\build.ps1 publish           发布 Release"
    }
}