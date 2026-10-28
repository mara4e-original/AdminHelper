# Как выпускать обновления

1. В `Updater.cs` впишите свой репозиторий: `Repo = "ник/AdminHelper"` (один раз).
2. Поднимите версию в `AdminHelper.csproj` (<Version>) и в `setup.iss` (MyAppVersion), например 1.3.0.
3. Запустите `build-installer.bat` -> появится `installer\AdminHelper-Setup.exe`.
4. На GitHub: Releases -> Draft a new release.
   - Tag: `v1.3.0` (должен совпадать с версией)
   - Описание: что нового (покажется в программе)
   - Прикрепите файл `AdminHelper-Setup.exe` (имя должно заканчиваться на `-Setup.exe`)
   - Publish release (не draft и не pre-release)

Программа проверяет `https://api.github.com/repos/<Repo>/releases/latest` через 4 секунды после
запуска и по кнопке во вкладке «Обновления». Репозиторий должен быть публичным.
