using System.Configuration;
using System.Data;
using System.Windows;

namespace ProxyControl
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // Важно: нужно корректно остановить прокси при выходе, 
            // чтобы вернуть системные настройки интернета.
            // В идеале ViewModel должна вызывать Stop() у сервиса.
            base.OnExit(e);
        }
    }

}
