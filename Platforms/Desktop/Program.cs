using System.Threading.Tasks;
using Uno.UI.Hosting;

namespace WinIconFinder;

internal class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWin32()
            .UseX11()
            .UseMacOS()
            .Build();

        await host.RunAsync();
    }
}
