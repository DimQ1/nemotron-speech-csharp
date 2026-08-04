# Aspire Dashboard — Development Telemetry

> ⚠️ **Aspire workload deprecated.** Начиная с .NET 10 Aspire доступен через NuGet-пакеты.
> Подробнее: https://aka.ms/aspire/support-policy

## OpenTelemetry (уже настроено)

Приложение уже экспортирует логи, метрики и трейсы через **OpenTelemetry Protocol (OTLP)** gRPC
на `http://localhost:4317` в Development-режиме.

Для просмотра телеметрии можно использовать любой OTLP-совместимый бэкенд:

- **Aspire Dashboard** — рекомендуемый вариант
- **Jaeger** — для трейсов
- **Prometheus + Grafana** — для метрик
- **Seq / Logstash** — для логов

## Запуск Aspire Dashboard

### Способ 1: Через Aspire.Hosting AppHost (рекомендуемый)

Создайте проект `.AppHost` рядом с `VoiceType.WinUI`:

```powershell
dotnet new aspire --name VoiceType.AppHost
cd VoiceType.AppHost
dotnet add reference ../VoiceType.WinUI/VoiceType.WinUI.csproj
```

В `Program.cs` добавьте:

```csharp
var builder = DistributedApplication.CreateBuilder(args);
builder.AddProject<Projects.VoiceType_WinUI>("voicetype");
builder.Run();
```

Запуск:

```powershell
dotnet run --project VoiceType.AppHost
```

### Способ 2: Через Aspire.Dashboard SDK

```powershell
# Установить SDK (замените на вашу платформу)
dotnet add package Aspire.Dashboard.Sdk.win-x64 --version 13.4.6
```

Затем запустить дашборд через `aspire-dashboard` CLI (документация: https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone).

### Способ 3: Docker

```powershell
docker run --rm -it -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Дашборд: `http://localhost:18888`

## Настройка приложения

Выберите профиль **"VoiceType + Aspire Dashboard"** в VS Code (Run & Debug):

```json
{
  "commandName": "MsixPackage",
  "environmentVariables": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317"
  }
}
```

## Автономный режим

Без Aspire телеметрия работает без ошибок:
- Все логи пишутся в `%LOCALAPPDATA%\VoiceType\error.log`
- Логи через `ILogger<T>` + `Debug.WriteLine`
- OTLP-экспорт автоматически отключается (`IsOtlpExportEnabled()` = false)

## Переменные окружения

| Переменная | По умолчанию | Описание |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` | gRPC endpoint OTLP |
| `ASPIRE_DASHBOARD_OTLP_ENDPOINT` | — | Альтернативный endpoint (legacy) |
| `ASPNETCORE_ENVIRONMENT` | — | `Development` включает OTLP-экспорт |