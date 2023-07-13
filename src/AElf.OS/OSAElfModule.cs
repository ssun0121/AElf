﻿using System;
using AElf.Kernel;
using AElf.Modularity;
using AElf.OS.BlockSync;
using AElf.OS.BlockSync.Worker;
using AElf.OS.Network;
using AElf.OS.Network.Grpc;
using AElf.OS.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.BackgroundWorkers;
using Volo.Abp.Modularity;

namespace AElf.OS;

[DependsOn(
    typeof(AbpBackgroundWorkersModule),
    typeof(KernelAElfModule),
    typeof(CoreOSAElfModule),
    typeof(GrpcNetworkModule)
)]
public class OSAElfModule : AElfModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();

        var address = Environment.GetEnvironmentVariable("AELF_ADDRESS");
        var password = Environment.GetEnvironmentVariable("AELF_PASSWORD"); 
        if (string.IsNullOrEmpty(address)) 
        { 
            Configure<AccountOptions>(configuration.GetSection("Account"));
        }
        else 
        { 
            Configure<AccountOptions>(option => 
            { 
                option.NodeAccount = address; 
                option.NodeAccountPassword = password;
            });
        }
        Configure<BlockSyncOptions>(configuration.GetSection("BlockSync"));
    }

    public override void OnPreApplicationInitialization(ApplicationInitializationContext context)
    {
        var taskQueueManager = context.ServiceProvider.GetService<ITaskQueueManager>();

        if (taskQueueManager != null)
        {
            taskQueueManager.CreateQueue(OSConstants.BlockSyncAttachQueueName);
            taskQueueManager.CreateQueue(OSConstants.BlockFetchQueueName, 4);
            taskQueueManager.CreateQueue(OSConstants.InitialSyncQueueName);
        }

        var backgroundWorkerManager = context.ServiceProvider.GetRequiredService<IBackgroundWorkerManager>();

        var networkOptions = context.ServiceProvider.GetService<IOptionsSnapshot<NetworkOptions>>()!.Value;
        if (networkOptions.EnablePeerDiscovery)
        {
            var peerDiscoveryWorker = context.ServiceProvider.GetService<PeerDiscoveryWorker>();
            backgroundWorkerManager.AddAsync(peerDiscoveryWorker);
        }

        backgroundWorkerManager.AddAsync(context.ServiceProvider.GetService<BlockDownloadWorker>());
        backgroundWorkerManager.AddAsync(context.ServiceProvider.GetService<PeerReconnectionWorker>());
    }
}