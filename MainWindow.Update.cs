using System.Windows;
using System.Windows.Media;

namespace AdminHelper;

public partial class MainWindow
{
    private UpdateInfo? _update;
    private string? _updFile;
    private bool _updBusy;

    private void InitUpdates()
    {
        UpdCurrent.Text = $"Установленная версия: {Updater.CurrentVersion.ToString(3)}";
        UpdInstallBtn.Visibility = Visibility.Collapsed;
        UpdStatusText.Text = "Проверка ещё не выполнялась";
    }

    private async Task AutoCheckUpdateAsync()
    {
        await Task.Delay(4000);   // не мешаем заставке и запуску
        await CheckUpdateAsync(silent: true);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync(silent: false);

    private async Task CheckUpdateAsync(bool silent)
    {
        if (_updBusy) return;
        if (!Updater.IsConfigured)
        {
            if (!silent) UpdStatusText.Text = "Не указан репозиторий GitHub (см. Updater.cs → Repo)";
            return;
        }

        _updBusy = true;
        UpdCheckBtn.IsEnabled = false;
        if (!silent) UpdStatusText.Text = "Проверяю обновления...";

        try
        {
            var info = await Updater.GetLatestAsync();
            UpdBar.Value = 0;

            if (info == null)
            {
                if (!silent) UpdStatusText.Text = "Релизов на GitHub пока нет";
            }
            else if (Updater.IsNewer(info))
            {
                _update = info;
                UpdStatusText.Text = $"Доступна версия {info.Version.ToString(3)}";
                UpdNotes.Text = string.IsNullOrWhiteSpace(info.Notes) ? "(описание не указано)" : info.Notes.Replace("\r\n", "\n");
                UpdInstallBtn.Content = $"Скачать и установить {info.Version.ToString(3)}";
                UpdInstallBtn.Visibility = info.AssetUrl != null ? Visibility.Visible : Visibility.Collapsed;
                if (info.AssetUrl == null) UpdNotes.Text = "В релизе нет файла установщика (*-Setup.exe).\n\n" + UpdNotes.Text;

                NavUpdText.Text = "Обновления  ●";
                NavUpdText.Foreground = (Brush)FindResource("GoodBrush");
            }
            else
            {
                _update = null;
                UpdInstallBtn.Visibility = Visibility.Collapsed;
                UpdNotes.Text = "";
                UpdStatusText.Text = "У вас последняя версия";
                NavUpdText.Text = "Обновления";
            }
        }
        catch (Exception ex)
        {
            if (!silent) UpdStatusText.Text = "Не удалось проверить: " + ex.Message;
        }
        finally
        {
            UpdCheckBtn.IsEnabled = true;
            _updBusy = false;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updBusy || _update == null) return;
        _updBusy = true;
        UpdCheckBtn.IsEnabled = false;
        UpdInstallBtn.IsEnabled = false;

        try
        {
            UpdStatusText.Text = "Скачиваю обновление...";
            var progress = new Progress<double>(p =>
            {
                UpdBar.Value = p;
                UpdStatusText.Text = $"Скачиваю обновление... {p:0}%";
            });

            _updFile = await Updater.DownloadAsync(_update, progress);
            UpdBar.Value = 100;
            UpdStatusText.Text = "Запускаю установщик...";

            var ask = MessageBox.Show(this,
                "Программа закроется, обновится и запустится снова. Продолжить?",
                "Обновление", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK)
            {
                UpdStatusText.Text = "Обновление отложено (файл скачан)";
                UpdInstallBtn.IsEnabled = true;
                return;
            }

            Updater.RunInstaller(_updFile);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdStatusText.Text = "Ошибка обновления: " + ex.Message;
            UpdInstallBtn.IsEnabled = true;
        }
        finally
        {
            UpdCheckBtn.IsEnabled = true;
            _updBusy = false;
        }
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string url = _update?.PageUrl is { Length: > 0 } u ? u : $"https://github.com/{Updater.Repo}/releases";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
