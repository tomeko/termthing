using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Renci.SshNet;
using TermThing.Views;

namespace TermThing;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        BindingPlugins.DataValidators.RemoveAt(0);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

        // Pre-JIT SSH.NET crypto before the first real connection.
        // Constructing a ConnectionInfo runs all key-exchange, cipher and HMAC
        // static initializers, shaving ~500-1500ms off the first connect attempt.
        // Instantiating the preferred kex algorithm objects additionally triggers
        // BouncyCastle's X25519 static constructors (the actual hot crypto path).
        _ = Task.Run(static () =>
        {
            try
            {
                var info = new ConnectionInfo("localhost", "warmup", new NoneAuthenticationMethod("warmup"));
                // Instantiate the two most likely kex algorithms to run their static ctors.
                foreach (var key in new[] { "curve25519-sha256", "ecdh-sha2-nistp256" })
                    if (info.KeyExchangeAlgorithms.TryGetValue(key, out var factory))
                        _ = factory();
            }
            catch { }
        });
    }
}
