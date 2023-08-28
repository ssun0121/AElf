using System;
using System.Threading.Tasks;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Configuration;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;

namespace AElf.Kernel.FeatureManager;

public class FeatureActiveService : IFeatureActiveService, ITransientDependency
{
    private readonly IBlockchainService _blockchainService;
    private readonly IConfigurationService _configurationService;

    public FeatureActiveService(IConfigurationService configurationService, IBlockchainService blockchainService)
    {
        _configurationService = configurationService;
        _blockchainService = blockchainService;
        Logger = NullLogger<FeatureActiveService>.Instance;
    }
    public ILogger<FeatureActiveService> Logger { get; set; }


    public async Task<bool> IsFeatureActive(string featureName)
    {
        var featureConfigurationName = GetFeatureConfigurationName(featureName);
        var chain = await _blockchainService.GetChainAsync();
        try
        {
            var activeHeightByteString = await _configurationService.GetConfigurationDataAsync(featureConfigurationName,
                new ChainContext
                {
                    BlockHeight = chain.BestChainHeight,
                    BlockHash = chain.BestChainHash
                });
            if (activeHeightByteString == null)
            {
                return false;
            }
            var activeHeight = new Int64Value();
            activeHeight.MergeFrom(activeHeightByteString);
            if (activeHeight.Value == 0) return false;

            return chain.BestChainHeight >= activeHeight.Value;
        }
        catch (Exception e)
        {
            Logger.LogTrace("Get Configuration failed.{message}",e.Message);
            return false;
        }
        
    }

    private string GetFeatureConfigurationName(string featureName)
    {
        return $"{FeatureManagerConstants.FeatureConfigurationNamePrefix}{featureName}";
    }
}