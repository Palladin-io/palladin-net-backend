using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Modules;

public interface IModule
{
    public string Name { get; }
    public bool IsEnabled { get; }

    public IServiceCollection ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
