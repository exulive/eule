using System.Windows;
using System.Windows.Threading;
using Eule.App.Controls;
using Eule.App.Theme;
using Eule.Core.Cloud;
using Eule.Core.Localization;
using Serilog;

namespace Eule.App.Views;

/// <summary>Sign in with the Eule account. "Not now" closes it; breaks keep working without an account.</summary>
public partial class LoginWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly AppHost host;

    public LoginWindow(AppHost host)
    {
        this.host = host;
        InitializeComponent();
        Owl.Accent = ThemeService.Brush("Eule.Sit");
        EmailBox.Text = host.Settings.LastEmail ?? "";

        SubmitButton.Click += async (_, _) => await SubmitAsync();
        LaterButton.Click += (_, _) => Close();
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(EmailBox.Text)) EmailBox.Focus(); else PasswordBox.Focus();
        };
    }

    /// <summary>True when the window closed after a successful sign-in.</summary>
    public bool SignedIn { get; private set; }

    async Task SubmitAsync()
    {
        string email = EmailBox.Text.Trim();
        string password = PasswordBox.Password ?? "";
        if (email.Length == 0 || password.Length == 0)
        {
            ShowError(L.T("login.missing"));
            return;
        }

        SetBusy(true);
        try
        {
            var account = await host.Api.LoginAsync(email, password, Environment.MachineName);
            host.SaveUiState(s => s.LastEmail = account.Email);
            Log.Information("Giriş yapıldı: {Email}", account.Email);
            SignedIn = true;
            ErrorText.Visibility = Visibility.Collapsed;
            Owl.Mood = OwlMood.Awake;
            Owl.Nod();
            // A short moment for the nod, then out of the way.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
        }
        catch (ApiException ex)
        {
            Owl.Shake();
            ShowError(L.T(ex.Kind switch
            {
                ApiError.InvalidCredentials => "login.errInvalid",
                ApiError.Network => "login.errNetwork",
                ApiError.RateLimited => "login.errRate",
                _ => "login.errServer",
            }));
            if (ex.Kind == ApiError.InvalidCredentials) { PasswordBox.Password = ""; PasswordBox.Focus(); }
        }
        finally
        {
            if (!SignedIn) SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        Owl.Mood = busy ? OwlMood.Thinking : OwlMood.Awake;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = busy ? "" : L.T("login.submit");
        SubmitButton.IsEnabled = EmailBox.IsEnabled = PasswordBox.IsEnabled = LaterButton.IsEnabled = !busy;
    }

    void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
