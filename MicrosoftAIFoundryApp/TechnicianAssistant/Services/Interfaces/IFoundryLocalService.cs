using System;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Interfaces
{
    public interface IFoundryLocalService
    {
        string Endpoint { get; }
        bool IsModelsReady { get; }
        event EventHandler? ModelsReady;
        Task InitializeAsync();
        Task<string> GetEndpointAsync();
        Task<string> LoadModelAsync(string modelName);
        Task ShutdownAsync();
        void SetLogger(Action<string> logger);
    }
}
