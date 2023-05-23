
using Microsoft.Extensions.Configuration;

namespace LiveVoxAgentSummary
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                 .SetBasePath(Directory.GetCurrentDirectory())
                 .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            IConfigurationRoot config = configuration.Build();
            await AgentSummary.GetAgentSummary(config);
        }
    }
}