# Aspire Dashboard — Development Telemetry

## Запуск Aspire Dashboard

```powershell
# Установить глобальный инструмент Aspire Dashboard
dotnet tool install -g Aspire.Dashboard

# Запустить дашборд
aspire-dashboard
```

Дашборд будет доступен по адресу: `http://localhost:18888`

## Настройка приложения

### Вариант 1: Launch Profile

Выберите профиль **"VoiceType + Aspire Dashboard"** в VS Code (Run & Debug → профиль):
- Устанавливает `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`
- Включает `ASPNETCORE_ENVIRONMENT=Development`

### Вариант 2: Ручной запуск

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project VoiceType.WinUI/VoiceType.WinUI.csproj -c Debug -p:WindowsPackageType=None
```

## Что вы увидите в дашборде

| Раздел | Что отображается |
|---|---|
| **Logs** | Все логи через `ISystemTelemetry` и `ILogger<T>` |
| **Traces** | Инструментированные HTTP-запросы и операции |
| **Metrics** | Счётчики событий и ошибок |
| **Structures** | Структурированные данные телеметрии |

## Переменные окружения

| Переменная | По умолчанию | Описание |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` | gRPC endpoint OTLP |
| `ASPIRE_DASHBOARD_OTLP_ENDPOINT` | — | Альтернативный endpoint |
| `ASPNETCORE_ENVIRONMENT` | — | `Development` включает OTLP-экспорт |