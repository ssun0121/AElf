using System.Threading.Tasks;
using AElf.Kernel;
using AElf.OS.BlockSync.Domain;
using AElf.OS.BlockSync.Dto;
using AElf.OS.BlockSync.Infrastructure;
using AElf.OS.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AElf.OS.BlockSync.Application;

public class BlockSyncService : IBlockSyncService
{
    private readonly IAnnouncementCacheProvider _announcementCacheProvider;
    private readonly IBlockDownloadJobManager _blockDownloadJobManager;
    private readonly IBlockFetchService _blockFetchService;
    private readonly IBlockSyncAttachService _blockSyncAttachService;
    private readonly IBlockSyncQueueService _blockSyncQueueService;

    public BlockSyncService(IBlockFetchService blockFetchService,
        IBlockSyncAttachService blockSyncAttachService,
        IBlockSyncQueueService blockSyncQueueService,
        IBlockDownloadJobManager blockDownloadJobManager, IAnnouncementCacheProvider announcementCacheProvider)
    {
        Logger = NullLogger<BlockSyncService>.Instance;

        _blockFetchService = blockFetchService;
        _blockSyncAttachService = blockSyncAttachService;
        _blockSyncQueueService = blockSyncQueueService;
        _blockDownloadJobManager = blockDownloadJobManager;
        _announcementCacheProvider = announcementCacheProvider;
    }

    public ILogger<BlockSyncService> Logger { get; set; }

    public async Task SyncByAnnouncementAsync(Chain chain, SyncAnnouncementDto syncAnnouncementDto)
    {
        Logger.LogDebug($"Sync by announcement and start to fetch block best chain height:{chain.BestChainHeight},longest chain height:{chain.LongestChainHeight}");
        if (syncAnnouncementDto.SyncBlockHash != null && syncAnnouncementDto.SyncBlockHeight <=
            chain.LongestChainHeight + BlockSyncConstants.BlockSyncModeHeightOffset)
        {
            if (!_blockSyncQueueService.ValidateQueueAvailability(OSConstants.BlockFetchQueueName))
            {
                Logger.LogWarning("Block sync fetch queue is too busy.");
                return;
            }
            Logger.LogDebug($"Start to enqueue fetch block [Sync by announcement].{syncAnnouncementDto.SyncBlockHeight}-{syncAnnouncementDto.BatchRequestBlockCount}");
            EnqueueFetchBlockJob(syncAnnouncementDto);
            Logger.LogDebug($"End to enqueue fetch block [Sync by announcement].{syncAnnouncementDto.SyncBlockHeight}-{syncAnnouncementDto.BatchRequestBlockCount}");

        }
        else
        {
            Logger.LogDebug($"Start to enqueue block download job [Sync by announcement].{syncAnnouncementDto.SyncBlockHeight}-{syncAnnouncementDto.BatchRequestBlockCount}-{syncAnnouncementDto.SuggestedPeerPubkey}");
            await _blockDownloadJobManager.EnqueueAsync(syncAnnouncementDto.SyncBlockHash, syncAnnouncementDto
                    .SyncBlockHeight,
                syncAnnouncementDto.BatchRequestBlockCount, syncAnnouncementDto.SuggestedPeerPubkey);
            Logger.LogDebug($"End to enqueue block download job [Sync by announcement].{syncAnnouncementDto.SyncBlockHeight}-{syncAnnouncementDto.BatchRequestBlockCount}-{syncAnnouncementDto.SuggestedPeerPubkey}");
        }
    }

    public async Task SyncByBlockAsync(Chain chain, SyncBlockDto syncBlockDto)
    {
        if (syncBlockDto.BlockWithTransactions.Height <=
            chain.LongestChainHeight + BlockSyncConstants.BlockSyncModeHeightOffset)
        {
            Logger.LogDebug($"Start to attach block job [Sync by block].chain longest height:{chain.LongestChainHeight}-peer pubkey:{syncBlockDto.SuggestedPeerPubkey}");
            EnqueueAttachBlockJob(syncBlockDto.BlockWithTransactions, syncBlockDto.SuggestedPeerPubkey);
            Logger.LogDebug($"End to attach block job [Sync by block].chain longest height:{chain.LongestChainHeight}-peer pubkey:{syncBlockDto.SuggestedPeerPubkey}");
        }
        else
        {
            Logger.LogDebug($"Start to enqueue block download job [Sync by block].{syncBlockDto.BlockWithTransactions}-{syncBlockDto.BatchRequestBlockCount}-{syncBlockDto.SuggestedPeerPubkey}");
            await _blockDownloadJobManager.EnqueueAsync(syncBlockDto.BlockWithTransactions.GetHash(),
                syncBlockDto.BlockWithTransactions.Height,
                syncBlockDto.BatchRequestBlockCount, syncBlockDto.SuggestedPeerPubkey);
            Logger.LogDebug($"End to enqueue block download job [Sync by block].{syncBlockDto.BlockWithTransactions}-{syncBlockDto.BatchRequestBlockCount}-{syncBlockDto.SuggestedPeerPubkey}");
        }
        
    }

    private void EnqueueFetchBlockJob(SyncAnnouncementDto syncAnnouncementDto)
    {
        _blockSyncQueueService.Enqueue(async () =>
        {
            Logger.LogDebug(
                $"Block sync: Fetch block, block height: {syncAnnouncementDto.SyncBlockHeight}, block hash: {syncAnnouncementDto.SyncBlockHash}.");

            var fetchResult = false;
            if (ValidateQueueAvailability())
            {
                Logger.LogDebug($"Start to fetch block.{syncAnnouncementDto.SyncBlockHash}-{syncAnnouncementDto.SyncBlockHeight}");
                fetchResult = await _blockFetchService.FetchBlockAsync(syncAnnouncementDto.SyncBlockHash,
                    syncAnnouncementDto.SyncBlockHeight, syncAnnouncementDto.SuggestedPeerPubkey);
                Logger.LogDebug($"End to fetch block.{syncAnnouncementDto.SyncBlockHash}-{syncAnnouncementDto.SyncBlockHeight}");

            }
            
            if (fetchResult)
            {
                Logger.LogDebug($"Fetch block success,block hash:{syncAnnouncementDto.SyncBlockHash}-block height:{syncAnnouncementDto.SyncBlockHeight}");
                return;
            }
                
            if (_announcementCacheProvider.TryGetAnnouncementNextSender(syncAnnouncementDto.SyncBlockHash,
                    out var senderPubKey))
            {
                Logger.LogDebug($"Fetch block failed {syncAnnouncementDto.SuggestedPeerPubkey},try to fetch from next sender {senderPubKey}");
                syncAnnouncementDto.SuggestedPeerPubkey = senderPubKey;
                Logger.LogDebug("Enqueue fetch block next sender.");
                EnqueueFetchBlockJob(syncAnnouncementDto);
            }
        }, OSConstants.BlockFetchQueueName);
    }

    private void EnqueueAttachBlockJob(BlockWithTransactions blockWithTransactions, string senderPubkey)
    {
        _blockSyncQueueService.Enqueue(async () =>
        {
            Logger.LogDebug($"Block sync: sync block, block: {blockWithTransactions}.");
            Logger.LogDebug($"Start to attach block [EnqueueAttachBlockJob].{blockWithTransactions}");
            await _blockSyncAttachService.AttachBlockWithTransactionsAsync(blockWithTransactions, senderPubkey);
            Logger.LogDebug($"End to attach block [EnqueueAttachBlockJob].{blockWithTransactions}");
        }, OSConstants.BlockSyncAttachQueueName);
    }

    private bool ValidateQueueAvailability()
    {
        if (!_blockSyncQueueService.ValidateQueueAvailability(OSConstants.BlockSyncAttachQueueName))
        {
            Logger.LogWarning("Block sync attach queue is too busy.");
            return false;
        }

        if (!_blockSyncQueueService.ValidateQueueAvailability(KernelConstants.UpdateChainQueueName))
        {
            Logger.LogWarning("Block sync attach and execute queue is too busy.");
            return false;
        }

        return true;
    }
}