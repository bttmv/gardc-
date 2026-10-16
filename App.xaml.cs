using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace WifiManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WifiManagerCS");
        System.IO.Directory.CreateDirectory(dir);
        Db.Init(System.IO.Path.Combine(dir, "data.db"));
        var cfg = Config.Load();

        if (!string.IsNullOrEmpty(cfg.AppPassHash) && !AskPassword(cfg.AppPassHash))
        {
            Shutdown();
            return;
        }

        var win = new WifiManager.MainWindow(cfg);
        MainWindow = win;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        win.Show();
    }

    static bool AskPassword(string hash)
    {
        for (int i = 0; i < 3; i++)
        {
            var w = new Window
            {
                Title = "كلمة مرور البرنامج", Width = 320, Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                FlowDirection = FlowDirection.RightToLeft, ResizeMode = ResizeMode.NoResize
            };
            var pb = new PasswordBox { Margin = new Thickness(12) };
            var ok = new Button { Content = "دخول", Margin = new Thickness(12, 0, 12, 12), IsDefault = true };
            ok.Click += (_, _) => w.DialogResult = true;
            pb.Loaded += (_, _) => pb.Focus();
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "أدخل كلمة المرور", Margin = new Thickness(12, 12, 12, 0) });
            sp.Children.Add(pb);
            sp.Children.Add(ok);
            w.Content = sp;
            if (w.ShowDialog() != true) return false;
            if (Config.VerifyHash(pb.Password, hash)) return true;
            MessageBox.Show("كلمة المرور غير صحيحة");
        }
        return false;
    }
}
