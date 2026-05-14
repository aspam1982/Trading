# CredentialsEditor

`CredentialsEditor` - WPF-редактор записей Windows Credential Manager для generic credentials,
используемых проектами решения `Trading`.

## Возможности

- Поиск ключей по `UserName` с SQL-like фильтром.
- Фильтр по умолчанию: `%username%`.
- Просмотр `TargetName`, `UserName`, даты изменения и значения секрета.
- Добавление нового ключа.
- Изменение выбранного ключа.
- `TargetName` выбранного ключа защищен от изменения; при добавлении новой записи `UserName` по умолчанию равен `username`.
- Удаление выбранного ключа после подтверждения.

## Технические детали

Проект использует `CommonClasses.WindowsCredentialManager`. Для поддержки списка ключей в
`CommonClasses` добавлен метод `ListCredentialsByUserName`, для сохранения с отдельным
`UserName` - перегрузка `WriteSecret(targetName, userName, secret)`, для удаления -
метод `DeleteSecret(targetName)`.
