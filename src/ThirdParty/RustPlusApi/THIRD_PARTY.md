# RustPlusApi (исходники сторонней библиотеки)

- Проект: https://github.com/HandyS11/RustPlusApi (лицензия MIT, файл LICENSE рядом)
- Ветка `feat/companion-socket`, коммит `ad4310583ee2c7c6a6dbd6b412df382ef92d461b` (2026-11-05)
- Что даёт эта ветка: `RustPlusConnection.ServerId` + `AuthToken` → подключение через `wss://companion-rust.facepunch.com/socket/{ServerId}`,
  токен входа Facepunch передаётся WebSocket-субпротоколами `rustplus.auth.v1` и base64url(токен) (файл `RustPlusEndpoint.cs`).
  В опубликованном пакете 2.0.0-beta.9 этого нет, а старый маршрут `/game/{ip}/{port}` отвечает HTTP 418.
- Скопированы все `*.cs` и `Protobuf/RustPlusContracts.proto` из `src/RustPlusApi` без правок; не скопированы netstandard-полифилы (нужны только для netstandard2.0), анализаторы и `InternalsVisibleTo` репозитория.
- Это НЕ документация Facepunch: протокол восстановлен по коду сторонних клиентов (см. docs/RUSTPLUS.md).
